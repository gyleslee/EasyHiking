using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HikingGuide.App.Services;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Routing;

namespace HikingGuide.App;

public partial class BRouterSettingsWindow : Window
{
    private readonly BRouterController _controller;
    private readonly Stopwatch _progressWatch = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _downloadCancellation;

    public BRouterSettingsWindow(BRouterController controller)
    {
        InitializeComponent();
        _controller = controller;
        _controller.ProgressChanged += OnProgress;
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _controller.ProgressChanged -= OnProgress;
            _downloadCancellation?.Cancel();
        };
        RefreshStatus();
        Closing+=(_,e)=>{if(_downloadCancellation is not null||_controller.Data.IsBusy){e.Cancel=true;ProgressText.Text="路由数据操作尚未完成，请等待完成，或先取消当前操作后再关闭窗口。";}};
    }

    private void RefreshStatus()
    {
        EngineStatus.Text = _controller.Process.IsRunning
            ? $"BRouter状态：运行中（127.0.0.1:{_controller.Process.Port}）"
            : "BRouter状态：未运行；有路由数据时按需启动。";
        DataPathText.Text = $"路由数据目录：{_controller.Data.CurrentSegmentsDirectory}";
        var current = _controller.Data.CurrentManifest;
        CurrentData.Text = current is null
            ? "当前路由数据：0 个瓦片"
            : $"当前路由数据：{current.TileCount} 个瓦片，{current.TotalBytes / 1024d / 1024d:F1} MB；最近修改 {current.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        ChinaManifest.Text = $"中国范围清单：{_controller.China.Tiles.Count} 个 5°×5° 路由瓦片（按用户确认的本地文件清单固定，目录增删不会自动改变范围）。";
        InstalledTiles.ItemsSource = current?.Tiles.OrderBy(tile => tile.FileName).Select(tile => new
        {
            tile.FileName,
            SizeMb = (tile.ByteLength / 1024d / 1024d).ToString("F1"),
            SourceLabel = tile.Source switch
            {
                RoutingTileSource.ChinaDownload => "中国下载",
                RoutingTileSource.WorldManualDownload => "世界下载",
                RoutingTileSource.ImportedFile => "本地导入",
                _ => "外部文件"
            }
        }).ToArray();
        var available = !_controller.Data.IsBusy && _downloadCancellation is null;
        UpdateChinaButton.IsEnabled = available;
        SelectWorldButton.IsEnabled = available;
        ImportButton.IsEnabled = available;
    }

    private async void OnUpdateChina(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null || _controller.Data.IsBusy) return;
        if (!_controller.China.IsApprovedRd5Range)
        {
            MessageBox.Show(this, "内置中国范围路由清单与已确认的文件清单不一致，已阻止下载。",
                "中国范围清单校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var installed = _controller.Data.CurrentManifest?.TileCount ?? 0;
        var confirmation = $"中国范围BRouter路由数据\n\n路由瓦片：{_controller.China.Tiles.Count}个\n当前已安装：{installed}个瓦片\n\n下载成功并验证完成后，现有BRouter路由数据将被删除并替换。\n\n开始下载吗？";
        if (MessageBox.Show(this, confirmation, "下载中国范围路由数据",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _downloadCancellation = new CancellationTokenSource();
        _progressWatch.Restart();
        RefreshStatus();
        ProgressText.Text = "准备下载中国范围路由数据……";
        try
        {
            await _controller.UpdateChinaAsync(_downloadCancellation.Token);
            ProgressText.Text = "下载完成并已删除旧数据";
        }
        catch (OperationCanceledException) { ProgressText.Text = "下载已取消，原有数据未删除。"; }
        catch (Exception exception)
        {
            ProgressText.Text = "中国范围BRouter路由数据下载失败，现有路由数据保持不变。";
            MessageBox.Show(this, $"{ProgressText.Text}\n{exception.Message}",
                "BRouter数据更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _progressWatch.Stop();
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            RefreshStatus();
        }
    }

    private async void OnSelectWorld(object sender, RoutedEventArgs e)
    {
        if (_controller.Data.IsBusy) return;
        SelectWorldButton.IsEnabled = false;
        ProgressText.Text = "正在读取官方世界路由瓦片目录……";
        try
        {
            var available = await _controller.WorldTiles.GetAvailableAsync();
            ProgressText.Text = $"官方目录提供 {available.Count} 个路由瓦片。";
            new WorldTileWindow(_controller, available) { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "读取世界路由数据失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshStatus(); }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (_controller.Data.IsBusy) return;
        new BRouterImportWindow(_controller) { Owner = this }.ShowDialog();
        RefreshStatus();
    }

    private void OnProgress(object? sender, RoutingDownloadProgress progress) =>
        Dispatcher.BeginInvoke(() =>
        {
            DownloadProgress.Maximum = Math.Max(progress.Total, 1);
            DownloadProgress.Value = progress.Completed;
            var speed = _progressWatch.Elapsed.TotalSeconds > 0
                ? progress.DownloadedBytes / 1024d / 1024d / _progressWatch.Elapsed.TotalSeconds : 0;
            ProgressText.Text = $"BRouter数据更新：{progress.Completed} / {progress.Total}；已下载 {progress.DownloadedBytes / 1024d / 1024d:F1} MB；{speed:F1} MB/s；当前 {progress.CurrentTile}";
        });

    private void OnCancelDownload(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();

    private void OnOpenDirectory(object sender, RoutedEventArgs e)
    {
        try
        {
            _controller.Paths.EnsureWritable();
            Directory.CreateDirectory(_controller.Data.CurrentSegmentsDirectory);
            Process.Start(new ProcessStartInfo(_controller.Data.CurrentSegmentsDirectory)
                { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "打开路由数据目录失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
