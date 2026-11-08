using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using HikingGuide.Infrastructure.Elevation;

namespace HikingGuide.App;

public partial class DemDatabaseWindow : Window
{
    private readonly DemDatabaseCatalog _catalog;
    private bool _busy;
    public event EventHandler? DatabasesChanged;

    private sealed record Row(DemDatabase Database)
    {
        public Guid Id => Database.Id;
        public string Name => Database.Name;
        public string RootPath => Database.RootPath;
        public string StatusText => !Directory.Exists(RootPath) ? "目录未连接" :
            Database.Status switch
            {
                DemDatabaseStatus.Ready => "可用",
                DemDatabaseStatus.Empty => "无有效文件",
                DemDatabaseStatus.Invalid => "无效",
                _ => "目录未连接"
            };
        public int ValidFileCount => Database.ValidFileCount;
        public int InvalidFileCount => Database.InvalidFileCount;
        public string UnitText => Database.UnitMetersConfirmed ? "已确认米" : "自动识别";
        public string? VerticalDatumName => Database.VerticalDatumName ?? "未注明";
    }

    public DemDatabaseWindow(DemDatabaseCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();
        RefreshRows();
        Closing+=(_,e)=>{if(_busy){e.Cancel=true;StatusLabel.Text="正在处理DEM数据，请等待完成后再关闭窗口。";}};
    }

    private Row? Selected => DatabaseGrid.SelectedItem as Row;

    private void RefreshRows(Guid? selectId = null)
    {
        DatabaseGrid.ItemsSource = _catalog.Databases.Select(item => new Row(item)).ToArray();
        if (selectId is Guid id)
            DatabaseGrid.SelectedItem = ((Row[])DatabaseGrid.ItemsSource).FirstOrDefault(row => row.Id == id);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshRows(Selected?.Id);

    private async void OnMount(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var folder = ChooseFolder();
        if (folder is null) return;
        var proposal = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var name = Prompt("数据库名称", "为这套 DEM 指定唯一名称：", proposal);
        if (name is null) return;
        var confirmed = MessageBox.Show(this,
            "若 DEM 文件没有注明高程单位，是否确认这套数据的高程单位是米？\n不确定请选择“否”；未知单位将不能参与统计。",
            "确认高程单位", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        await RunAsync(async progress => await _catalog.MountAsync(name, folder,
            unitMetersConfirmed: confirmed, progress: progress));
    }

    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { } selected) return;
        await RunAsync(async progress => await _catalog.RescanAsync(selected.Id, progress));
    }

    private async void OnRelocate(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { } selected) return;
        var folder = ChooseFolder();
        if (folder is null) return;
        await RunAsync(async progress => await _catalog.RelocateAsync(selected.Id, folder, progress));
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { } selected) return;
        var name = Prompt("重命名 DEM 数据库", "新的数据库名称：", selected.Name);
        if (name is null) return;
        await RunAsync(async _ => { await _catalog.RenameAsync(selected.Id, name); return null; });
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { } selected) return;
        if (MessageBox.Show(this,
            $"仅移除“{selected.Name}”的软件挂载配置和索引缓存，原始 DEM 文件不会被删除。是否继续？",
            "移除 DEM 挂载", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async _ => { await _catalog.RemoveMountAsync(selected.Id); return null; });
    }

    private async Task RunAsync(Func<IProgress<DemScanProgress>, Task<DemScanResult?>> operation)
    {
        _busy = true;
        ScanProgress.Visibility = Visibility.Visible;
        var progress = new Progress<DemScanProgress>(value => StatusLabel.Text =
            $"已扫描 {value.Discovered} 个文件；有效 {value.Valid}，无效 {value.Invalid}：{value.CurrentFile}");
        try
        {
            var result = await operation(progress);
            RefreshRows(result?.Database.Id);
            DatabasesChanged?.Invoke(this, EventArgs.Empty);
            StatusLabel.Text = result is null ? "数据库配置已更新。" :
                $"完成：有效 {result.Database.ValidFileCount}，无效 {result.Database.InvalidFileCount}。";
            if (result is { Warnings.Count: > 0 })
                MessageBox.Show(this, string.Join("\n", result.Warnings.Take(8)),
                    "DEM 扫描警告", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            StatusLabel.Text = $"操作失败：{error.Message}";
            MessageBox.Show(this, error.Message, "DEM 数据库错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; ScanProgress.Visibility = Visibility.Collapsed; }
    }

    private string? ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择包含 TIF/HGT 的 DEM 目录" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string? Prompt(string title, string instruction, string initial)
    {
        var dialog = new Window { Title = title, Width = 420, Height = 160,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = instruction });
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 10) };
        panel.Children.Add(box);
        var button = new Button { Content = "确定", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(button);
        dialog.Content = panel;
        box.SelectAll();
        box.Focus();
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
