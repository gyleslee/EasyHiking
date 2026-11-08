using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.Core.Favorites;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Favorites;
using HikingGuide.App.Services;
using Microsoft.Win32;

namespace HikingGuide.App;

public sealed class FavoriteEditorWindow : Window
{
    private readonly FavoritesRepository _repository;
    private readonly FavoritePoint _original;
    private readonly TextBox _name = new(), _description = new() { AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.Wrap },
        _latitude = new(), _longitude = new(), _elevation = new(), _sourceReference = new();
    private readonly ComboBox _collection = new() { DisplayMemberPath = "Name" }, _dem = new() { DisplayMemberPath = "Name" };
    private readonly ListBox _images = new() { Height = 120 };
    private readonly List<FavoriteMediaAttachment> _retained;
    private readonly List<FavoriteMediaInput> _added;
    private readonly FavoriteDetails? _details;
    private FavoriteElevationSourceKind _elevationKind;
    private string? _elevationReference;
    private bool _settingElevation, _busy;
    private bool _automation;
    public bool Saved { get; private set; }
    public FavoriteEditorWindow(FavoritesRepository repository, IReadOnlyList<FavoriteCollection> collections, FavoritePoint point,
        FavoriteDetails? details = null, IReadOnlyList<FavoriteMediaInput>? images = null, IReadOnlyList<FavoriteMediaAttachment>? onlineReferences = null,
        GeoCoordinate? originalCoordinate = null)
    {
        _repository = repository; _original = point; _details = details; _retained = details?.Attachments.ToList() ?? onlineReferences?.ToList() ?? [];
        _added = images?.ToList() ?? []; _elevationKind = point.ElevationSourceKind; _elevationReference = point.ElevationSourceReference;
        Title = details is null ? "新建收藏点" : "编辑收藏点"; Background = System.Windows.Media.Brushes.White; Width = 600; Height = 740; MinWidth = 480; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = Button("保存", async () => await SaveAsync()); var cancel = Button("取消", () => { if (!_busy) Close(); return Task.CompletedTask; });
        buttons.Children.Add(save); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var stack = new StackPanel(); root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _name.Text = point.Name; _description.Text = point.Description; _latitude.Text = point.Latitude.ToString("R", CultureInfo.InvariantCulture); _longitude.Text = point.Longitude.ToString("R", CultureInfo.InvariantCulture);
        _elevation.Text = point.ElevationMeters?.ToString("G", CultureInfo.CurrentCulture) ?? "";
        _collection.ItemsSource = collections; _collection.SelectedItem = collections.FirstOrDefault(c => c.Id == point.CollectionId) ?? collections.First(c => c.IsSystemCollection);
        AddRow(stack, "名称", _name); AddRow(stack, "收藏夹", _collection); AddRow(stack, "描述", _description);
        if (originalCoordinate is GeoCoordinate original)
        {
            var positions = new StackPanel { Orientation = Orientation.Horizontal };
            var current = new RadioButton { Content = "当前地图/吸附位置", IsChecked = true, Margin = new Thickness(4) };
            var source = new RadioButton { Content = "原始标注位置", Margin = new Thickness(4) };
            current.Checked += (_, _) => SetCoordinate(new(point.Latitude, point.Longitude)); source.Checked += (_, _) => SetCoordinate(original);
            positions.Children.Add(current); positions.Children.Add(source); AddRow(stack, "复制位置", positions);
        }
        AddRow(stack, "纬度（WGS84）", _latitude); AddRow(stack, "经度（WGS84）", _longitude); AddRow(stack, "固定记录海拔（米，可空）", _elevation);
        _elevation.TextChanged += (_, _) => { if (!_settingElevation) { _elevationKind = FavoriteElevationSourceKind.Manual; _elevationReference = null; } };
        _dem.ItemsSource = ((App)System.Windows.Application.Current).DemCatalog.Databases;
        var demRow = new DockPanel(); var demButton = Button("从DEM读取…", ReadDemAsync); DockPanel.SetDock(demButton, Dock.Right); demRow.Children.Add(demButton); demRow.Children.Add(_dem); AddRow(stack, "选择高程数据库", demRow);
        AddRow(stack, "图片（网络引用标记为仅在线）", _images);
        _sourceReference.Text = point.SourceReference ?? ""; AddRow(stack, "来源/链接（可空）", _sourceReference);
        var mediaButtons = new WrapPanel(); mediaButtons.Children.Add(Button("添加图片…", AddImagesAsync)); mediaButtons.Children.Add(Button("查看图片", ViewImageAsync));
        mediaButtons.Children.Add(Button("添加网络图片链接…", () => { var url = FavoritesDialogs.Prompt(this, "网络图片链接", "HTTP/HTTPS图片链接："); if (url is null) return Task.CompletedTask; CheckUrl(url); _retained.Add(new(Guid.NewGuid(), _original.Id, null, "网络图片", null, _retained.Count + _added.Count, DateTimeOffset.UtcNow, url, true)); RefreshImages(); return Task.CompletedTask; }));
        mediaButtons.Children.Add(Button("修改所选图片链接…", () => { var index = _images.SelectedIndex; if (index < 0 || index >= _retained.Count) return Task.CompletedTask; var url = FavoritesDialogs.Prompt(this, "图片链接", "HTTP/HTTPS图片链接（留空移除）：", _retained[index].ExternalUrl ?? ""); if (url is null) return Task.CompletedTask; if (!string.IsNullOrWhiteSpace(url)) CheckUrl(url); _retained[index] = _retained[index] with { ExternalUrl = string.IsNullOrWhiteSpace(url) ? null : url }; RefreshImages(); return Task.CompletedTask; }));
        mediaButtons.Children.Add(Button("移除所选图片", () => { var index = _images.SelectedIndex; if (index >= 0) { if (index < _retained.Count) _retained.RemoveAt(index); else _added.RemoveAt(index - _retained.Count); RefreshImages(); } return Task.CompletedTask; }));
        mediaButtons.Children.Add(Button("修改图片说明", () => { var index = _images.SelectedIndex; if (index < 0) return Task.CompletedTask; var text = FavoritesDialogs.Prompt(this, "图片说明", "图片说明：", index < _retained.Count ? _retained[index].Caption ?? "" : _added[index - _retained.Count].Caption ?? "");
            if (text is not null) { if (index < _retained.Count) _retained[index] = _retained[index] with { Caption = text }; else _added[index - _retained.Count] = _added[index - _retained.Count] with { Caption = text }; } return Task.CompletedTask; }));
        stack.Children.Add(mediaButtons); RefreshImages();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }
    private static void AddRow(Panel panel, string label, UIElement control)
    { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) }); panel.Children.Add(control); }
    internal static Button Button(string title, Func<Task> action)
    {
        var button = new Button { Content = title, Margin = new Thickness(3), Padding = new Thickness(7, 4, 7, 4) };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception error) { MessageBox.Show(Window.GetWindow(button), error.Message, "收藏点操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { button.IsEnabled = true; } }; return button;
    }
    private void SetCoordinate(GeoCoordinate point) { _latitude.Text = point.Latitude.ToString("R", CultureInfo.InvariantCulture); _longitude.Text = point.Longitude.ToString("R", CultureInfo.InvariantCulture); }
    private GeoCoordinate Coordinate()
    {
        if (!double.TryParse(_latitude.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) || !double.TryParse(_longitude.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)) throw new ArgumentException("请填写有效经纬度数值。");
        var coordinate = new GeoCoordinate(latitude, longitude); coordinate.Validate(); return coordinate;
    }
    private async Task ReadDemAsync()
    {
        if (_dem.SelectedItem is not HikingGuide.Infrastructure.Elevation.DemDatabase database) throw new ArgumentException("请先选择一套DEM数据库。");
        var app = (App)System.Windows.Application.Current; if (app.DemProvider is null) throw new InvalidOperationException("DEM读取组件不可用。");
        var coordinate = Coordinate(); var elevation = await Task.Run(async () => await app.DemProvider.SampleAsync(database.Id, coordinate));
        if (elevation is null) throw new InvalidOperationException("当前DEM数据库不覆盖此收藏点位置，或该位置没有有效高程。");
        _settingElevation = true; _elevation.Text = elevation.Value.ToString("F2", CultureInfo.CurrentCulture); _settingElevation = false;
        _elevationKind = FavoriteElevationSourceKind.CopiedDem; _elevationReference = $"{database.Id:D}|{database.Name}";
    }
    private async Task AddImagesAsync()
    {
        var dialog = new OpenFileDialog { Title = "选择收藏点图片", Multiselect = true, Filter = "图片文件|*.jpg;*.jpeg;*.png;*.gif;*.bmp" };
        if (dialog.ShowDialog(this) != true) return;
        var loaded = new List<FavoriteMediaInput>(); foreach (var path in dialog.FileNames) loaded.Add(await FavoriteImageService.LoadAsync(path)); _added.AddRange(loaded); RefreshImages();
    }
    private void RefreshImages() => _images.ItemsSource = _retained.Select(a => a.OriginalFileName + (a.OnlineOnly ? "（仅在线）" : "" )).Concat(_added.Select(a => a.OriginalFileName)).ToArray();
    private async Task ViewImageAsync()
    {
        var index = _images.SelectedIndex; if (index < 0) return;
        byte[] bytes;
        if (index >= _retained.Count) bytes = _added[index - _retained.Count].Bytes;
        else { var attachment = _retained[index]; if (attachment.OnlineOnly) { FavoritesDialogs.OpenExternal(attachment.ExternalUrl!); return; }
            var blob = _details?.Blobs.FirstOrDefault(b => b.Id == attachment.MediaBlobId) ?? throw new InvalidOperationException("此图片暂无可用原图。"); bytes = await _repository.ReadOriginalAsync(blob); }
        var source = await Task.Run(() => FavoriteImageService.Decode(bytes));
        new Window { Owner = this, Title = "收藏点图片", Width = 850, Height = 650, Content = new Image { Source = source, Stretch = System.Windows.Media.Stretch.Uniform } }.ShowDialog();
    }
    private async Task SaveAsync()
    {
        var coordinate = Coordinate(); double? elevation = null;
        if (!string.IsNullOrWhiteSpace(_elevation.Text)) { if (!double.TryParse(_elevation.Text, out var parsed) || !double.IsFinite(parsed)) throw new ArgumentException("海拔应为空或有效数值。"); elevation = parsed; }
        var point = _original with { Name = _name.Text.Trim(), Description = _description.Text,
            CollectionId = ((FavoriteCollection)_collection.SelectedItem).Id, Latitude = coordinate.Latitude, Longitude = coordinate.Longitude,
            ElevationMeters = elevation, ElevationSourceKind = elevation.HasValue ? _elevationKind : FavoriteElevationSourceKind.Unknown, ElevationSourceReference = _elevationReference,
            SourceReference = string.IsNullOrWhiteSpace(_sourceReference.Text) ? null : _sourceReference.Text.Trim() };
        point.Validate(); _busy = true;
        try { await _repository.SaveAsync(point, _added, _retained); Saved = true; _busy = false; if (_automation) Close(); else DialogResult = true; }
        finally { _busy = false; }
    }
    public Task RunAutomationSaveAsync() { _automation = true; return SaveAsync(); }
    internal Task RunP92EditAsync(string name, string description) { _name.Text = name; _description.Text = description; return SaveAsync(); }
    private static void CheckUrl(string value) { if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) throw new ArgumentException("图片链接必须是有效的HTTP/HTTPS地址。"); }
}

internal static class FavoritesDialogs
{
    public static string? Prompt(Window owner, string title, string label, string initial = "")
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(8), MinWidth = 280 }; var panel = new StackPanel { Margin = new Thickness(8) };
        var window = new Window { Owner = owner, Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ResizeMode = ResizeMode.NoResize };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8) }); panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okay = new Button { Content = "确定", IsDefault = true, Margin = new Thickness(8), Padding = new Thickness(12, 4, 12, 4) }; okay.Click += (_, _) => window.DialogResult = true;
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8), Padding = new Thickness(12, 4, 12, 4) }; buttons.Children.Add(okay); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; return window.ShowDialog() == true ? input.Text : null;
    }
    public static void OpenExternal(string url)
    { if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("网络图片链接无效。"); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
}
