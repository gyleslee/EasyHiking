using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using HikingGuide.App.Services;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Routing;
using Microsoft.Win32;

namespace HikingGuide.App;

public partial class BRouterImportWindow : Window
{
    private readonly BRouterController _controller;
    private readonly ObservableCollection<ImportRow> _rows = [];
    private CancellationTokenSource? _cancellation;
    private readonly Stopwatch _watch = new();

    public BRouterImportWindow(BRouterController controller)
    {
        InitializeComponent();
        _controller = controller;
        FileList.ItemsSource = _rows;
        Loaded += (_, _) => ChooseFiles();
        _controller.ImportProgressChanged += OnProgress;
        Closed += (_, _) =>
        {
            _controller.ImportProgressChanged -= OnProgress;
            _cancellation?.Cancel();
        };
    }

    private void ChooseFiles()
    {
        if (_cancellation is not null) return;
        var picker = new OpenFileDialog
        {
            Title = "选择已有BRouter路由数据",
            Filter = "BRouter路由数据 (*.rd5)|*.rd5",
            Multiselect = true, CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var paths = picker.FileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var duplicates = paths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicates.Length > 0)
                throw new InvalidOperationException($"不同路径有同名瓦片，请只保留一个版本：{string.Join("、", duplicates)}。");
            var installed = _controller.Data.CurrentManifest?.Tiles.Select(tile => tile.FileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var prepared = new List<ImportRow>();
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                BRouterTile.ParseFileName(name);
                var length = new FileInfo(path).Length;
                if (length < 200) throw new InvalidDataException($"文件为空或不是有效rd5：{name}。");
                prepared.Add(new ImportRow(path, name, length, installed.Contains(name)));
            }
            _rows.Clear();
            foreach (var row in prepared) _rows.Add(row);
            SummaryText.Text = $"已选择 {_rows.Count} 个文件，共 {_rows.Sum(row => row.Length) / 1024d / 1024d:F1} MB；同名 {_rows.Count(row => row.Exists)} 个。";
            ProgressText.Text = "等待确认导入；正式校验在后台执行。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "导入文件校验失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnReselect(object sender, RoutedEventArgs e) => ChooseFiles();

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0 || _cancellation is not null) return;
        if (ApplyAll.IsChecked == true)
        {
            var choice = AllConflictChoice.SelectedIndex == 1 ? ImportRow.ReplaceLabel : ImportRow.KeepLabel;
            foreach (var row in _rows.Where(row => row.Exists)) row.SelectedChoice = choice;
            FileList.Items.Refresh();
        }
        var requests = _rows.Select(row => new RoutingImportRequest(row.Path,
            !row.Exists ? RoutingImportAction.Add : row.SelectedChoice == ImportRow.ReplaceLabel
                ? RoutingImportAction.Replace : RoutingImportAction.Keep)).ToArray();
        _cancellation = new CancellationTokenSource();
        _watch.Restart();
        ImportButton.IsEnabled = false;
        ProgressText.Text = "正在后台复制并校验BRouter路由数据……";
        try
        {
            var result = await _controller.ImportAsync(requests, _cancellation.Token);
            var message = $"BRouter路由数据导入完成\n新增：{result.Added}\n更新：{result.Replaced}\n保留：{result.Kept}\n当前总瓦片：{result.Manifest.TileCount}";
            ProgressText.Text = message.Replace('\n', ' ');
            MessageBox.Show(this, message, "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (OperationCanceledException) { ProgressText.Text = "导入已取消，当前路由数据保持不变。"; }
        catch (Exception exception)
        {
            ProgressText.Text = "导入失败，当前路由数据保持不变。";
            MessageBox.Show(this, $"{ProgressText.Text}\n{exception.Message}",
                "导入BRouter路由数据失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _watch.Stop();
            _cancellation.Dispose();
            _cancellation = null;
            ImportButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null) _cancellation.Cancel();
        else Close();
    }

    private void OnProgress(object? sender, RoutingImportProgress progress) =>
        Dispatcher.BeginInvoke(() =>
        {
            ImportProgress.Maximum = Math.Max(progress.Total, 1);
            ImportProgress.Value = progress.Completed;
            var speed = _watch.Elapsed.TotalSeconds > 0
                ? progress.CopiedBytes / 1024d / 1024d / _watch.Elapsed.TotalSeconds : 0;
            ProgressText.Text = $"导入BRouter路由数据：{progress.Completed}/{progress.Total}；当前 {progress.CurrentTile}；已复制 {progress.CopiedBytes / 1024d / 1024d:F1} MB；{speed:F1} MB/s。";
        });

    private sealed class ImportRow(string path, string fileName, long length, bool exists)
    {
        public const string KeepLabel = "保留当前文件";
        public const string ReplaceLabel = "使用导入文件替换";
        public string Path { get; } = path;
        public string FileName { get; } = fileName;
        public long Length { get; } = length;
        public bool Exists { get; } = exists;
        public string SizeMb => (Length / 1024d / 1024d).ToString("F1");
        public string Status => Exists ? "已存在" : "可导入";
        public string[] Choices { get; } = exists ? [KeepLabel, ReplaceLabel] : ["新增"];
        public string SelectedChoice { get; set; } = exists ? KeepLabel : "新增";
    }
}
