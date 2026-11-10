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
        _latitude = new(), _longitude = new(), _elevation = new();
    private readonly ComboBox _collection = new() { DisplayMemberPath = "Name" }, _dem = new() { DisplayMemberPath = "Name" };
    private readonly ListBox _images = new() { Height = 120 };
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,3)};
    private readonly List<FavoriteMediaAttachment> _retained;
    private readonly List<FavoriteMediaInput> _added;
    private readonly FavoriteDetails? _details;
    private FavoriteElevationSourceKind _elevationKind;
    private string? _elevationReference;
    private bool _settingElevation, _busy, _saving, _loaded;
    private Panel? _form;
    private CancellationTokenSource? _demRequest, _operation;
    private long _demVersion, _elevationEdits;
    private Task _demTask=Task.CompletedTask;
    private bool _automation;
    public bool Saved { get; private set; }
    public FavoriteEditorWindow(FavoritesRepository repository, IReadOnlyList<FavoriteCollection> collections, FavoritePoint point,
        FavoriteDetails? details = null, IReadOnlyList<FavoriteMediaInput>? images = null, IReadOnlyList<FavoriteMediaAttachment>? onlineReferences = null,
        GeoCoordinate? originalCoordinate = null)
    {
        _repository = repository; _original = point; _details = details; _retained = details?.Attachments.ToList() ?? onlineReferences?.ToList() ?? [];
        _added = images?.ToList() ?? []; _elevationKind = point.ElevationSourceKind; _elevationReference = point.ElevationSourceReference;
        Title = details is null ? "新建收藏点" : "编辑收藏点"; Background = System.Windows.Media.Brushes.White; Width = 450; Height = 740; MinWidth = 380; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = Button("保存", async () => await SaveAsync()); var cancel = Button("取消", () => { if (!_saving) { _operation?.Cancel(); Close(); } return Task.CompletedTask; });
        buttons.Children.Add(save); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var stack = new StackPanel(); _form=stack; root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
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
        _elevation.TextChanged += (_, _) => { if (!_settingElevation) { _elevationEdits++; _elevationKind = FavoriteElevationSourceKind.Manual; _elevationReference = null; } };
        var databases=((App)System.Windows.Application.Current).DemCatalog.Databases;
        _dem.ItemsSource=databases;
        _dem.SelectedItem=details is null?databases.FirstOrDefault():databases.FirstOrDefault(d=>point.ElevationSourceReference?.StartsWith(d.Id.ToString("D"),StringComparison.OrdinalIgnoreCase)==true);
        AddRow(stack,"选择高程数据库（选择后自动读取）",_dem);
        _dem.SelectionChanged+=(_,_)=>QueueDem();_latitude.TextChanged+=(_,_)=>QueueDem(true);_longitude.TextChanged+=(_,_)=>QueueDem(true);
        AddRow(stack,"图片",_images);
        _images.MouseDoubleClick+=async(_,e)=>{if(e.OriginalSource is DependencyObject source&&ItemsControl.ContainerFromElement(_images,source) is ListBoxItem)try{await ViewImageAsync();}catch(Exception error){MessageBox.Show(this,error.Message,"图片打开失败");}};
        var mediaButtons=new WrapPanel();
        mediaButtons.Children.Add(Button("添加本地图片",AddImagesAsync));
        mediaButtons.Children.Add(Button("添加网络图片链接并下载",AddNetworkAsync));
        mediaButtons.Children.Add(Button("查看图片",ViewImageAsync));
        mediaButtons.Children.Add(Button("移除所选图片",()=>{var i=_images.SelectedIndex;if(i>=0){if(i<_retained.Count)_retained.RemoveAt(i);else _added.RemoveAt(i-_retained.Count);RefreshImages();}return Task.CompletedTask;}));
        mediaButtons.Children.Add(Button("修改图片说明",()=>{var i=_images.SelectedIndex;if(i<0)return Task.CompletedTask;var text=FavoritesDialogs.Prompt(this,"图片说明","图片说明：",i<_retained.Count?_retained[i].Caption??"":_added[i-_retained.Count].Caption??"");if(text is not null){if(i<_retained.Count)_retained[i]=_retained[i] with{Caption=text};else _added[i-_retained.Count]=_added[i-_retained.Count] with{Caption=text};RefreshImages();}return Task.CompletedTask;}));
        stack.Children.Add(mediaButtons);stack.Children.Add(_status);RefreshImages();
        Loaded+=(_,_)=>{_loaded=true;if(details is null){if(_dem.SelectedItem is null)_status.Text="尚未挂载DEM，可手动填写海拔。";else QueueDem();}};
        Closing += (_, e) => { if (_saving) e.Cancel = true; else { _demRequest?.Cancel(); _operation?.Cancel(); } };
    }
    private static void AddRow(Panel panel, string label, UIElement control)
    { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) }); panel.Children.Add(control); }
    internal static Button Button(string title, Func<Task> action)
    {
        var button = new Button { Content = title, Margin = new Thickness(3), Padding = new Thickness(7, 4, 7, 4) };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch(OperationCanceledException){} catch (Exception error) { MessageBox.Show(Window.GetWindow(button), error.Message, "收藏点操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { button.IsEnabled = true; } }; return button;
    }
    private void SetCoordinate(GeoCoordinate point) { _latitude.Text = point.Latitude.ToString("R", CultureInfo.InvariantCulture); _longitude.Text = point.Longitude.ToString("R", CultureInfo.InvariantCulture); }
    private GeoCoordinate Coordinate()
    {
        if (!double.TryParse(_latitude.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) || !double.TryParse(_longitude.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)) throw new ArgumentException("请填写有效经纬度数值。");
        var coordinate = new GeoCoordinate(latitude, longitude); coordinate.Validate(); return coordinate;
    }
    private void QueueDem(bool debounce=false){if(!_loaded)return;_demRequest?.Cancel();_demRequest?.Dispose();_demRequest=new();_demTask=ReadDemAsync(++_demVersion,_elevationEdits,_demRequest.Token,debounce);}
    private async Task ReadDemAsync(long version,long edits,CancellationToken token,bool debounce){
        try{if(debounce)await Task.Delay(350,token);if(_dem.SelectedItem is not HikingGuide.Infrastructure.Elevation.DemDatabase db)return;
            var provider=((App)System.Windows.Application.Current).DemProvider;if(provider is null){_status.Text="DEM读取组件不可用，原有海拔保留，可手动填写。";return;}
            var coordinate=Coordinate();if(!_busy)_status.Text="正在读取DEM海拔…";
            var elevation=await Task.Run(async()=>await provider.SampleAsync(db.Id,coordinate,token),token);
            if(token.IsCancellationRequested||version!=_demVersion||edits!=_elevationEdits)return;
            if(elevation is null){if(!_busy)_status.Text="所选DEM未覆盖或无有效高程，原有海拔保留，可手动填写。";return;}
            _settingElevation=true;try{_elevation.Text=elevation.Value.ToString("F2",CultureInfo.CurrentCulture);}finally{_settingElevation=false;}
            _elevationKind=FavoriteElevationSourceKind.CopiedDem;_elevationReference=$"{db.Id:D}|{db.Name}";if(!_busy)_status.Text=$"已从{db.Name}读取海拔。";
        }catch(OperationCanceledException){}catch(Exception error){if(!token.IsCancellationRequested&&!_busy)_status.Text="DEM读取未完成，原有海拔保留："+error.Message;}}
    private async Task AddNetworkAsync(){if(_busy)return;var url=FavoritesDialogs.Prompt(this,"添加网络图片链接并下载","HTTP/HTTPS图片链接：");if(string.IsNullOrWhiteSpace(url))return;await AddNetworkImageAsync(url.Trim());}
    internal async Task AddNetworkImageAsync(string url){
        if(_busy)return;
        var existing=_retained.FindIndex(a=>a.MediaBlobId.HasValue&&a.ExternalUrl==url);var pending=_added.FindIndex(a=>a.ExternalUrl==url);
        if(existing>=0||pending>=0){_images.SelectedIndex=existing>=0?existing:_retained.Count+pending;_status.Text="此链接的本地图片已在列表中。";return;}
        _busy=true;_form!.IsEnabled=false;_operation=new();var token=_operation.Token;_status.Text="正在下载图片，完成后随收藏点保存；取消可关闭此窗口。";
        try{var image=await FavoriteImageService.DownloadAsync(url,token);token.ThrowIfCancellationRequested();var online=_retained.FindIndex(a=>a.OnlineOnly&&a.ExternalUrl==url);if(online>=0){image=image with{Caption=_retained[online].Caption};_retained.RemoveAt(online);}_added.Add(image);RefreshImages();_images.SelectedIndex=_retained.Count+_added.Count-1;_status.Text="图片已下载，保存收藏点后存入收藏库。";}
        catch{_status.Text="下载未完成，未添加图片，可稍后重试。";throw;}
        finally{_busy=false;_form.IsEnabled=true;_operation?.Dispose();_operation=null;}
    }
    private async Task AddImagesAsync()
    {
        if(_busy)return;
        var dialog = new OpenFileDialog { Title = "选择收藏点图片", Multiselect = true, Filter = "图片文件|*.jpg;*.jpeg;*.png;*.gif;*.bmp" };
        if (dialog.ShowDialog(this) != true) return;
        _busy=true;_form!.IsEnabled=false;_operation=new();var token=_operation.Token;
        try{var loaded=new List<FavoriteMediaInput>();foreach(var path in dialog.FileNames){token.ThrowIfCancellationRequested();loaded.Add(await FavoriteImageService.LoadAsync(path));}token.ThrowIfCancellationRequested();_added.AddRange(loaded);RefreshImages();}
        finally{_busy=false;_form.IsEnabled=true;_operation?.Dispose();_operation=null;}
    }
    private void RefreshImages(){var i=_images.SelectedIndex;_images.ItemsSource=_retained.Select(a=>a.OriginalFileName+(a.Caption is{Length:>0}?" · "+a.Caption:"")).Concat(_added.Select(a=>a.OriginalFileName+(a.Caption is{Length:>0}?" · "+a.Caption:""))).ToArray();if(_images.Items.Count>0)_images.SelectedIndex=Math.Clamp(i,0,_images.Items.Count-1);}
    private Task ViewImageAsync(){if(_busy||_images.SelectedIndex<0)return Task.CompletedTask;new FavoriteImageViewerWindow(FavoriteImageEntries.Create(_repository,_details,_retained,_added),_images.SelectedIndex){Owner=this}.ShowDialog();return Task.CompletedTask;}
    private async Task SaveAsync()
    {
        if(_busy)return;_busy=_saving=true;_form!.IsEnabled=false;
        try{
        await _demTask;
        var coordinate = Coordinate(); double? elevation = null;
        if (!string.IsNullOrWhiteSpace(_elevation.Text)) { if (!double.TryParse(_elevation.Text, out var parsed) || !double.IsFinite(parsed)) throw new ArgumentException("海拔应为空或有效数值。"); elevation = parsed; }
        var point = _original with { Name = _name.Text.Trim(), Description = _description.Text,
            CollectionId = ((FavoriteCollection)_collection.SelectedItem).Id, Latitude = coordinate.Latitude, Longitude = coordinate.Longitude,
            ElevationMeters = elevation, ElevationSourceKind = elevation.HasValue ? _elevationKind : FavoriteElevationSourceKind.Unknown, ElevationSourceReference = _elevationReference };
        point.Validate();
        await _repository.SaveAsync(point, _added, _retained); Saved = true; _busy = _saving = false; if (_automation) Close(); else DialogResult = true;
        }finally { _busy = _saving = false; _form.IsEnabled=true; }
    }
    public Task RunAutomationSaveAsync() { _automation = true; return SaveAsync(); }
    internal Task RunP92EditAsync(string name, string description) { _name.Text = name; _description.Text = description; return SaveAsync(); }
    internal ComboBox DemForAutomation=>_dem;
    internal TextBox ElevationForAutomation=>_elevation;
    internal Task DemTaskForAutomation=>_demTask;
    internal void DoubleClickImageForAutomation(){var item=_images.ItemContainerGenerator.ContainerFromIndex(_images.SelectedIndex) as ListBoxItem??throw new InvalidOperationException("图片列表尚未显示");_images.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=item});}
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
