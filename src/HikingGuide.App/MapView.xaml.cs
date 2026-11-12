using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HikingGuide.Core.Models;
using HikingGuide.Application.Logging;
using HikingGuide.Application.Maps;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;

namespace HikingGuide.App;

public partial class MapView : UserControl
{
    private readonly ILoggingService _log;
    private WebViewMapBridge? _bridge;
    internal WebViewMapBridge? BridgeForAutomation => _bridge;
    private long _trackMenuRequestVersion;
    internal ContextMenu? ActiveFavoriteMenu { get; private set; }
    internal ContextMenu? ActiveTrackMenuForAutomation { get; private set; }
    internal ContextMenu? ActiveMapMenuForAutomation {get;private set;}

    public MapView()
    {
        InitializeComponent();
        _log = ((App)System.Windows.Application.Current).LoggingService;
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await AttachAsync();
    private void OnOpenMapManager(object sender,RoutedEventArgs e)
    {if(Window.GetWindow(this) is MainWindow window)window.OpenMapManager();}
    private async void OnToggleTerrain(object sender,RoutedEventArgs e)
    {try{if(DataContext is MapViewModel vm)await vm.ToggleTerrainAsync();}catch(Exception error){MessageBox.Show(Window.GetWindow(this),"无法切换3D地图："+error.Message,"3D地图");}}
    private async void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => await AttachAsync();

    private async Task AttachAsync()
    {
        if (!IsLoaded || DataContext is not MapViewModel viewModel || _bridge is not null) return;
        _bridge = new WebViewMapBridge(MapWebView, _log, Path.Combine(AppContext.BaseDirectory, "Map", "Web"));
        viewModel.RightClicked += OnRightClicked;
        await viewModel.AttachBridgeAsync(_bridge);
    }

    private void OnRightClicked(object? sender, MapEventMessage value)
    {
        ++_trackMenuRequestVersion;
        if (ActiveTrackMenuForAutomation is { } previousTrackMenu) previousTrackMenu.IsOpen = false;
        ActiveTrackMenuForAutomation = null;
        if (!value.Latitude.HasValue || !value.Longitude.HasValue) return;
        if (value.Type == "FavoriteRightClicked" && value.FavoriteId is Guid favoriteId && Window.GetWindow(this) is MainWindow favoritesWindow)
        {
            var favoritesMenu = new ContextMenu();
            ActiveFavoriteMenu = favoritesMenu;
            AddFavoriteAction(favoritesMenu, "编辑收藏点…", () => favoritesWindow.FavoritesContent.EditByIdAsync(favoriteId));
            AddFavoriteAction(favoritesMenu, "移动到收藏夹…", () => favoritesWindow.FavoritesContent.MoveIdsAsync([favoriteId]));
            AddFavoriteAction(favoritesMenu, "查看图片…", () => favoritesWindow.FavoritesContent.ViewImagesByIdAsync(favoriteId));
            AddFavoriteAction(favoritesMenu, "删除收藏点", () => favoritesWindow.FavoritesContent.DeleteIdsAsync([favoriteId]));
            AddWeather(favoritesMenu, value); OpenAtMapPosition(favoritesMenu, value); return;
        }
        if (value.Type == "MarkerRightClicked")
        {
            ShowMarkerMenu(value);
            return;
        }
        if (value.Type is "TrackContextMenuRequested" or "SplitSectionContextMenuRequested")
        {
            ShowTrackMenu(value);
            return;
        }
        var coordinate = $"{value.Latitude.Value:F8}, {value.Longitude.Value:F8}";
        var menu = new ContextMenu();
        AddFavoriteAt(menu, value);
        if (DataContext is MapViewModel viewModel)
        {
            var waypoint = new MenuItem { Header = "在此添加普通标注" };
            waypoint.Click += (_, _) => viewModel.RequestAddWaypoint(value.Latitude.Value, value.Longitude.Value);
            menu.Items.Add(waypoint);
            var guide = new MenuItem { Header = "在此添加攻略节点" };
            guide.Click += (_, _) => viewModel.RequestAddGuideNode(value.Latitude.Value, value.Longitude.Value);
            menu.Items.Add(guide);
            menu.Items.Add(new Separator());
        }
        var copy = new MenuItem { Header = $"复制经纬度（{coordinate}）" };
        copy.Click += (_, _) => Clipboard.SetText(coordinate);
        menu.Items.Add(copy);
        AddWeather(menu, value);
        OpenAtMapPosition(menu, value);
    }

    private async void ShowTrackMenu(MapEventMessage value)
    {
        try { await ShowTrackMenuAsync(value, _trackMenuRequestVersion); }
        catch (Exception exception) { _log.Error(exception, "打开轨迹编辑菜单失败"); }
    }

    private async Task ShowTrackMenuAsync(MapEventMessage value, long requestVersion)
    {
        var main = Window.GetWindow(this)?.DataContext as MainViewModel;
        var project = main?.MapContextProject;
        var track = project?.Tracks.FirstOrDefault(item => item.Id == value.TrackId);
        if (main is null || project is null || track is null || project.Id != value.ProjectId) return;
        var prepared = await main.PrepareTrackMapContextAsync(value);
        if (requestVersion != _trackMenuRequestVersion || prepared is null || !ReferenceEquals(main.MapContextProject, project) ||
            DataContext is not MapViewModel map || !map.IsTrackMapContextCurrent(prepared)) return;
        value = prepared;
        if (ActiveTrackMenuForAutomation is { } previousMenu) previousMenu.IsOpen = false;
        var menu = new ContextMenu();
        ActiveTrackMenuForAutomation = menu;
        menu.Tag = value;
        AddFavoriteAt(menu, value);
        if (main.IsTrackEditActive)
            Add("退出轨迹编辑模式", main.ExitTrackEditContextCommand, track);
        else
            Add("进入项目轨迹编辑模式", main.EnterTrackEditContextCommand, track);
        menu.Items.Add(new Separator());
        Add("添加轨迹点", main.AddTrackPointCommand);
        Add("删除轨迹点", main.DeleteTrackPointCommand);
        menu.Items.Add(new Separator());
        Add("拆分轨迹段", main.SplitTrackSegmentCommand, value);
        Add("删除轨迹段", main.DeleteTrackSegmentCommand);
        Add("反转轨迹段", main.ReverseTrackSegmentCommand);
        menu.Items.Add(new Separator());
        Add("反转整条轨迹", main.ReverseTrackCommand);
        menu.Items.Add(new Separator());
        Add("合并轨迹", main.CreateMergedTrackCommand);
        Add("连接轨迹", main.CreateConnectedTrackCommand);
        if(new HikingGuide.Core.Services.TrackKilometerService().Assess(track).Eligible){menu.Items.Add(new Separator());menu.Items.Add(new MenuItem{Header="显示公里数标识（所选轨迹）",Command=new ScopedMapCommand(main.ShowKilometersCommand,()=>ReferenceEquals(main.MapContextProject,project)&&map.IsTrackMapContextCurrent(value)),CommandParameter=track});menu.Items.Add(new MenuItem{Header="隐藏公里数标识（所选轨迹）",Command=new ScopedMapCommand(main.HideKilometersCommand,()=>ReferenceEquals(main.MapContextProject,project)&&map.IsTrackMapContextCurrent(value)),CommandParameter=track});}
        AddWeather(menu, value);
        OpenAtMapPosition(menu, value);

        void Add(string header, ICommand command, object? parameter = null) => menu.Items.Add(new MenuItem
        {
            Header = header,
            ToolTip=map.TerrainCoordinateEditingBlocked?MapViewModel.TerrainEditingWarning:null,
            Command = new ScopedMapCommand(command, () => ReferenceEquals(main.MapContextProject, project) &&
                project.Tracks.Contains(track) && map.IsTrackMapContextCurrent(value)&&(!map.TerrainCoordinateEditingBlocked||header is "进入项目轨迹编辑模式" or "退出轨迹编辑模式")),
            CommandParameter = parameter
        });
    }

    /// <summary>保留原命令可执行状态，同时阻止旧菜单对后来选中的其他轨迹执行。</summary>
    private sealed class ScopedMapCommand(ICommand command, Func<bool> isCurrent) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => command.CanExecuteChanged += value;
            remove => command.CanExecuteChanged -= value;
        }
        public bool CanExecute(object? parameter) => isCurrent() && command.CanExecute(parameter);
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter)) command.Execute(parameter);
        }
    }

    private void ShowMarkerMenu(MapEventMessage value)
    {
        var main = Window.GetWindow(this)?.DataContext as MainViewModel;
        var project = main?.MapContextProject;
        if (main is null || project is null || project.Id != value.ProjectId) return;
        MapMarkerBase? marker = value.GuideNodeId is Guid guideId
            ? project.GuideNodes.FirstOrDefault(node => node.Id == guideId)
            : value.WaypointId is Guid waypointId
                ? project.Waypoints.FirstOrDefault(point => point.Id == waypointId) : null;
        if (marker is null) return;
        main.SelectMarkerFromMapContext(marker);
        var isGuide = marker is GuideNode;
        var hasMain = project.MainGuideTrack is not null;
        var menu = new ContextMenu();
        if (Window.GetWindow(this) is MainWindow favoritesWindow)
            AddFavoriteAction(menu, "复制为收藏点", () => favoritesWindow.FavoritesContent.CopyMarkerAsync(project, marker));
        Add($"编辑{(isGuide ? "攻略节点" : "普通标注点")}",
            () => main.EditMarkerCommand.Execute(marker), main.EditMarkerCommand.CanExecute(marker));
        Add($"删除{(isGuide ? "攻略节点" : "普通标注点")}",
            () => main.DeleteMarkerCommand.Execute(marker), main.DeleteMarkerCommand.CanExecute(marker));
        menu.Items.Add(new Separator());
        if (marker is Waypoint)
            Add("转为攻略节点", () => _ = main.ConvertMapMarkerAsync(marker, true), hasMain && !main.IsBusy);
        else
            Add("转为普通标注点", () => _ = main.ConvertMapMarkerAsync(marker, false), !main.IsBusy);
        Add("吸附到攻略轨迹", () => _ = main.SnapMapMarkerAsync(marker, false), hasMain && !main.IsBusy);
        Add("选择吸附路段（重合／折返）…", () => _ = main.SnapMapMarkerAsync(marker, marker is Waypoint, true), !main.IsBusy && (marker is Waypoint || hasMain));
        if (marker is Waypoint)
            Add("吸附到最近轨迹", () => _ = main.SnapMapMarkerAsync(marker, true),
                project.Tracks.Count > 0 && !main.IsBusy);
        AddWeather(menu, value);
        OpenAtMapPosition(menu, value);

        void Add(string title, Action action, bool enabled)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                if (!ReferenceEquals(main.MapContextProject, project) ||
                    marker is Waypoint point && !project.Waypoints.Contains(point) ||
                    marker is GuideNode node && !project.GuideNodes.Contains(node)) return;
                action();
            };
            menu.Items.Add(item);
        }
    }

    private void OpenAtMapPosition(ContextMenu menu, MapEventMessage value)
    {
        ActiveMapMenuForAutomation=menu;
        menu.PlacementTarget = MapWebView;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = value.ScreenX ?? 0;
        menu.VerticalOffset = value.ScreenY ?? 0;
        menu.IsOpen = true;
    }

    private void AddFavoriteAt(ContextMenu menu, MapEventMessage value)
    {
        if (Window.GetWindow(this) is MainWindow window && value.Latitude is double latitude && value.Longitude is double longitude)
            AddFavoriteAction(menu, "添加收藏点", () => DataContext is MapViewModel vm&&!vm.AllowTerrainCoordinateEdit()?Task.CompletedTask:window.FavoritesContent.AddAsync(new(latitude, longitude)));
    }
    private void AddFavoriteAction(ContextMenu menu, string title, Func<Task> action)
    {
        var item = new MenuItem { Header = title };
        item.Click += async (_, _) => { try { await action(); } catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "收藏点操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); } };
        menu.Items.Add(item);
    }

    private void AddWeather(ContextMenu menu, MapEventMessage value)
    {
        if (value.Latitude is not double latitude || value.Longitude is not double longitude) return;
        menu.Items.Add(new Separator());
        Add("windy天气", WeatherUrlService.Windy(latitude, longitude));
        Add("meteoblue天气", WeatherUrlService.Meteoblue(latitude, longitude));

        void Add(string title, string url)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) =>
            {
                try { DefaultBrowserUrlLauncher.OpenInDefaultBrowser(url); }
                catch (Exception error)
                {
                    _log.Error(error, $"打开天气网页失败：{title}");
                    MessageBox.Show("无法打开系统默认浏览器。", "天气查询", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            menu.Items.Add(item);
        }
    }
}
