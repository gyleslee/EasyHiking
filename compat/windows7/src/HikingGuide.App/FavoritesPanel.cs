using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Favorites;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Favorites;
using HikingGuide.Infrastructure.Media;
using Microsoft.Win32;

namespace HikingGuide.App;

public sealed partial class FavoritesPanel : UserControl
{
    public sealed record CollectionChoice(Guid? Id, string Name, bool Create = false);
    public sealed record FavoriteRow(FavoritePoint Point, string Collection, int ImageCount)
    {
        public string Name => Point.Name; public double? Elevation => Point.ElevationMeters;
        public double Latitude => Point.Latitude; public double Longitude => Point.Longitude; public DateTimeOffset ModifiedAt => Point.ModifiedAt;
    }
    private readonly bool _manager;
    private readonly ComboBox _collectionsBox = new()
    {
        DisplayMemberPath = "Name", MinWidth = 110, MinHeight = 30, FontSize = 14,
        Padding = new Thickness(6, 2, 6, 2),
        HorizontalContentAlignment = HorizontalAlignment.Left,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly TextBox _search = new() { ToolTip = "非空时搜索整个收藏库的名称和描述，清空后恢复收藏夹筛选。", MinWidth = 80 };
    private readonly CheckBox _show = new() { Content = "地图上显示收藏点", Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended };
    private readonly ListBox _managerCollections = new() { DisplayMemberPath = "Name", Width = 180 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3) };
    private IReadOnlyList<FavoriteCollection> _collections = [];
    private bool _refreshing, _hooked;
    private bool _syncingSelection;
    private long _request;
    private CancellationTokenSource? _searchCancellation;
    private Window? _hoverOwner;
    private static void CloseHover() => FavoriteHoverCardController.Shared.Close();
    private static App Host => (App)System.Windows.Application.Current;
    private FavoritesRepository Repository => Host.Favorites;
    private Window OwnerWindow => Window.GetWindow(this);
    private MapViewModel? Map => (Host.MainWindow?.DataContext as MainViewModel)?.Map;
    public Guid? SelectedCollectionId => (_collectionsBox.SelectedItem as CollectionChoice)?.Id;
    public FavoritesPanel() : this(false) { }
    public FavoritesPanel(bool manager)
    {
        _manager = manager; Background = System.Windows.Media.Brushes.White; var root = new DockPanel { Margin = new Thickness(5) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        _collectionsBox.Width = 160;
        var collectionItemStyle = new Style(typeof(ComboBoxItem));
        collectionItemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        collectionItemStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        collectionItemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 14d));
        collectionItemStyle.Setters.Add(new Setter(Control.MinHeightProperty, 30d));
        _collectionsBox.ItemContainerStyle = collectionItemStyle;
        if (!manager)
        {
            var toolbar = new Grid { MinWidth = 800 };
            foreach (var size in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = size });
            var collectionHost = new StackPanel { Orientation = Orientation.Horizontal };
            collectionHost.Children.Add(new TextBlock { Text = "收藏夹：", VerticalAlignment = VerticalAlignment.Center }); collectionHost.Children.Add(_collectionsBox);
            _collectionsBox.ToolTip = "收藏夹"; _search.ToolTip = "搜索收藏点：非空时搜索全收藏库。"; _search.Margin = new Thickness(8, 0, 5, 0);
            var searchGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 5, 0) };
            searchGroup.Children.Add(new TextBlock { Text = "搜索🔍", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var searchHost = new Grid { Width = 250, ClipToBounds = true }; _search.Margin = new Thickness(0); searchHost.Children.Add(_search); searchGroup.Children.Add(searchHost);
            var searchHint = new TextBlock { Text = "搜索收藏点……", Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), IsHitTestVisible = false };
            searchHost.Children.Add(searchHint); _search.TextChanged += (_, _) => searchHint.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed;
            var open = FavoriteEditorWindow.Button("打开收藏点管理器", () => { OpenManager(); return Task.CompletedTask; });
            _show.HorizontalAlignment = HorizontalAlignment.Left;
            var controls = new FrameworkElement[] { collectionHost, searchGroup, open, _show };
            for (var i = 0; i < controls.Length; i++) { Grid.SetColumn(controls[i], i); toolbar.Children.Add(controls[i]); }
            var toolbarScroll = new ScrollViewer { Margin = new Thickness(3), Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            toolbar.SetBinding(WidthProperty, new Binding("ViewportWidth") { Source = toolbarScroll }); header.Children.Add(toolbarScroll);
        }
        else
        {
            var filter = new WrapPanel { Margin = new Thickness(3) }; filter.Children.Add(new TextBlock { Text = "收藏夹：", VerticalAlignment = VerticalAlignment.Center }); filter.Children.Add(_collectionsBox);
            filter.Children.Add(new TextBlock { Text = "搜索🔍", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) }); _search.Width = 250; filter.Children.Add(_search); header.Children.Add(filter);
        }
        var actions = new WrapPanel(); actions.Children.Add(FavoriteEditorWindow.Button("新建", () => AddAsync(new(23.1, 113.3)))); actions.Children.Add(FavoriteEditorWindow.Button("编辑", EditSelectedAsync));
        actions.Children.Add(FavoriteEditorWindow.Button("删除", DeleteSelectedAsync)); actions.Children.Add(FavoriteEditorWindow.Button("移动到收藏夹…", MoveSelectedAsync)); if (manager) header.Children.Add(actions);
        if (manager)
        {
            var libraryActions = new WrapPanel(); libraryActions.Children.Add(FavoriteEditorWindow.Button("新建收藏夹", CreateCollectionAsync)); libraryActions.Children.Add(FavoriteEditorWindow.Button("重命名收藏夹", RenameCollectionAsync));
            libraryActions.Children.Add(FavoriteEditorWindow.Button("删除收藏夹", DeleteCollectionAsync)); libraryActions.Children.Add(FavoriteEditorWindow.Button("导入…", ImportAsync)); libraryActions.Children.Add(FavoriteEditorWindow.Button("导出收藏库…", ExportAsync)); libraryActions.Children.Add(FavoriteEditorWindow.Button("导出为KMZ…", () => ExportKmzAsync())); header.Children.Add(libraryActions);
        }
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        if (manager)
        {
            var body = new DockPanel(); DockPanel.SetDock(_managerCollections, Dock.Left); body.Children.Add(_managerCollections); body.Children.Add(_grid); root.Children.Add(body);
            foreach (var (name, property) in new[] { ("名称", "Name"), ("收藏夹", "Collection"), ("海拔（米）", "Elevation"), ("纬度", "Latitude"), ("经度", "Longitude"), ("图片数", "ImageCount"), ("修改时间", "ModifiedAt") })
                _grid.Columns.Add(new DataGridTextColumn { Header = name, Binding = new Binding(property) });
            _grid.MouseDoubleClick += async (_, _) => await Guard(EditSelectedAsync);
            _managerCollections.SelectionChanged += async (_, _) => { if (_refreshing || _managerCollections.SelectedItem is not CollectionChoice choice) return;
                _collectionsBox.SelectedItem = _collectionsBox.Items.Cast<CollectionChoice>().FirstOrDefault(c => c.Id == choice.Id); await RefreshAsync(); };
        }
        else root.Children.Add(_list);
        VirtualizingPanel.SetIsVirtualizing(_list, true); VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(_list, true);
        _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingFavoriteWrapPanel)));
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var card = new FrameworkElementFactory(typeof(Border)); card.SetValue(Border.BorderBrushProperty, System.Windows.Media.Brushes.LightGray); card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        card.SetValue(Border.CornerRadiusProperty, new CornerRadius(3)); card.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6)); card.SetValue(Border.MinHeightProperty, 32d);
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding("Name")); text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.None); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center); card.AppendChild(text); _list.ItemTemplate = new DataTemplate { VisualTree = card }; _list.DisplayMemberPath = "";
        _grid.EnableRowVirtualization = true; _grid.EnableColumnVirtualization = true;
        var style = new Style(typeof(ListBoxItem)); style.Setters.Add(new Setter(MarginProperty, new Thickness(4))); style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center)); style.Setters.Add(new EventSetter(MouseEnterEvent, new MouseEventHandler(OnHover))); style.Setters.Add(new EventSetter(MouseLeaveEvent, new MouseEventHandler(OnLeave))); _list.ItemContainerStyle = style;
        _list.SelectionChanged += async (_, e) => { CloseHover(); if (_refreshing || _syncingSelection) return; if (e.AddedItems.Cast<FavoritePoint>().LastOrDefault() is { } point) { Host.FavoriteSelection.SelectFavorite(point); if (Map is not null) await Map.LocateFavoriteAsync(point); } };
        _grid.SelectionChanged += (_, e) => { if (_refreshing || _syncingSelection) return; if (e.AddedItems.Cast<FavoriteRow>().LastOrDefault() is { } row) Host.FavoriteSelection.SelectFavorite(row.Point); };
        _collectionsBox.SelectionChanged += async (_, _) => { CloseHover(); if (_refreshing) return; if ((_collectionsBox.SelectedItem as CollectionChoice)?.Create == true) await Guard(CreateCollectionAsync); else await Guard(RefreshAsync); };
        _search.TextChanged += async (_, _) =>
        {
            CloseHover(); if (_refreshing) return; _searchCancellation?.Cancel(); using var cancellation = new CancellationTokenSource(); _searchCancellation = cancellation;
            try { await Task.Delay(200, cancellation.Token); await RefreshAsync(); } catch (OperationCanceledException) { } catch (Exception error) { _status.Text = "搜索失败：" + error.Message; }
            finally { if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null; }
        };
        _show.Click += async (_, _) => await Guard(async () => { Host.GlobalSettingsService.Current.ShowFavoritesOnMap = _show.IsChecked == true; await Host.GlobalSettingsService.SaveAsync(); Host.NotifyFavoritesChanged(); });
        var menu = new ContextMenu(); Add(menu, "编辑收藏点…", EditSelectedAsync); Add(menu, "移动到收藏夹…", MoveSelectedAsync); Add(menu, "删除收藏点", DeleteSelectedAsync); Add(menu, "导出所选收藏点…", () => ExportSelectedAsync(Selected().Select(p => p.Id).ToArray()));
        Add(menu, "导出所选收藏点为KMZ…", () => ExportKmzAsync(Selected().Select(p => p.Id).ToArray())); _list.ContextMenu = menu; _grid.ContextMenu = menu;
        Add(menu, "取消收藏点选中", () => { Host.FavoriteSelection.ClearSelection(); return Task.CompletedTask; });
        _list.PreviewMouseRightButtonDown += (_, e) => { var item = ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) as ListBoxItem; if (item is { IsSelected: false }) { _list.SelectedItems.Clear(); item.IsSelected = true; } };
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => CloseHover()));
        _list.PreviewMouseWheel += (_, _) => CloseHover(); IsVisibleChanged += (_, _) => { if (!IsVisible) CloseHover(); };
        _grid.PreviewMouseRightButtonDown += (_, e) => { if (ItemsControl.ContainerFromElement(_grid, e.OriginalSource as DependencyObject) is DataGridRow { IsSelected: false } row) { _grid.SelectedItems.Clear(); row.IsSelected = true; } };
        Loaded += async (_, _) => { if (!_hooked) { Host.FavoritesChanged += OnFavoritesChanged; Host.FavoriteSelection.SelectionChanged += OnFavoriteSelection; Host.FavoriteMoveStatus += OnMoveStatus; _hoverOwner = Window.GetWindow(this); if (_hoverOwner is not null) { _hoverOwner.Deactivated += OnOwnerInactive; _hoverOwner.Closed += OnOwnerInactive; } _hooked = true; } await Guard(RefreshAsync); };
        Unloaded += (_, _) => { CloseHover(); if (_hooked) { Host.FavoritesChanged -= OnFavoritesChanged; Host.FavoriteSelection.SelectionChanged -= OnFavoriteSelection; Host.FavoriteMoveStatus -= OnMoveStatus; } if (_hoverOwner is not null) { _hoverOwner.Deactivated -= OnOwnerInactive; _hoverOwner.Closed -= OnOwnerInactive; } _hoverOwner = null; _hooked = false; _searchCancellation?.Cancel(); };
    }
    private void OnMoveStatus(object? sender, string message) => _status.Text = message;
    private void OnFavoriteSelection(object? sender, EventArgs e)
    {
        _syncingSelection = true;
        try
        {
            var id = Host.FavoriteSelection.SelectedFavoritePointId;
            if (_manager) { var row = _grid.Items.Cast<FavoriteRow>().FirstOrDefault(r => r.Point.Id == id); if (row is null) _grid.SelectedItems.Clear(); else if (!_grid.SelectedItems.Contains(row)) { _grid.SelectedItems.Clear(); _grid.SelectedItem = row; } }
            else { var point = _list.Items.Cast<FavoritePoint>().FirstOrDefault(p => p.Id == id); if (point is null) _list.SelectedItems.Clear(); else if (!_list.SelectedItems.Contains(point)) { _list.SelectedItems.Clear(); _list.SelectedItem = point; } }
        }
        finally { _syncingSelection = false; }
    }
    private void OnOwnerInactive(object? sender, EventArgs e) => CloseHover();
    private async void OnFavoritesChanged(object? sender, EventArgs e) => await Guard(RefreshAsync);
    private async Task Guard(Func<Task> action) { try { await action(); } catch (Exception error) { _status.Text = error.Message; MessageBox.Show(OwnerWindow, error.Message, "收藏点操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private void Add(ContextMenu menu, string name, Func<Task> action) { var item = new MenuItem { Header = name }; item.Click += async (_, _) => await Guard(action); menu.Items.Add(item); }
    public async Task RefreshAsync()
    {
        CloseHover();
        if (Host.FavoritesStartupError is not null) { IsEnabled = false; _status.Text = Host.FavoritesStartupError; return; }
        var request = ++_request; var collection = SelectedCollectionId; var query = _search.Text;
        var collections = await Repository.CollectionsAsync(); var points = await Repository.SearchAsync(collection, query); var all = await Repository.SearchAsync();
        var counts = _manager ? await Repository.MediaCountsAsync() : null;
        if (request != _request || collection != SelectedCollectionId || query != _search.Text) return;
        _refreshing = true;
        try
        {
            CloseHover();
            _collections = collections;
            var choices = new[] { new CollectionChoice(null, "全部收藏点") }.Concat(collections.Select(c => new CollectionChoice(c.Id, c.Name))).Append(new CollectionChoice(null, "新建收藏夹", true)).ToArray();
            _collectionsBox.ItemsSource = choices; _collectionsBox.SelectedItem = choices.FirstOrDefault(c => c.Id == collection && !c.Create) ?? choices[0];
            _show.IsChecked = Host.GlobalSettingsService.Current.ShowFavoritesOnMap;
            if (_manager) { _grid.ItemsSource = points.Select(p => new FavoriteRow(p, collections.First(c => c.Id == p.CollectionId).Name, counts!.GetValueOrDefault(p.Id))).ToArray();
                _managerCollections.ItemsSource = choices.Where(c => !c.Create).Select(c => c with { Name = c.Name + $"（{all.Count(p => c.Id is null || p.CollectionId == c.Id)}）" }).ToArray(); }
            else _list.ItemsSource = points;
            _status.Text = (string.IsNullOrWhiteSpace(query) ? "当前列表" : "全库搜索结果") + $"：{points.Count}个" + (!Repository.TrigramAvailable ? "；使用子串搜索兼容模式" : "");
        }
        finally { _refreshing = false; }
        Host.FavoriteSelection.Reconcile(all); OnFavoriteSelection(this, EventArgs.Empty);
        if (!_manager && Map is not null) await Map.UpdateFavoritesAsync(all, _show.IsChecked == true);
    }
    public void OpenManager()
    { CloseHover(); new Window { Owner = OwnerWindow, Title = "收藏点管理器", Width = 1050, Height = 700, MinWidth = 750, MinHeight = 450, Content = new FavoritesPanel(true), WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show(); }
    private IReadOnlyList<FavoritePoint> Selected() => _manager ? _grid.SelectedItems.Cast<FavoriteRow>().Select(r => r.Point).ToArray() : _list.SelectedItems.Cast<FavoritePoint>().ToArray();
    public async Task AddAsync(GeoCoordinate coordinate)
    {
        var point = new FavoritePoint { CollectionId = string.IsNullOrWhiteSpace(_search.Text) ? SelectedCollectionId ?? FavoriteDefaults.UnclassifiedId : FavoriteDefaults.UnclassifiedId,
            Latitude = coordinate.Latitude, Longitude = coordinate.Longitude };
        await EditAsync(point);
    }
    public async Task EditByIdAsync(Guid id) { Host.FavoriteMover.CancelPending(id); if (Map is not null) await Map.SendFavoriteCommandAsync("CloseFavoritePopup"); var details = await Repository.GetAsync(id) ?? throw new ArgumentException("收藏点不存在。"); Host.FavoriteSelection.SelectFavorite(details.Point); Host.NotifyFavoritesChanged(); await EditAsync(details.Point, details); }
    public async Task ViewImagesByIdAsync(Guid id)
    {
        var detail = await Repository.GetAsync(id) ?? throw new ArgumentException("收藏点不存在。");
        Host.FavoriteSelection.SelectFavorite(detail.Point);
        new FavoriteImageViewerWindow(FavoriteImageEntries.Create(Repository,detail,detail.Attachments)){Owner=OwnerWindow}.Show();
    }

    private async Task EditSelectedAsync() { if (Selected().FirstOrDefault() is { } point) await EditByIdAsync(point.Id); }
    private async Task EditAsync(FavoritePoint point, FavoriteDetails? details = null, IReadOnlyList<FavoriteMediaInput>? images = null,
        IReadOnlyList<FavoriteMediaAttachment>? online = null, GeoCoordinate? original = null)
    {
        var editor = new FavoriteEditorWindow(Repository, await Repository.CollectionsAsync(), point, details, images, online, original) { Owner = OwnerWindow };
        if (editor.ShowDialog() == true) Host.NotifyFavoritesChanged();
    }
    public async Task CopyMarkerAsync(HikingProject project, MapMarkerBase marker)
    {
        var draft = await new FavoriteMarkerCopyService().PrepareAsync(project, marker);
        await EditAsync(draft.Point, images: draft.Images, online: draft.OnlineReferences, original: draft.OriginalCoordinate);
    }
    public async Task CreateCollectionAsync()
    {
        var name = FavoritesDialogs.Prompt(OwnerWindow, "新建收藏夹", "收藏夹名称：");
        if (name is null) { _refreshing = true; _collectionsBox.SelectedIndex = 0; _refreshing = false; return; }
        var now = DateTimeOffset.UtcNow; var created = await Repository.SaveCollectionAsync(new(Guid.NewGuid(), name, _collections.Count, now, now));
        await RefreshAsync(); _collectionsBox.SelectedItem = _collectionsBox.Items.Cast<CollectionChoice>().First(c => c.Id == created.Id); Host.NotifyFavoritesChanged();
    }
    public async Task RenameCollectionAsync()
    {
        var collection = _collections.FirstOrDefault(c => c.Id == SelectedCollectionId); if (collection is null || collection.IsSystemCollection) throw new ArgumentException("请先选择一个非系统收藏夹。");
        var name = FavoritesDialogs.Prompt(OwnerWindow, "重命名收藏夹", "收藏夹名称：", collection.Name); if (name is null) return;
        await Repository.SaveCollectionAsync(collection with { Name = name }); Host.NotifyFavoritesChanged();
    }
    public async Task DeleteCollectionAsync()
    {
        var collection = _collections.FirstOrDefault(c => c.Id == SelectedCollectionId); if (collection is null || collection.IsSystemCollection) throw new ArgumentException("请先选择一个非系统收藏夹。");
        var choice = new Window { Owner = OwnerWindow, Title = "删除收藏夹", SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var body = new StackPanel { Margin = new Thickness(16) }; choice.Content = body; body.Children.Add(new TextBlock { Text = $"删除“{collection.Name}”时如何处理其中收藏点？" }); bool deleteAll = false;
        var safe = FavoriteEditorWindow.Button("移动到“未分类”后删除收藏夹", () => { choice.DialogResult = true; return Task.CompletedTask; }); safe.IsDefault = true; body.Children.Add(safe);
        body.Children.Add(FavoriteEditorWindow.Button("删除收藏夹及其中全部收藏点", () => { if (MessageBox.Show(choice, "确定永久删除该收藏夹及其中全部收藏点？", "再次确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes) { deleteAll = true; choice.DialogResult = true; } return Task.CompletedTask; }));
        body.Children.Add(FavoriteEditorWindow.Button("取消", () => { choice.Close(); return Task.CompletedTask; }));
        if (choice.ShowDialog() != true) return; await Repository.DeleteCollectionAsync(collection.Id, deleteAll); _refreshing = true; _collectionsBox.SelectedIndex = 0; _refreshing = false; Host.NotifyFavoritesChanged();
    }
    private async Task DeleteSelectedAsync()
    {
        var selected = Selected(); if (selected.Count == 0) return;
        if (MessageBox.Show(OwnerWindow, $"确定删除所选{selected.Count}个收藏点？", "删除收藏点", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Repository.DeleteAsync(selected.Select(p => p.Id).ToArray()); Host.NotifyFavoritesChanged();
    }
    private async Task MoveSelectedAsync() => await MoveIdsAsync(Selected().Select(p => p.Id).ToArray());
    public async Task MoveIdsAsync(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        var window = new Window { Owner = OwnerWindow, Title = "移动到收藏夹", SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var combo = new ComboBox { ItemsSource = await Repository.CollectionsAsync(), DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 250, Margin = new Thickness(10) };
        var stack = new StackPanel(); stack.Children.Add(combo); stack.Children.Add(FavoriteEditorWindow.Button("确定移动", () => { window.DialogResult = true; return Task.CompletedTask; })); window.Content = stack;
        if (window.ShowDialog() != true) return; await Repository.MoveAsync(ids, ((FavoriteCollection)combo.SelectedItem).Id); Host.NotifyFavoritesChanged();
    }
    public async Task DeleteIdsAsync(IReadOnlyList<Guid> ids)
    { if (MessageBox.Show(OwnerWindow, "确定删除此收藏点？", "删除收藏点", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes) { await Repository.DeleteAsync(ids); Host.NotifyFavoritesChanged(); } }
    public async Task ImportAsync()
    {
        var dialog = new OpenFileDialog { Title = "导入收藏点", Filter = "支持的收藏文件|*.hgfav;*.kmz|收藏点交换包|*.hgfav|KMZ兴趣点|*.kmz" }; if (dialog.ShowDialog(OwnerWindow) != true) return;
        var window = new Window { Owner = OwnerWindow, Title = "导入冲突处理", SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(12) }; panel.Children.Add(new TextBlock { Text = "身份相同的收藏点如何处理？（应用到本次所有冲突）" }); FavoriteConflictPolicy policy = FavoriteConflictPolicy.Skip;
        foreach (var (name, value) in new[] { ("跳过", FavoriteConflictPolicy.Skip), ("覆盖", FavoriteConflictPolicy.Overwrite), ("保留两份", FavoriteConflictPolicy.KeepBoth) }) panel.Children.Add(FavoriteEditorWindow.Button(name, () => { policy = value; window.DialogResult = true; return Task.CompletedTask; })); window.Content = panel;
        if (window.ShowDialog() != true) return; _status.Text = "正在验证并导入收藏点及图片…";
        var result = Path.GetExtension(dialog.FileName).Equals(".kmz",StringComparison.OrdinalIgnoreCase)?await new FavoriteKmzImporter().ImportAsync(Repository,dialog.FileName,policy,new WpfImageDecodeValidator()):await Repository.ImportAsync(dialog.FileName, policy); Host.NotifyFavoritesChanged(); MessageBox.Show(OwnerWindow, $"已导入{result.Imported}个，跳过{result.Skipped}个。", "收藏点导入完成");
    }
    private async Task<(FavoriteExportSelection Selection,IReadOnlyList<FavoriteCollection> Collections,IReadOnlyList<FavoritePoint> Points)?> ChooseExportAsync(IReadOnlyList<Guid>? explicitIds=null)
    {
        var collections=await Repository.CollectionsAsync();var points=await Repository.SearchAsync();
        if(explicitIds is not null){var selection=new FavoriteExportSelection(FavoriteExportScope.Points,[],explicitIds);selection.Validate();return(selection,collections,points);}
        var window=new FavoriteExportWindow(collections,points,Selected().Select(p=>p.Id).ToArray(),SelectedCollectionId){Owner=OwnerWindow};
        return window.ShowDialog()==true?(window.Selection!,collections,points):null;
    }
    public async Task ExportAsync()=>await ExportSelectedAsync();
    private async Task ExportSelectedAsync(IReadOnlyList<Guid>? ids=null,Guid? collectionId=null)
    {
        var choice=await ChooseExportAsync(ids);if(choice is null)return;var(selection,collections,points)=choice.Value;
        if(collectionId.HasValue)selection=new(FavoriteExportScope.Collections,[collectionId.Value],[]);
        var dialog=new SaveFileDialog{Title="导出收藏点及完整图片",Filter="收藏点交换包|*.hgfav",DefaultExt=".hgfav",FileName=selection.DefaultFileName(collections,points)+".hgfav",OverwritePrompt=true};
        if(dialog.ShowDialog(OwnerWindow)!=true)return;
        _status.Text="正在导出收藏点与原图…";
        await Repository.ExportAsync(dialog.FileName,selection.Scope==FavoriteExportScope.Points?selection.PointIds:null,collectionIds:selection.Scope==FavoriteExportScope.Collections?selection.CollectionIds:null);
        _status.Text="收藏点和原图导出完成。";
    }
    public async Task ExportKmzAsync(IReadOnlyList<Guid>? explicitSelection=null)
    {
        CloseHover();var choice=await ChooseExportAsync(explicitSelection);if(choice is null)return;var(selection,collections,points)=choice.Value;
        var selected=selection.Scope==FavoriteExportScope.Points?selection.PointIds:null;
        var collectionIds=selection.Scope==FavoriteExportScope.Collections?selection.CollectionIds:null;
        var save=new SaveFileDialog{Title="导出收藏点为KMZ",Filter="KMZ地图文件|*.kmz",DefaultExt=".kmz",FileName=selection.DefaultFileName(collections,points)+".kmz",OverwritePrompt=true};
        if (save.ShowDialog(OwnerWindow) != true) return;
        using var cancellation = new CancellationTokenSource(); bool completed = false;
        var progressWindow = new Window { Owner = OwnerWindow, Title = "正在导出收藏点KMZ", Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var body = new StackPanel { Margin = new Thickness(16) }; var text = new TextBlock { Text = "正在准备收藏点与原图…", TextWrapping = TextWrapping.Wrap }; body.Children.Add(text);
        body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 12, 0, 12) });
        body.Children.Add(FavoriteEditorWindow.Button("取消", () => { cancellation.Cancel(); text.Text = "正在取消导出…"; return Task.CompletedTask; })); progressWindow.Content = body;
        progressWindow.Closing += (_, e) => { if (!completed) { e.Cancel = true; cancellation.Cancel(); text.Text = "正在取消导出…"; } }; progressWindow.Show();
        FavoriteKmzResult? result = null;
        try
        {
            var progress = new Progress<FavoriteKmzProgress>(p => text.Text = $"正在导出收藏点KMZ\n收藏点：{p.PointsCompleted} / {p.PointsTotal}\n图片：{p.ImagesCompleted} / {p.ImagesTotal}");
            result = await Repository.ExportKmzAsync(save.FileName, selected, null, true, progress, cancellation.Token,collectionIds);
        }
        catch (OperationCanceledException) { _status.Text = "已取消KMZ导出，未生成半成品文件。"; }
        finally { completed = true; progressWindow.Close(); }
        if (result is not null) MessageBox.Show(OwnerWindow, $"收藏点KMZ导出完成\n收藏点：{result.PointCount}\n收藏夹：{result.CollectionCount}\n图片：{result.ImageCount}\n文件：{result.FileBytes / 1048576d:F2} MB\n保存位置：{result.FilePath}", "收藏点KMZ导出完成");
    }
    private async void OnHover(object sender, MouseEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not FavoritePoint point) return;
        var controller = FavoriteHoverCardController.Shared; var request = controller.Enter(point.Id, item);
        try
        {
            await Task.Delay(250, request.Token); if (!controller.Matches(point.Id, item, request.Generation)) return;
            var details = await Repository.GetAsync(point.Id, request.Token); if (details is null || !controller.Matches(point.Id, item, request.Generation)) return;
            var card = new StackPanel { MaxWidth = 280 }; card.Children.Add(new TextBlock { Text = point.Name, FontWeight = FontWeights.Bold });
            card.Children.Add(new TextBlock { Text = point.Description.Length > 500 ? point.Description[..500] + "……" : point.Description, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 });
            card.Children.Add(new TextBlock { Text = $"海拔：{(point.ElevationMeters is double h ? h.ToString("F2") + "米" : "无数据")}\n纬度：{point.Latitude:F6}\n经度：{point.Longitude:F6}" });
            if (details.Blobs.FirstOrDefault() is { } blob) card.Children.Add(new Image { Source = await FavoriteImageService.ThumbnailAsync(Repository, blob), MaxHeight = 150, MaxWidth = 240 });
            if (details.Attachments.Count > 1) card.Children.Add(new TextBlock { Text = $"共{details.Attachments.Count}张图片" });
            if (details.Attachments.Any(a => a.OnlineOnly)) card.Children.Add(new TextBlock { Text = "包含仅在线网络图片" });
            controller.Open(point.Id, item, request.Generation, new Border { Background = System.Windows.Media.Brushes.White, BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(10), Child = card });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _status.Text = "收藏点信息读取失败：" + error.Message; }
    }
    private void OnLeave(object sender, MouseEventArgs e) => CloseHover();
    public async Task<bool> RunAutomationPanelAsync(Guid collectionId, string query, int expected)
    {
        await RefreshAsync();
        _refreshing = true; _collectionsBox.SelectedItem = _collectionsBox.Items.Cast<CollectionChoice>().First(c => c.Id == collectionId);
        _search.Text = query; _refreshing = false; await RefreshAsync();
        return (_collectionsBox.Items.Cast<CollectionChoice>().Last().Create) && _list.Items.Count == expected;
    }
    public async Task<(bool Passed, int VisibleContainers, double Milliseconds)> RunVirtualizationSmokeAsync()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var points = Enumerable.Range(0, 10000).Select(i => new FavoritePoint { Name = $"收藏点{i + 1}", Latitude = 23, Longitude = 113 }).ToArray();
        _refreshing = true;
        try
        {
            _list.ItemsSource = points; _list.UpdateLayout();
            var panel = FindPanel(_list);
            if (panel is null) return (false, 0, watch.Elapsed.TotalMilliseconds);
            var visible = System.Windows.Media.VisualTreeHelper.GetChildrenCount(panel);
            var bounded = visible > 0 && visible < 500;
            panel.SetVerticalOffset(panel.ExtentHeight); _list.UpdateLayout();
            var reachesEnd = _list.ItemContainerGenerator.ContainerFromIndex(9999) is ListBoxItem;
            panel.SetVerticalOffset(0); _list.UpdateLayout();
            var reachesStart = _list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem;
            return (bounded && reachesEnd && reachesStart, visible, watch.Elapsed.TotalMilliseconds);
        }
        finally { _refreshing = false; await RefreshAsync(); }
        static VirtualizingFavoriteWrapPanel? FindPanel(DependencyObject root)
        {
            if (root is VirtualizingFavoriteWrapPanel panel) return panel;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindPanel(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }
    }
}
