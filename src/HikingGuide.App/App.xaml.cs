using System.IO;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HikingGuide.Application.Logging;
using HikingGuide.Application.Projects;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.Maps;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Services;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Infrastructure.Logging;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.TrackFiles;
using HikingGuide.Infrastructure.Media;
using HikingGuide.Application.Media;
using HikingGuide.Infrastructure.Routing;
using HikingGuide.Infrastructure.Elevation;

namespace HikingGuide.App;

public partial class App : System.Windows.Application
{
    private readonly Stopwatch _startupClock = Stopwatch.StartNew();
    private readonly Dictionary<string, double> _startupTimings = [];
    private ILoggingService? _loggingService;
    private IAppSessionStateService? _sessionStateService;
    private MainViewModel? _viewModel;
    private HttpClient? _mapHttpClient;
    private HttpExternalMediaDownloadService? _externalMediaDownloadService;
    public BRouterController? BRouterController { get; private set; }
    private HikingGuide.Infrastructure.Maps.OsmRoadTileSource? _osmRoads;
    public HikingGuide.Infrastructure.Maps.OsmRoadTileSource OsmRoads=>_osmRoads??=CreateOsmRoads();
    private HikingGuide.Infrastructure.Maps.OsmRoadTileSource CreateOsmRoads()
    {
        var runtime=Environment.GetEnvironmentVariable("HIKINGGUIDE_BROUTER_RUNTIME_DIR");
        if(string.IsNullOrWhiteSpace(runtime))runtime=BRouterController!.Paths.RuntimeDirectory;
        var data=BRouterController!.Data.CurrentSegmentsDirectory;
        var testData=Environment.GetEnvironmentVariable("HIKINGGUIDE_OSM_ROAD_DATA_DIR");
        if(!string.IsNullOrWhiteSpace(testData)&&GlobalSettingsService.SettingsFilePath.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))data=testData;
        return new(data,runtime,Path.Combine(AppContext.BaseDirectory,"RoadNetworkRuntime","OsmRoadReader.jar"),Path.Combine(new ApplicationDataPaths().RootDirectory,"OsmRoadCache"),BRouterController.DisplayDataGate);
    }
    private HikingGuide.Infrastructure.Maps.OfflineMapLibrary? _offlineMaps;
    private HikingGuide.Infrastructure.Maps.OfflineMapDownloads? _offlineDownloads;
    private Services.OfflineRasterTileFetcher? _offlineFetcher;
    public HikingGuide.Infrastructure.Maps.OfflineMapLibrary OfflineMaps=>_offlineMaps??=new(ApplicationDataPaths.OfflineMapsDirectory(AppContext.BaseDirectory,Environment.GetEnvironmentVariable("HIKINGGUIDE_FUNCTION_DATA_ROOT")),Path.Combine(new ApplicationDataPaths().RootDirectory,"OfflineMapState"));
    public HikingGuide.Infrastructure.Maps.OfflineMapDownloads OfflineDownloads=>_offlineDownloads??=new(OfflineMaps,_offlineFetcher??=new(OfflineMaps,HikingGuide.Infrastructure.Maps.MapTileDiskCache.Shared(Path.Combine(new ApplicationDataPaths().RootDirectory,"MapTileCache"))));
    public DemDatabaseCatalog DemCatalog { get; private set; } = null!;
    public DemElevationProvider? DemProvider { get; private set; }
    public HikingGuide.App.Services.TerrainTileGenerator? TerrainTiles {get;private set;}
    public string? DemStartupError { get; private set; }
    public string? DemConfigWarning { get; private set; }
    public ILoggingService LoggingService => _loggingService ?? throw new InvalidOperationException("日志服务尚未初始化。");
    public IGlobalSettingsService GlobalSettingsService { get; private set; } = null!;
    public HikingGuide.Infrastructure.Favorites.FavoritesRepository Favorites { get; private set; } = null!;
    public string? FavoritesStartupError { get; private set; }
    public event EventHandler? FavoritesChanged;
    public void NotifyFavoritesChanged() => FavoritesChanged?.Invoke(this, EventArgs.Empty);
    public Task ClearOfflineTerrainCacheAsync(bool contours)=>_viewModel?.Map.ClearOfflineTerrainCacheAsync(contours)??
        HikingGuide.Infrastructure.Maps.MapTileDiskCache.Shared(DemTerrainTiles.CacheRoot).ClearAsync(contours?DemTerrainTiles.ContourCacheId:DemTerrainTiles.HillshadeCacheId);
    public IMapSourceTestService MapSourceTestService { get; private set; } = null!;
    public MapSourceTemplateService MapSourceTemplateService { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var applicationStartedAt = DateTimeOffset.Now;
        base.OnStartup(e);

        if (e.Args.Any(value => value is "--t10-ui-test" or "--t10-manual-capture" or "--lifecycle-profile" or "--lifecycle-ui-test" or "--china-range-ui-test" or "--installation-ui-test" or "--arcgis-redirect-ui-test"))
        {
            // 必须在任何设置加载、会话记录或收藏库初始化之前验证真实数据路径。
            var isolatedData = Environment.GetEnvironmentVariable("HIKINGGUIDE_APPDATA_DIR");
            var isolatedFunctions = Environment.GetEnvironmentVariable("HIKINGGUIDE_FUNCTION_DATA_ROOT");
            if (string.IsNullOrWhiteSpace(isolatedData) || string.IsNullOrWhiteSpace(isolatedFunctions) ||
                !Path.GetFullPath(isolatedData).StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(isolatedFunctions).StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase))
            { Shutdown(2); return; }
        }

        if(!await PrepareInstalledUserDataAsync())return;
#if DEBUG
        const bool enableDebugLogging = true;
#else
        const bool enableDebugLogging = false;
#endif
        _loggingService = new SerilogLoggingService(
            Path.Combine(ApplicationDataPaths.FunctionRootDirectory(AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("HIKINGGUIDE_FUNCTION_DATA_ROOT")), "logs"),
            enableDebugLogging);
        RegisterGlobalExceptionHandlers();
        _loggingService.Information("应用程序启动");

        var dataPaths = new ApplicationDataPaths();
        DemCatalog = new DemDatabaseCatalog(dataPaths);
        // DEM索引/GDAL与全局设置、收藏库互不依赖，后台并行准备；窗口构造前统一等待。
        var demInitialization = Task.Run(async () =>
        {
            try { await DemCatalog.LoadAsync(); }
            catch (Exception error)
            { _loggingService.Error(error, "[DemDatabase] 加载DEM挂载配置失败"); DemConfigWarning = $"DEM数据库挂载配置读取失败：{error.Message}。项目不会被删除；请检查应用数据目录。"; }
            var catalogMs = _startupClock.Elapsed.TotalMilliseconds;
            try { DemProvider = new DemElevationProvider(DemCatalog); TerrainTiles = new(DemCatalog); }
            catch (Exception error)
            { DemStartupError = $"内置 GDAL/PROJ 高程运行库未能初始化：{error.Message}。GPS分析和其他项目功能仍可使用。"; _loggingService.Error(error, "[DemRuntime] 高程运行库初始化失败"); }
            return (CatalogMs: catalogMs, RuntimeMs: _startupClock.Elapsed.TotalMilliseconds);
        });
        var imageValidator = new WpfImageDecodeValidator();
        _externalMediaDownloadService = new HttpExternalMediaDownloadService(dataPaths, imageValidator: imageValidator);
        MapSourceTemplateService = new MapSourceTemplateService();
        GlobalSettingsService = new GlobalSettingsService(dataPaths, _loggingService, MapSourceTemplateService);
        await GlobalSettingsService.LoadAsync();
        _startupTimings["SettingsReadyMs"] = _startupClock.Elapsed.TotalMilliseconds;
        var configuredFunctionRoot = Environment.GetEnvironmentVariable("HIKINGGUIDE_FUNCTION_DATA_ROOT");
        var functionRoot = ApplicationDataPaths.FunctionRootDirectory(AppContext.BaseDirectory, configuredFunctionRoot);
        Favorites = new HikingGuide.Infrastructure.Favorites.FavoritesRepository(new HikingGuide.Infrastructure.Favorites.FavoritesPathProvider(functionRoot));
        try { await Favorites.InitializeAsync(); }
        catch (Exception error) { FavoritesStartupError = $"收藏点数据库无法打开：{error.Message}。原收藏库已保留，项目功能不受影响。"; _loggingService.Error(error, "[Favorites] 初始化失败"); }
        _startupTimings["FavoritesReadyMs"] = _startupClock.Elapsed.TotalMilliseconds;
        var demReady = await demInitialization;
        _startupTimings["DemCatalogReadyMs"] = demReady.CatalogMs;
        _startupTimings["DemRuntimeReadyMs"] = demReady.RuntimeMs;
        BRouterController = new BRouterController(_loggingService,
            e.Args.Any(value => value is "--t10-ui-test" or "--t10-manual-capture" or "--lifecycle-profile" or "--lifecycle-ui-test" or "--china-range-ui-test" or "--installation-ui-test")
                ? new BRouterPathProvider(Path.Combine(functionRoot, "RuntimeHost")) : null);
        BRouterController.DatasetChanged+=(_,_)=>Dispatcher.BeginInvoke(()=>{_osmRoads?.InvalidateData();GlobalSettingsService.NotifyMapSettingsChanged();});
        BRouterController.UpdateCompleted += (_, message) => Dispatcher.BeginInvoke(() =>
            MessageBox.Show(MainWindow, message, "BRouter路由数据", MessageBoxButton.OK,
                MessageBoxImage.Information));
        BRouterController.WarningRaised += (_, message) => Dispatcher.BeginInvoke(() =>
            MessageBox.Show(MainWindow, message, "BRouter路由数据警告", MessageBoxButton.OK,
                MessageBoxImage.Warning));
        _mapHttpClient = MapTileHttpTransport.CreateClient();
        MapSourceTestService = new MapSourceTestService(_mapHttpClient, MapSourceTemplateService, _loggingService);
        var projectFileService = new ProjectFileService();
        IRecentProjectService recentProjectService = new RecentProjectService(dataPaths);
        IActiveProjectService activeProjectService = new ActiveProjectService(dataPaths);
        IAutoSaveService autoSaveService = new AutoSaveService(projectFileService, dataPaths);
        LifecycleAutoSave? lifecycleAutoSave = null;
        if(e.Args.Contains("--lifecycle-ui-test")) autoSaveService = lifecycleAutoSave = new(autoSaveService);
        _sessionStateService = new AppSessionStateService(dataPaths);
        var previousSessionEndedNormally = await _sessionStateService.BeginSessionAsync();
        IProjectService projectService = new ProjectService(projectFileService, recentProjectService, autoSaveService);
        ITrackFileService trackFileService = new TrackFileService(
            [new GpxTrackImporter(), new KmlTrackImporter(imageValidator), new KmzTrackImporter(imageValidator: imageValidator)],
            [new GpxTrackExporter(), new KmlTrackExporter()],
            _loggingService);
        IFileDialogService dialogService = new WindowsFileDialogService();
        LifecycleDialogs? lifecycleDialogs = null;
        if(e.Args.Contains("--lifecycle-ui-test")) dialogService = lifecycleDialogs = new();
        var mapProfile = MapDisplayProfile.Default;
        var mapViewModel = new MapViewModel(
            GlobalSettingsService,
            new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier(mapProfile)),
            new MapGeoJsonBuilder(),
            mapProfile,
            _loggingService);
        _viewModel = new MainViewModel(
            projectService,
            _loggingService,
            trackFileService,
            dialogService,
            recentProjectService,
            autoSaveService,
            activeProjectService,
            mapViewModel,
            _externalMediaDownloadService);
        _viewModel.ConfigureRouting(BRouterController.Provider);
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        InitializeFavoriteInteractions(mapViewModel);
        window.Show();
        _dataMigrationWindow?.Close();_dataMigrationWindow=null;
        _startupTimings["WindowShownMs"] = _startupClock.Elapsed.TotalMilliseconds;
        if (DemStartupError is not null)
            _ = Dispatcher.BeginInvoke(() => MessageBox.Show(window, DemStartupError,
                "DEM 高程模块不可用", MessageBoxButton.OK, MessageBoxImage.Warning));
        if (DemConfigWarning is not null)
            _ = Dispatcher.BeginInvoke(() => MessageBox.Show(window, DemConfigWarning,
                "DEM 数据库配置警告", MessageBoxButton.OK, MessageBoxImage.Warning));
        void QueueRoutingInitialization() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!window.IsVisible || Dispatcher.HasShutdownStarted) return;
            _ = Task.Run(async () =>
            {
            try
            {
                if (e.Args.Length == 0) await BRouterController.InitializeLocalAsync();
                else await BRouterController.RecoverAsync();
            }
            catch (Exception exception)
            {
                _loggingService?.Error(exception, "[RoutingData] 本地BRouter数据初始化失败");
                _ = Dispatcher.BeginInvoke(() => MessageBox.Show(window,
                    $"BRouter路由数据初始化失败：{exception.Message}", "BRouter路由数据",
                    MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            });
        }), DispatcherPriority.ContextIdle);
        var restoreSmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p5-restore-smoke-test", StringComparison.OrdinalIgnoreCase));
        await _viewModel.InitializeAsync(previousSessionEndedNormally, restoreLastProject: e.Args.Length == 0 || restoreSmokeIndex >= 0 || e.Args.Contains("--lifecycle-profile"));
        // 先完成前台项目恢复；路由数据扫描/迁移不再与大项目解压争用启动阶段磁盘。
        // 校验和迁移逻辑不变，仍在后台完整执行。
        QueueRoutingInitialization();
        _startupTimings["WorkspaceReadyMs"] = _startupClock.Elapsed.TotalMilliseconds;
        _loggingService.Information($"[Lifecycle] 主界面显示 {_startupTimings["WindowShownMs"]:F0} 毫秒；工作区恢复 {_startupTimings["WorkspaceReadyMs"]:F0} 毫秒");
        var lifecycleProfile=Array.IndexOf(e.Args,"--lifecycle-profile");
        if(lifecycleProfile>=0&&lifecycleProfile+1<e.Args.Length)
        {
            await _viewModel.WaitForMapRefreshForAutomationAsync();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            _startupTimings["InteractiveMs"]=_startupClock.Elapsed.TotalMilliseconds;
            await File.WriteAllTextAsync(e.Args[lifecycleProfile+1],JsonSerializer.Serialize(new{Passed=true,Timings=_startupTimings,Workspace=_viewModel.LifecycleTimings,Projects=_viewModel.NavigationNodes.Count},new JsonSerializerOptions{WriteIndented=true}));
            Shutdown(0);return;
        }
        var lifecycleSmoke=Array.IndexOf(e.Args,"--lifecycle-ui-test");
        if(lifecycleSmoke>=0&&lifecycleSmoke+1<e.Args.Length)
        {
            var success=await RunLifecycleSmokeAsync(window,_viewModel,projectService,lifecycleAutoSave!,lifecycleDialogs!,e.Args[lifecycleSmoke+1]);
            Shutdown(success?0:1);return;
        }
        var t10 = Array.FindIndex(e.Args, value => value is "--t10-ui-test" or "--t10-manual-capture");
        var arcgis=Array.IndexOf(e.Args,"--arcgis-redirect-ui-test");
        if(arcgis>=0&&arcgis+1<e.Args.Length)
        {
            var success=await RunArcgisRedirectSmokeAsync(window,_viewModel,e.Args[arcgis+1]);Shutdown(success?0:1);return;
        }
        var installation=Array.IndexOf(e.Args,"--installation-ui-test");
        if(installation>=0&&installation+1<e.Args.Length)
        {
            var success=await RunInstallationSmokeAsync(window,_viewModel.Map,e.Args[installation+1]);Shutdown(success?0:1);return;
        }
        var chinaRange = Array.IndexOf(e.Args,"--china-range-ui-test");
        if(chinaRange>=0&&chinaRange+1<e.Args.Length)
        {
            var success=await RunChinaRangeSmokeAsync(window,e.Args[chinaRange+1]);
            Shutdown(success?0:1);return;
        }
        if (t10 >= 0 && t10 + 1 < e.Args.Length)
        {
            var success = await RunT10SmokeAsync(window, _viewModel, e.Args[t10 + 1], e.Args[t10] == "--t10-manual-capture");
            Shutdown(success ? 0 : 1); return;
        }
        var boundarySmoke = Array.FindIndex(e.Args, value => value == "--guide-boundary-ui-test");
        var coordinateSmoke=Array.FindIndex(e.Args,value=>value=="--map-crs-ui-test");
        if(coordinateSmoke>=0&&coordinateSmoke+1<e.Args.Length){var success=await RunMapCoordinateSmokeAsync(window,_viewModel,e.Args[coordinateSmoke+1]);Shutdown(success?0:1);return;}
        var rasterSmoke=Array.FindIndex(e.Args,value=>value=="--p101-raster-ui-test");
        var rasterReopen=Array.FindIndex(e.Args,value=>value=="--p101-raster-reopen-test");
        if(rasterReopen>=0&&rasterReopen+2<e.Args.Length){var success=await RunRasterStackReopenAsync(window,_viewModel,e.Args[rasterReopen+1],e.Args[rasterReopen+2]);Shutdown(success?0:1);return;}
        if(rasterSmoke>=0&&rasterSmoke+1<e.Args.Length){var success=await RunRasterStackSmokeAsync(window,_viewModel,e.Args[rasterSmoke+1]);Shutdown(success?0:1);return;}
        var linkSmoke = Array.FindIndex(e.Args, value => value == "--elevation-link-ui-test");
        if (linkSmoke >= 0 && linkSmoke + 1 < e.Args.Length)
        {
            var success = await RunElevationLinkSmokeAsync(window, _viewModel, e.Args[linkSmoke + 1]);
            Shutdown(success ? 0 : 1); return;
        }
        if (boundarySmoke >= 0 && boundarySmoke + 2 < e.Args.Length)
        {
            var success = await RunGuideBoundarySmokeAsync(window, _viewModel, e.Args[boundarySmoke + 1], e.Args[boundarySmoke + 2]);
            Shutdown(success ? 0 : 1); return;
        }
        var p101Smoke = Array.FindIndex(e.Args, value => value == "--p101-ui-test");
        if (p101Smoke >= 0 && p101Smoke + 1 < e.Args.Length)
        {
            var success = await RunP101SmokeAsync(window, _viewModel, e.Args[p101Smoke + 1]);
            Shutdown(success ? 0 : 1); return;
        }
        var widthsSmoke = Array.FindIndex(e.Args, value => value is "--guide-widths-ui-test" or "--guide-widths-reopen-test");
        if (widthsSmoke >= 0 && widthsSmoke + 1 < e.Args.Length)
        {
            var success = await RunGuideWidthsSmokeAsync(window, _viewModel, e.Args[widthsSmoke + 1], e.Args[widthsSmoke].Contains("reopen"));
            if (success) window.Close(); else Shutdown(1);
            return;
        }
        var selectionSmoke = Array.FindIndex(e.Args, value => value == "--guide-selection-ui-test");
        if (selectionSmoke >= 0 && selectionSmoke + 1 < e.Args.Length)
        {
            var success = await RunGuideSelectionSmokeAsync(window, _viewModel, e.Args[selectionSmoke + 1]);
            Shutdown(success ? 0 : 1); return;
        }
        var layoutSmoke = Array.FindIndex(e.Args, value => value is "--panel-layout-ui-test" or "--panel-layout-reopen-test");
        if (layoutSmoke >= 0 && layoutSmoke + 1 < e.Args.Length)
        {
            var success = await RunPanelLayoutSmokeAsync(window, e.Args[layoutSmoke + 1], e.Args[layoutSmoke].Contains("reopen"));
            if (success) window.Close(); else Shutdown(1);
            return;
        }
        var p92Smoke = Array.FindIndex(e.Args, value => value.Equals("--p92-ui-smoke-test", StringComparison.OrdinalIgnoreCase));
        var p10Smoke = Array.FindIndex(e.Args, value => value.Equals("--p10-ui-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p10Smoke >= 0 && p10Smoke + 1 < e.Args.Length) { var success = await RunP10SmokeAsync(window, _viewModel, e.Args[p10Smoke + 1]); Shutdown(success ? 0 : 1); return; }
        if (p92Smoke >= 0 && p92Smoke + 1 < e.Args.Length) { var success = await RunP92SmokeAsync(window, _viewModel, e.Args[p92Smoke + 1]); Shutdown(success ? 0 : 1); return; }
        var p92Reopen = Array.FindIndex(e.Args, value => value.Equals("--p92-persistence-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p92Reopen >= 0 && p92Reopen + 1 < e.Args.Length) { var success = await RunP92ReopenAsync(e.Args[p92Reopen + 1]); Shutdown(success ? 0 : 1); return; }
        var p91Smoke = Array.FindIndex(e.Args, value => value.Equals("--p91-ui-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p91Smoke >= 0 && p91Smoke + 1 < e.Args.Length)
        { var passed = await RunP91SmokeAsync(window, _viewModel, e.Args[p91Smoke + 1]); Shutdown(passed ? 0 : 1); return; }
        var favoritesSmoke = Array.FindIndex(e.Args, value => value.Equals("--favorites-ui-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (favoritesSmoke >= 0 && favoritesSmoke + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[favoritesSmoke + 1]); Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var checks = new Dictionary<string, bool>(); string? error = null; var mapReports = new List<System.Text.Json.JsonElement>();
            object? virtualizationReport = null;
            try
            {
                var now = DateTimeOffset.UtcNow;
                var collection = await Favorites.SaveCollectionAsync(new(Guid.NewGuid(), "摄影点", 1, now, now));
                var point = new HikingGuide.Core.Favorites.FavoritePoint { Name = "观景台收藏点", Description = "中文描述\n摄影观景位置", Latitude = 23.1382, Longitude = 113.327,
                    ElevationMeters = 123.45, ElevationSourceKind = HikingGuide.Core.Favorites.FavoriteElevationSourceKind.Manual, CollectionId = collection.Id };
                using var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppLogo.png"))!.Stream;
                using var memory = new MemoryStream(); await resource.CopyToAsync(memory);
                var image = await Task.Run(() => HikingGuide.App.Services.FavoriteImageService.FromBytes(memory.ToArray(), "山脊.png"));
                var frame = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                    new byte[] { 20, 140, 240, 255, 40, 160, 220, 255, 60, 120, 200, 255, 80, 100, 180, 255 }, 8);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                using var secondMemory = new MemoryStream(); encoder.Save(secondMemory);
                var second = await Task.Run(() => HikingGuide.App.Services.FavoriteImageService.FromBytes(secondMemory.ToArray(), "第二张.png"));
                var editor = new FavoriteEditorWindow(Favorites, await Favorites.CollectionsAsync(), point, images: [image, second]) { Owner = window };
                editor.Show(); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                CaptureWindowPng(editor, Path.ChangeExtension(reportPath, ".editor.png"));
                await editor.RunAutomationSaveAsync(); checks["EditorSavedTwoImages"] = editor.Saved && (await Favorites.GetAsync(point.Id))!.Attachments.Count == 2;
                var detail = (await Favorites.GetAsync(point.Id))!; checks["LazyThumbnail"] = (await HikingGuide.App.Services.FavoriteImageService.ThumbnailAsync(Favorites, detail.Blobs[0])).PixelWidth > 0;
                await Favorites.SaveAsync(new() { Name = "船底顶备用营地", Description = "跨收藏夹搜索", Latitude = 23.1385, Longitude = 113.3275 });
                window.ShowFavoritesForAutomation();
                checks["GlobalSearchAndDropdown"] = await window.FavoritesContent.RunAutomationPanelAsync(collection.Id, "营地", 1);
                checks["ClearSearchRestoresCollection"] = await window.FavoritesContent.RunAutomationPanelAsync(collection.Id, "", 1);
                var virtualization = await window.FavoritesContent.RunVirtualizationSmokeAsync();
                checks["TenThousandCardsVirtualizedAndScroll"] = virtualization.Passed;
                virtualizationReport = new { virtualization.VisibleContainers, virtualization.Milliseconds };
                for (var i = 0; i < 200 && !mapViewModel.IsReady; i++) await Task.Delay(100);
                checks["MapReadyWithoutProject"] = mapViewModel.IsReady && _viewModel.MapContextProject is null;
                async Task<System.Text.Json.JsonElement> InspectMap()
                {
                    var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler<HikingGuide.Application.Maps.MapEventMessage> handler = (_, value) => { if (value.Type == "FavoritesSmokeCompleted") completion.TrySetResult(value.Message ?? "{}"); };
                    mapViewModel.FavoritesEvent += handler;
                    try { await mapViewModel.RunFavoritesSmokeAsync(); var json = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); using var document = System.Text.Json.JsonDocument.Parse(json); var result = document.RootElement.Clone(); mapReports.Add(result); return result; }
                    finally { mapViewModel.FavoritesEvent -= handler; }
                }
                await window.FavoritesContent.RefreshAsync(); await mapViewModel.LocateFavoriteAsync(point); await Task.Delay(700);
                var state = await InspectMap();
                checks["PurpleStarAndLocation"] = state.GetProperty("layerPresent").GetBoolean() && state.GetProperty("imagePresent").GetBoolean() &&
                    state.GetProperty("featureCount").GetInt32() == 2 && Math.Abs(state.GetProperty("latitude").GetDouble() - point.Latitude) < 1e-5 && state.GetProperty("zoom").GetDouble() >= 16;
                await mapViewModel.UpdateFavoritesAsync(await Favorites.SearchAsync(), false); var hidden = await InspectMap();
                checks["HideKeepsSearch"] = !hidden.GetProperty("visible").GetBoolean() && (await Favorites.SearchAsync(query: "营地")).Count == 1;
                await mapViewModel.UpdateFavoritesAsync(await Favorites.SearchAsync(), true);
                foreach (var project in Enumerable.Range(0, 3).Select(i => new HikingProject { Name = $"收藏点独立性项目{i}" })) await mapViewModel.SetProjectAsync(project);
                await mapViewModel.SetProjectAsync(null); var afterClose = await InspectMap();
                checks["SurvivesThreeProjectsAndClose"] = afterClose.GetProperty("featureCount").GetInt32() == 2 && (await Favorites.SearchAsync()).Count == 2;
                var package = Path.ChangeExtension(reportPath, ".hgfav"); await Favorites.ExportAsync(package);
                var target = new HikingGuide.Infrastructure.Favorites.FavoritesRepository(new HikingGuide.Infrastructure.Favorites.FavoritesPathProvider(Path.Combine(Favorites.Paths.TempPath, "smoke-import")));
                await target.InitializeAsync(); await target.ImportAsync(package);
                checks["FullPackageRoundTrip"] = (await target.SearchAsync()).Count == 2 && (await target.GetAsync(point.Id))!.Attachments.Count == 2;
                CaptureWindowPng(window, Path.ChangeExtension(reportPath, ".panel.png")); await mapViewModel.CapturePreviewAsync(Path.ChangeExtension(reportPath, ".map.png"));
            }
            catch (Exception exception) { error = exception.ToString(); }
            var passed = error is null && checks.Count >= 9 && checks.Values.All(v => v);
            await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(new { Passed = passed, Error = error, Checks = checks,
                Favorites.SqliteVersion, Favorites.TrigramAvailable, Virtualization = virtualizationReport, Maps = mapReports }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Shutdown(passed ? 0 : 1); return;
        }
        var p82UiIndex = Array.FindIndex(e.Args, value =>
            value.Equals("--p82-ui-smoke-test", StringComparison.OrdinalIgnoreCase));
        var p9Index = Array.FindIndex(e.Args, value => value.Equals("--p9b-ui-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p9Index >= 0 && p9Index + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[p9Index + 1]);
            var project = new HikingProject { Name = "P9-B界面验收副本" };
            var track = new HikingGuide.Core.Models.Track { Name = "里程分类界面测试" };
            track.Segments.Add(new TrackSegment { Points = [
                new TrackPoint { Latitude = 23, Longitude = 113, SourceElevation = 100 },
                new TrackPoint { Latitude = 23.002, Longitude = 113, SourceElevation = 108 }] });
            track.RecalculateDistances(); project.AddTrack(track);
            var passed = false; string? error = null;
            try { passed = await window.RunP9TerrainUiSmokeAsync(project, track); }
            catch (Exception exception) { error = exception.ToString(); }
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(new { Passed = passed, Error = error },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Shutdown(passed ? 0 : 1);
            return;
        }
        if (p82UiIndex >= 0 && p82UiIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[p82UiIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var longName = new string('长', 200);
            var samplePath = Path.Combine(Path.GetDirectoryName(reportPath)!, "p82-long-project.hikeproj");
            await projectFileService.SaveAsync(new HikingProject { Name = longName }, samplePath, false);
            await _viewModel.OpenForAutomationAsync(samplePath);
            window.UpdateLayout();
            await Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Loaded);
            var root = _viewModel.NavigationNodes.FirstOrDefault(node => node.Model is HikingProject);
            var projectLabel = root is null ? null : FindVisual<TextBlock>(window,
                block => ReferenceEquals(block.DataContext, root));
            var tree = FindVisual<TreeView>(window, _ => true);
            var menu = FindVisual<Menu>(window, _ => true);
            var fileMenu = menu?.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Header, "文件"));
            var fileHeaders = fileMenu?.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray() ?? [];
            var kmlPosition = Array.IndexOf(fileHeaders, "导出KML");
            var exportOrder = kmlPosition >= 0 && kmlPosition + 1 < fileHeaders.Length &&
                fileHeaders[kmlPosition + 1] == "导出KMZ";
            var contextHeaders = projectLabel?.ContextMenu?.Items.OfType<MenuItem>()
                .Select(item => item.Header?.ToString()).ToArray() ?? [];
            var contextKmlPosition = Array.IndexOf(contextHeaders, "导出KML");
            var contextExportOrder = contextKmlPosition >= 0 &&
                contextKmlPosition + 1 < contextHeaders.Length &&
                contextHeaders[contextKmlPosition + 1] == "导出KMZ";
            var trackMenu = menu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "轨迹编辑")) == true;
            var treeFixed = projectLabel?.TextTrimming == TextTrimming.None &&
                projectLabel.TextWrapping == TextWrapping.NoWrap &&
                double.IsPositiveInfinity(projectLabel.MaxWidth) &&
                projectLabel.ToolTip?.ToString()?.Contains(longName) == true &&
                tree is not null && ScrollViewer.GetHorizontalScrollBarVisibility(tree) == ScrollBarVisibility.Auto;
            var report = new
            {
                成功 = exportOrder && contextExportOrder && trackMenu && treeFixed && window.Icon is not null,
                长项目名称长度 = longName.Length,
                根节点完整单行及悬停提示 = treeFixed,
                图标 = window.Icon is not null,
                顶部轨迹编辑菜单 = trackMenu,
                文件菜单顺序 = fileHeaders,
                导出KML下方是KMZ = exportOrder,
                项目树右键KML下方是KMZ = contextExportOrder,
                项目树保留横向滚动 = tree is not null &&
                    ScrollViewer.GetHorizontalScrollBarVisibility(tree) == ScrollBarVisibility.Auto
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            Shutdown(report.成功 ? 0 : 1);
            return;
        }
        var p83TreeIndex = Array.FindIndex(e.Args, value =>
            value.Equals("--p83-tree-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p83TreeIndex >= 0 && p83TreeIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[p83TreeIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var directory = Path.GetDirectoryName(reportPath)!;
            var shortPath = Path.Combine(directory, "p83-short-project.hikeproj");
            var longPath = Path.Combine(directory, "p83-long-project.hikeproj");
            await projectFileService.SaveAsync(new HikingProject { Name = "短名称项目" }, shortPath, false);
            var sample = new HikingProject { Name = new string('项', 200) };
            var track = new HikingGuide.Core.Models.Track { Name = new string('轨', 200) };
            var part = new TrackSegment();
            part.Points.Add(new TrackPoint { Latitude = 23, Longitude = 113 });
            part.Points.Add(new TrackPoint { Latitude = 23.001, Longitude = 113.001 });
            track.Segments.Add(part);
            track.RecalculateDistances();
            sample.AddTrack(track);
            sample.Waypoints.Add(new Waypoint { Name = new string('点', 200),
                AssociatedTrackId = track.Id, Latitude = 23.0005, Longitude = 113.0005 });
            await projectFileService.SaveAsync(sample, longPath, false);
            await _viewModel.OpenForAutomationAsync(shortPath);
            var result = await window.RunP83TreeSmokeAsync(longPath);
            var report = new { 成功 = result.Success, 结果 = result };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            Shutdown(report.成功 ? 0 : 1);
            return;
        }
        var p7WorldSmokeIndex = Array.FindIndex(e.Args, value =>
            value.Equals("--p7-world-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p7WorldSmokeIndex >= 0 && p7WorldSmokeIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[p7WorldSmokeIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var available = new HashSet<BRouterTile>
            {
                BRouterTile.ParseFileName("E110_N20.rd5"),
                BRouterTile.ParseFileName("E115_N20.rd5")
            };
            var world = new WorldTileWindow(BRouterController, available) { Owner = window };
            world.Show();
            var result = await world.RunMapSmokeAsync();
            var report = new { 成功 = result.Ready && result.Osm && result.Grid &&
                result.Selected == 2, 地图加载 = result.Ready, OSM底图 = result.Osm,
                五度网格 = result.Grid, 点击加框选数量 = result.Selected };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            world.Close();
            window.ApproveAutomationClose();
            Shutdown(report.成功 ? 0 : 1);
            return;
        }
        var p7SmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p7-smoke-test", StringComparison.OrdinalIgnoreCase));
        var p7BRouterSmokeIndex = Array.FindIndex(e.Args, value =>
            value.Equals("--p7-brouter-smoke-test", StringComparison.OrdinalIgnoreCase));
        var brouterSmoke = p7BRouterSmokeIndex >= 0;
        if (brouterSmoke) p7SmokeIndex = p7BRouterSmokeIndex;
        if (p7SmokeIndex >= 0 && p7SmokeIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[p7SmokeIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var sample = new HikingProject { Name = "P7界面验收副本" };
            var samplePath = Path.ChangeExtension(reportPath, ".hikeproj");
            await projectFileService.SaveAsync(sample, samplePath, false);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => rendered.TrySetResult(true);
            await _viewModel.OpenForAutomationAsync(samplePath);
            var mapReady = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(45))) == rendered.Task;
            if (brouterSmoke) _viewModel.StartBRouterPlanCommand.Execute(null);
            else _viewModel.StartManualPlanCommand.Execute(null);
            var active = await WaitUntilAsync(() => _viewModel.IsRoutePlanningActive, TimeSpan.FromSeconds(5));
            if (active && mapReady)
            {
                await Task.Delay(300);
                var expectedPoints = 0;
                foreach (var longitude in brouterSmoke
                    ? new[] { 113.3270, 113.3275, 113.3280 } : new[] { 113d, 113.001, 113.002 })
                {
                    await mapViewModel.RunP7PlanningClickSmokeAsync(
                        brouterSmoke ? 23.1382 + (longitude - 113.3270) * 0.4 : 23,
                        longitude);
                    expectedPoints++;
                    await WaitUntilAsync(() => _viewModel.RoutePlanForAutomation?.ControlPoints.Count == expectedPoints &&
                        !_viewModel.IsBusy, TimeSpan.FromSeconds(30));
                }
            }
            var expectedLegs = 2;
            var twoLegs = await WaitUntilAsync(() =>
                _viewModel.RoutePlanForAutomation?.Legs.Count == expectedLegs,
                TimeSpan.FromSeconds(6));
            var projectUnchanged = projectService.CurrentProject?.Tracks.Count == 0;
            var dialogSaved = false;
            if (twoLegs)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) =>
                {
                    var dialog = Windows.OfType<RouteTrackInfoWindow>().FirstOrDefault();
                    if (dialog is null) return;
                    timer.Stop();
                    ((TextBox)dialog.FindName("NameInput")!).Text =
                        brouterSmoke ? "P7真实BRouter界面轨迹" : "P7手绘规划轨迹";
                    ((Button)dialog.FindName("SaveButton")!).RaiseEvent(
                        new RoutedEventArgs(ButtonBase.ClickEvent));
                    dialogSaved = dialog.DialogResult == true;
                };
                timer.Start();
                _viewModel.CompletePlanningCommand.Execute(null);
            }
            var committed = await WaitUntilAsync(() =>
                projectService.CurrentProject?.Tracks.Count == 1, TimeSpan.FromSeconds(6));
            var project = projectService.CurrentProject!;
            var validTrack = committed && project.MainGuideTrackId is null &&
                project.Tracks.Single().SourceFormat == TrackSourceFormat.RoutePlanner &&
                project.Tracks.Single().Segments.Count == 1 &&
                project.Tracks.Single().GetSplitSections().Count == 1 &&
                project.Tracks.Single().Segments[0].RouteProvenance?.Legs.Count == expectedLegs &&
                (!brouterSmoke || project.Tracks.Single().Segments[0]
                    .RouteProvenance?.BRouterRequestMetadata?.Profile == "shortest");
            var savedPath = Path.ChangeExtension(reportPath, ".edited.hikeproj");
            await projectFileService.SaveAsync(project, savedPath, false);
            var reopened = await projectFileService.LoadAsync(savedPath);
            var reopenedTrack = reopened.Tracks.Count == 1 &&
                reopened.Tracks.Single().Segments.Count == 1 &&
                reopened.Tracks.Single().GetSplitSections().Count == 1 &&
                reopened.Tracks.Single().Segments[0].RouteProvenance?.Legs.Count == expectedLegs &&
                reopened.MainGuideTrackId is null;
            var report = new
            {
                成功 = mapReady && active && twoLegs && projectUnchanged && dialogSaved &&
                    validTrack && reopenedTrack,
                地图加载 = mapReady, 进入规划模式 = active, 地图点击形成预期分段 = twoLegs,
                规划模式 = brouterSmoke ? "BRouter" : "手绘",
                提交前项目未新增轨迹 = projectUnchanged, 完成轨迹信息窗口 = dialogSaved,
                普通轨迹且未自动设主攻略 = validTrack, 保存重开 = reopenedTrack,
                测试副本 = samplePath, 保存副本 = savedPath
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            Shutdown(report.成功 ? 0 : 1);
            return;
        }
        var p61SmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p61-map-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p61SmokeIndex >= 0 && p61SmokeIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[p61SmokeIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var track = new HikingGuide.Core.Models.Track { Name = "P6-1地图分段测试轨迹" };
            foreach (var longitude in new[] { 113d, 113.01, 113.02, 113.03 })
                track.Segments.Add(new TrackSegment { Points =
                [new TrackPoint { Latitude = 23, Longitude = longitude },
                 new TrackPoint { Latitude = 23, Longitude = longitude + .006 }] });
            track.RecalculateDistances();
            var sample = new HikingProject { Name = "P6-1地图交互验收样本" };
            sample.AddTrack(track);
            var other = new HikingGuide.Core.Models.Track { Name = "第二条轨迹", Segments =
            [new TrackSegment { Points =
            [new TrackPoint { Latitude = 23.01, Longitude = 113 },
             new TrackPoint { Latitude = 23.01, Longitude = 113.03 }] }] };
            other.RecalculateDistances();
            sample.AddTrack(other, false);
            var samplePath = Path.ChangeExtension(reportPath, ".hikeproj");
            await projectFileService.SaveAsync(sample, samplePath, false);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => rendered.TrySetResult(true);
            var contextCount = 0;
            mapViewModel.RightClicked += (_, message) =>
            {
                if (message.Type == "SplitSectionContextMenuRequested") contextCount++;
            };
            await _viewModel.OpenForAutomationAsync(samplePath);
            var mapReady = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(45))) == rendered.Task;
            var opened = projectService.CurrentProject!;
            var openedTrack = opened.Tracks.First();
            var sections = openedTrack.GetSplitSections();
            var single = false; var multi = false; var toggled = false; var rightClickKept = false;
            var editModeSelection = false; var treeHighlighted = false;
            var mapClickDoesNotFocus = false;
            var panelChecks = new Dictionary<string, bool>();
            if (mapReady)
            {
                await Task.Delay(650);
                var focusBeforeClick = mapViewModel.FocusObjectRequestCount;
                var selectionBeforeClick = _viewModel.MapSelectionSyncCount;
                await mapViewModel.RunP62TrackClickSmokeAsync(openedTrack.Id);
                var trackClickNoFocus = await WaitUntilAsync(() =>
                    _viewModel.MapSelectionSyncCount > selectionBeforeClick, TimeSpan.FromSeconds(5)) &&
                    mapViewModel.FocusObjectRequestCount == focusBeforeClick;
                await mapViewModel.RunP61SplitSectionSmokeAsync(sections[0].Id);
                single = await WaitUntilAsync(() => _viewModel.SelectedSplitSectionIds.SequenceEqual([sections[0].Id]),
                    TimeSpan.FromSeconds(5));
                mapClickDoesNotFocus = trackClickNoFocus &&
                    mapViewModel.FocusObjectRequestCount == focusBeforeClick;
                await mapViewModel.RunP61SplitSectionSmokeAsync(sections[2].Id, true);
                await mapViewModel.RunP61SplitSectionSmokeAsync(sections[3].Id, true);
                multi = await WaitUntilAsync(() => _viewModel.SelectedSplitSectionIds.Count == 3,
                    TimeSpan.FromSeconds(5));
                treeHighlighted = _viewModel.NavigationNodes.Single().Children
                    .SelectMany(node => node.Children).SelectMany(group => group.Children)
                    .Count(node => node.IsMultiSelected && node.Model is SplitSection) == 3;
                await mapViewModel.RunP61SplitSectionSmokeAsync(sections[3].Id, true);
                toggled = await WaitUntilAsync(() => _viewModel.SelectedSplitSectionIds.Count == 2,
                    TimeSpan.FromSeconds(5));
                await mapViewModel.RunP61SplitSectionSmokeAsync(sections[2].Id, contextMenu: true);
                rightClickKept = await WaitUntilAsync(() => contextCount == 1,
                    TimeSpan.FromSeconds(5)) && _viewModel.SelectedSplitSectionIds.Count == 2;
                var trackNode = _viewModel.NavigationNodes.Single().Children.First(node =>
                    ReferenceEquals(node.Model, openedTrack));
                _viewModel.SelectNavigationNode(trackNode, false);
                if (_viewModel.EnterTrackEditCommand.CanExecute(null))
                    _viewModel.EnterTrackEditCommand.Execute(null);
                if (await WaitUntilAsync(() => _viewModel.IsTrackEditActive, TimeSpan.FromSeconds(8)))
                {
                    await mapViewModel.RunP61SplitSectionSmokeAsync(sections[1].Id);
                    editModeSelection = await WaitUntilAsync(() =>
                        _viewModel.SelectedSplitSectionIds.SequenceEqual([sections[1].Id]),
                        TimeSpan.FromSeconds(5));
                }
                await mapViewModel.CapturePreviewAsync(Path.ChangeExtension(reportPath, ".png"));
                foreach (var splitMode in new[] { false, true })
                    foreach (var connect in new[] { false, true })
                    {
                        object? focused = null;
                        GeoCoordinate? endpoint = null;
                        var panel = splitMode
                            ? new TrackMergeWindow(openedTrack, connect, null,
                                geometry => focused = geometry, coordinate => endpoint = coordinate)
                            : new TrackMergeWindow(opened, connect, null,
                                geometry => focused = geometry, coordinate => endpoint = coordinate);
                        panel.Owner = window;
                        panel.Show();
                        var list = (ListBox)panel.FindName("TrackList")!;
                        list.SelectedIndex = 0;
                        var first = list.Items[0];
                        var focusedCorrectly = splitMode
                            ? focused is SplitSection focusedSection && focusedSection.Id == sections[0].Id
                            : ReferenceEquals(focused, openedTrack);
                        var expectedStart = splitMode ? sections[0].StartPoint!.Coordinate : openedTrack.StartPoint!.Coordinate;
                        var expectedEnd = splitMode ? sections[0].EndPoint!.Coordinate : openedTrack.EndPoint!.Coordinate;
                        var startButton = (Button)panel.FindName("StartButton")!;
                        var endButton = (Button)panel.FindName("EndButton")!;
                        startButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        var forwardStart = endpoint == expectedStart;
                        first.GetType().GetProperty("IsReversed")!.SetValue(first, true);
                        endpoint = null;
                        startButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        var reverseStart = endpoint == expectedEnd;
                        endpoint = null;
                        endButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        var reverseEnd = endpoint == expectedStart;
                        panelChecks[(splitMode ? "分段" : "轨迹") + (connect ? "连接" : "合并")] =
                            focusedCorrectly && forwardStart && reverseStart && reverseEnd;
                        panel.Close();
                    }
            }
            var report = new
            {
                成功 = mapReady && single && multi && toggled && rightClickKept && mapClickDoesNotFocus &&
                    editModeSelection && treeHighlighted && panelChecks.Count == 4 && panelChecks.Values.All(value => value),
                地图加载 = mapReady, 普通单选 = single, Ctrl多选 = multi,
                地图左击轨迹和拆分分段不自动定位 = mapClickDoesNotFocus,
                Ctrl取消 = toggled, 右击保留多选 = rightClickKept,
                项目树多项高亮 = treeHighlighted, 编辑模式选择 = editModeSelection,
                右键消息次数 = contextCount, 四个面板 = panelChecks, 测试副本 = samplePath
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            Shutdown(report.成功 ? 0 : 1);
            return;
        }
        var p62SmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p62-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p62SmokeIndex >= 0 && p62SmokeIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[p62SmokeIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var sample = new HikingProject { Name = "P6-2属性及右击拆分验收样本" };
            var track = new HikingGuide.Core.Models.Track { Name = "原轨迹", Segments =
            [
                new TrackSegment { Points =
                [new TrackPoint { Latitude = 23, Longitude = 113 },
                 new TrackPoint { Latitude = 23, Longitude = 113.006 }] },
                new TrackSegment { Points =
                [new TrackPoint { Latitude = 23, Longitude = 113.01 },
                 new TrackPoint { Latitude = 23, Longitude = 113.016 }] }
            ] };
            track.RecalculateDistances();
            sample.AddTrack(track);
            var guideService = new GuideStructureService();
            foreach (var longitude in new[] { 113.002, 113.012 })
            {
                var node = new GuideNode { Name = $"节点{longitude}", TrackId = track.Id,
                    OriginalCoordinate = new GeoCoordinate(23, longitude) };
                guideService.SnapNode(node, track);
                sample.GuideNodes.Add(node);
            }
            GuideStructureService.ReplaceSegments(sample, guideService.RebuildSegments(sample));
            var samplePath = Path.ChangeExtension(reportPath, ".hikeproj");
            await projectFileService.SaveAsync(sample, samplePath, false);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => rendered.TrySetResult(true);
            await _viewModel.OpenForAutomationAsync(samplePath);
            var mapReady = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(45))) == rendered.Task;
            var opened = projectService.CurrentProject!;
            track = opened.Tracks.Single();
            var guide = opened.GuideSegments.Single();
            var editTrack = EditItem(track, "轨迹改名", "轨迹用户说明", null, null);
            var section = track.GetSplitSections()[0];
            var editSplit = EditItem(section, "拆分段改名", "拆分段用户说明", null, null);
            var editGuide = EditItem(guide, "攻略段改名", "攻略段人工描述", "石阶", "45");
            var edited = editTrack && editSplit && editGuide && track.Name == "轨迹改名" &&
                track.Description == "轨迹用户说明" &&
                track.GetSplitSections()[0].Name == "拆分段改名" &&
                track.Segments[0].SplitSectionDescription == "拆分段用户说明" &&
                guide.CustomName == "攻略段改名" && guide.ManualData?.RoadCondition == "石阶" &&
                guide.ManualData?.Description == "攻略段人工描述" &&
                guide.ManualData?.PlannedDuration == TimeSpan.FromMinutes(45);
            MapEventMessage? context = null;
            mapViewModel.RightClicked += (_, value) =>
            {
                if (value.Type == "SplitSectionContextMenuRequested") context = value;
            };
            var currentTrackNode = _viewModel.NavigationNodes.Single().Children.First(node => ReferenceEquals(node.Model, track));
            _viewModel.SelectNavigationNode(currentTrackNode, false);
            await Task.Delay(500);
            if (_viewModel.EnterTrackEditCommand.CanExecute(null)) _viewModel.EnterTrackEditCommand.Execute(null);
            var editMode = await WaitUntilAsync(() => _viewModel.IsTrackEditActive, TimeSpan.FromSeconds(8));
            if (editMode) await Task.Delay(300);
            if (editMode && mapReady)
                await mapViewModel.RunP62RightClickSmokeAsync(track.Id, track.GetSplitSections()[0].Id,
                    23, 113.003);
            var rightClickReceived = await WaitUntilAsync(() => context is not null, TimeSpan.FromSeconds(6));
            var directCommandEnabled = context is not null &&
                _viewModel.SplitTrackSegmentCommand.CanExecute(context);
            var oldRevision = track.GeometryRevision;
            var oldPartCount = track.Segments.Count;
            if (directCommandEnabled)
            {
                _viewModel.AutoConfirmTrackEditForAutomation = true;
                _viewModel.SplitTrackSegmentCommand.Execute(context);
                await WaitUntilAsync(() => track.GeometryRevision > oldRevision, TimeSpan.FromSeconds(12));
                _viewModel.AutoConfirmTrackEditForAutomation = false;
            }
            var splitAtRightClick = track.GeometryRevision == oldRevision + 1 &&
                track.Segments.Count == oldPartCount + 1 &&
                track.Segments[0].EndPoint is { } splitEnd &&
                Math.Abs(splitEnd.Longitude - 113.003) < 0.0001;
            var preservedManual = opened.GuideSegments.Concat(opened.RetiredGuideSegments).Any(item =>
                item.Id == guide.Id && item.CustomName == "攻略段改名" &&
                item.ManualData?.Description == "攻略段人工描述");
            var savedPath = Path.ChangeExtension(reportPath, ".edited.hikeproj");
            await projectFileService.SaveAsync(opened, savedPath, false);
            var reopened = await projectFileService.LoadAsync(savedPath);
            var reopenedFields = reopened.Tracks.Single().Name == "轨迹改名" &&
                reopened.Tracks.Single().GetSplitSections()[0].Name == "拆分段改名" &&
                reopened.GuideSegments.Concat(reopened.RetiredGuideSegments).Any(item =>
                    item.CustomName == "攻略段改名" && item.ManualData?.RoadCondition == "石阶");
            var report = new
            {
                成功 = mapReady && edited && editMode && rightClickReceived && directCommandEnabled &&
                    splitAtRightClick && preservedManual && reopenedFields,
                地图加载 = mapReady, 三类属性窗口编辑 = edited, 编辑模式 = editMode,
                右击坐标事件 = rightClickReceived, 右击拆分命令可执行 = directCommandEnabled,
                无需第二次左击即拆分 = splitAtRightClick, 人工内容保留 = preservedManual,
                保存重开属性保留 = reopenedFields, 测试副本 = samplePath, 保存副本 = savedPath
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            Shutdown(report.成功 ? 0 : 1);
            return;

            bool EditItem(object model, string name, string description, string? road, string? duration)
            {
                var command = _viewModel.EditProjectGeometryItemCommand;
                if (!command.CanExecute(model)) return false;
                var saved = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) =>
                {
                    var dialog = Windows.OfType<ProjectItemEditorWindow>().FirstOrDefault();
                    if (dialog is null) return;
                    timer.Stop();
                    ((TextBox)dialog.FindName("NameInput")!).Text = name;
                    ((TextBox)dialog.FindName("DescriptionInput")!).Text = description;
                    if (road is not null) ((TextBox)dialog.FindName("RoadConditionInput")!).Text = road;
                    if (duration is not null) ((TextBox)dialog.FindName("DurationInput")!).Text = duration;
                    ((Button)dialog.FindName("SaveButton")!).RaiseEvent(
                        new RoutedEventArgs(ButtonBase.ClickEvent));
                    saved = dialog.DialogResult == true;
                };
                timer.Start();
                command.Execute(model);
                timer.Stop();
                return saved;
            }
        }
        var p6SmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p6-track-edit-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p6SmokeIndex >= 0 && p6SmokeIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[p6SmokeIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var track = new HikingGuide.Core.Models.Track { Name = "P6地图编辑测试轨迹", Segments =
            [
                new TrackSegment { Points =
                [
                    new TrackPoint { Latitude = 23, Longitude = 113 },
                    new TrackPoint { Latitude = 23, Longitude = 113.01, SourceElevation = 120 },
                    new TrackPoint { Latitude = 23, Longitude = 113.02 },
                    new TrackPoint { Latitude = 23, Longitude = 113.03 }
                ] }
            ] };
            track.RecalculateDistances();
            var sample = new HikingProject { Name = "P6界面验收样本" };
            sample.AddTrack(track);
            var guides = new GuideStructureService();
            foreach (var longitude in new[] { 113.005, 113.025 })
            {
                var node = new GuideNode { TrackId = track.Id,
                    OriginalCoordinate = new GeoCoordinate(23, longitude) };
                guides.SnapNode(node, track);
                sample.GuideNodes.Add(node);
            }
            GuideStructureService.ReplaceSegments(sample, guides.RebuildSegments(sample));
            var samplePath = Path.ChangeExtension(reportPath, ".hikeproj");
            await projectFileService.SaveAsync(sample, samplePath, false);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => rendered.TrySetResult(true);
            await _viewModel.OpenForAutomationAsync(samplePath);
            var mapReady = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(45))) == rendered.Task;
            var opened = projectService.CurrentProject!;
            var target = opened.Tracks.Single();
            var originalFingerprint = new TrackSnapService().GetGeometryFingerprint(target);
            var targetPoint = target.Segments[0].Points[1];
            var pointId = targetPoint.Id;
            var oldCoordinate = targetPoint.Coordinate;
            var newCoordinate = new GeoCoordinate(oldCoordinate.Latitude + 0.0001, oldCoordinate.Longitude);
            var trackNode = _viewModel.NavigationNodes.Single().Children.First(node =>
                ReferenceEquals(node.Model, target));
            _viewModel.SelectNavigationNode(trackNode, false);
            if (mapReady && _viewModel.EnterTrackEditCommand.CanExecute(null))
                _viewModel.EnterTrackEditCommand.Execute(null);
            var modeEntered = await WaitUntilAsync(() => _viewModel.IsTrackEditActive,
                TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            if (modeEntered) await mapViewModel.RunP6TrackEditSmokeAsync(pointId,
                oldCoordinate, newCoordinate);
            var selectedRealPoint = await WaitUntilAsync(() =>
                _viewModel.SelectedTrackEditPointId == pointId, TimeSpan.FromSeconds(2));
            var dragCommitted = await WaitUntilAsync(() => target.GeometryRevision == 1 &&
                target.Segments[0].Points[1].Coordinate == newCoordinate, TimeSpan.FromSeconds(15));
            var sourceMetadataKept = target.Segments[0].Points[1].SourceElevation == 120 &&
                target.Segments[0].Points[1].IsCoordinateEdited;
            await WaitUntilAsync(() => !_viewModel.IsBusy, TimeSpan.FromSeconds(10));
            if (dragCommitted && _viewModel.UndoCommand.CanExecute(null)) _viewModel.UndoCommand.Execute(null);
            var undoRestored = target.GeometryRevision == 0 &&
                new TrackSnapService().GetGeometryFingerprint(target) == originalFingerprint;
            if (undoRestored && _viewModel.RedoCommand.CanExecute(null)) _viewModel.RedoCommand.Execute(null);
            var redoRestored = target.GeometryRevision == 1 &&
                target.Segments[0].Points[1].Coordinate == newCoordinate;
            await Task.Delay(500);
            if (_viewModel.ExitTrackEditCommand.CanExecute(null)) _viewModel.ExitTrackEditCommand.Execute(null);
            var modeExited = await WaitUntilAsync(() => !_viewModel.IsTrackEditActive,
                TimeSpan.FromSeconds(5));
            var screenshot = Path.ChangeExtension(reportPath, ".png");
            if (mapReady) await mapViewModel.CapturePreviewAsync(screenshot);
            var report = new
            {
                成功 = mapReady && modeEntered && selectedRealPoint && dragCommitted &&
                    sourceMetadataKept && undoRestored && redoRestored && modeExited,
                地图加载 = mapReady,
                编辑模式进入 = modeEntered,
                完整轨迹真实点选择 = selectedRealPoint,
                拖动提交一次Revision = dragCommitted,
                来源元数据保留且标记坐标已编辑 = sourceMetadataKept,
                撤销恢复几何及Revision = undoRestored,
                重做恢复编辑结果 = redoRestored,
                编辑模式退出 = modeExited,
                地图截图 = screenshot,
                测试项目 = samplePath
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            Shutdown(report.成功 ? 0 : 1);
            return;
        }
        var workspaceSmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p52-workspace-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (workspaceSmokeIndex >= 0 && workspaceSmokeIndex + 3 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[workspaceSmokeIndex + 1]);
            var firstPath = Path.GetFullPath(e.Args[workspaceSmokeIndex + 2]);
            var secondPath = Path.GetFullPath(e.Args[workspaceSmokeIndex + 3]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await _viewModel.OpenForAutomationAsync(firstPath);
            var first = projectService.CurrentProject ?? throw new InvalidOperationException("首个项目未打开。");
            await _viewModel.OpenForAutomationAsync(secondPath);
            var second = projectService.CurrentProject ?? throw new InvalidOperationException("第二个项目未打开。");
            var twoRoots = _viewModel.NavigationNodes.Count == 2;
            var firstRoot = _viewModel.NavigationNodes.First(node => ReferenceEquals(node.Model, first));
            var firstTrack = firstRoot.Children.First(node => node.Model is HikingGuide.Core.Models.Track);
            var mainBefore = first.MainGuideTrackId;
            var render = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => render.TrySetResult(true);
            _viewModel.SelectNavigationNode(firstTrack);
            var switchedByChild = ReferenceEquals(projectService.CurrentProject, first);
            var rendered = await Task.WhenAny(render.Task, Task.Delay(TimeSpan.FromSeconds(30))) == render.Task;
            var mainUnchanged = first.MainGuideTrackId == mainBefore;
            await _viewModel.OpenForAutomationAsync(firstPath);
            var noDuplicate = projectService.OpenProjects.Count == 2;
            var capture = Path.ChangeExtension(reportPath, ".png");
            if (rendered) await mapViewModel.CapturePreviewAsync(capture);
            var dense = new HikingProject { Name = "P5-2千标注性能样本" };
            for (var index = 0; index < 3; index++)
            {
                var track = new HikingGuide.Core.Models.Track { Name = $"测试轨迹{index + 1}" };
                track.Segments.Add(new TrackSegment { Points =
                [
                    new TrackPoint { Latitude = 23 + index * 0.01, Longitude = 113 },
                    new TrackPoint { Latitude = 23 + index * 0.01, Longitude = 113.01 }
                ] });
                track.RecalculateDistances();
                dense.AddTrack(track);
            }
            for (var index = 0; index < 1000; index++)
                dense.Waypoints.Add(new Waypoint { Name = $"标注{index + 1}", Latitude = 23 + index * 0.00001,
                    Longitude = 113.005, AssociatedTrackId = dense.Tracks[index % 3].Id });
            var densePath = Path.Combine(Path.GetDirectoryName(reportPath)!, $"p52-dense-{Guid.NewGuid():N}.hikeproj");
            await projectFileService.SaveAsync(dense, densePath, false);
            var treeTimer = Stopwatch.StartNew();
            await _viewModel.OpenForAutomationAsync(densePath);
            var denseLoaded = projectService.CurrentProject!;
            var denseRoot = _viewModel.NavigationNodes.Single(node => ReferenceEquals(node.Model, denseLoaded));
            foreach (var trackNode in denseRoot.Children.Where(node => node.Model is HikingGuide.Core.Models.Track))
            { trackNode.IsExpanded = true; trackNode.IsExpanded = false; trackNode.IsExpanded = true; }
            treeTimer.Stop();
            var denseTreeReady = denseRoot.Children.Count(node => node.Model is HikingGuide.Core.Models.Track) == 3 &&
                denseRoot.Children.SelectMany(node => node.Children).SelectMany(node => node.Children)
                    .Count(node => node.Model is Waypoint) == 1000;
            var denseTrackNodes = denseRoot.Children.Where(node => node.Model is HikingGuide.Core.Models.Track).ToArray();
            _viewModel.UpdateProjectItemSelection(denseTrackNodes[0], System.Windows.Input.ModifierKeys.None);
            _viewModel.UpdateProjectItemSelection(denseTrackNodes[1], System.Windows.Input.ModifierKeys.Control);
            var multiSelected = _viewModel.SelectedProjectItemCount == 2 &&
                denseTrackNodes[0].IsMultiSelected && denseTrackNodes[1].IsMultiSelected;
            var oldColors = denseLoaded.Tracks.Select(track => track.DisplayColor).ToArray();
            _viewModel.RecolorSelectedTracksCommand.Execute(null);
            var batchRecolored = denseLoaded.Tracks.Take(2).All(track => TrackDisplayColor.IsValid(track.DisplayColor)) &&
                denseLoaded.Tracks[0].DisplayColor != denseLoaded.Tracks[1].DisplayColor;
            _viewModel.UndoCommand.Execute(null);
            var colorUndo = denseLoaded.Tracks.Select(track => track.DisplayColor).SequenceEqual(oldColors);
            var pickerPath = Path.ChangeExtension(reportPath, ".color-picker.png");
            var picker = new TrackColorWindow("#9DD410") { Owner = window };
            picker.Show();
            await Task.Delay(200);
            picker.UpdateLayout();
            var pickerImage = new RenderTargetBitmap((int)picker.ActualWidth, (int)picker.ActualHeight,
                96, 96, PixelFormats.Pbgra32);
            pickerImage.Render(picker);
            var pickerEncoder = new PngBitmapEncoder();
            pickerEncoder.Frames.Add(BitmapFrame.Create(pickerImage));
            await using (var pickerFile = File.Create(pickerPath)) pickerEncoder.Save(pickerFile);
            picker.Close();
            var pickerRendered = File.Exists(pickerPath) && new FileInfo(pickerPath).Length > 1000;

            var conversionRoot = _viewModel.NavigationNodes.Single(node => ReferenceEquals(node.Model, denseLoaded));
            var ordinaryNodes = conversionRoot.Children.First(node => node.Model is HikingGuide.Core.Models.Track)
                .Children.First(node => node.DisplayName.StartsWith("普通标注", StringComparison.Ordinal))
                .Children.Take(2).ToArray();
            _viewModel.UpdateProjectItemSelection(ordinaryNodes[0], System.Windows.Input.ModifierKeys.None);
            _viewModel.UpdateProjectItemSelection(ordinaryNodes[1], System.Windows.Input.ModifierKeys.Control);
            var forwardMenuReady = _viewModel.ConvertSelectedWaypointsCommand.CanExecute(null);
            _viewModel.ConvertSelectedWaypointsCommand.Execute(null);
            for (var attempt = 0; attempt < 100 && (_viewModel.IsBusy || denseLoaded.GuideNodes.Count != 2); attempt++)
                await Task.Delay(100);
            var forwardConverted = denseLoaded.GuideNodes.Count == 2 && denseLoaded.Waypoints.Count == 998;
            var guideRoot = _viewModel.NavigationNodes.Single(node => ReferenceEquals(node.Model, denseLoaded));
            var guideTrackNode = guideRoot.Children.First(node => node.Model is HikingGuide.Core.Models.Track);
            var segmentNames = guideTrackNode.Children.First(node => node.DisplayName.StartsWith("攻略分段", StringComparison.Ordinal))
                .Children.Select(node => node.DisplayName).ToArray();
            var segmentNamesReady = segmentNames.Length == 1 && segmentNames[0].Contains(" → ") &&
                segmentNames[0].EndsWith(" km", StringComparison.Ordinal);
            var guideNodes = guideTrackNode.Children.First(node => node.DisplayName.StartsWith("攻略节点", StringComparison.Ordinal))
                .Children.Take(2).ToArray();
            _viewModel.UpdateProjectItemSelection(guideNodes[0], System.Windows.Input.ModifierKeys.None);
            _viewModel.UpdateProjectItemSelection(guideNodes[1], System.Windows.Input.ModifierKeys.Control);
            var reverseMenuReady = _viewModel.ConvertSelectedGuideNodesCommand.CanExecute(null);
            _viewModel.ConvertSelectedGuideNodesCommand.Execute(null);
            for (var attempt = 0; attempt < 100 && (_viewModel.IsBusy || denseLoaded.GuideNodes.Count != 0); attempt++)
                await Task.Delay(100);
            var reverseConverted = denseLoaded.GuideNodes.Count == 0 && denseLoaded.Waypoints.Count == 1000;
            _viewModel.UndoCommand.Execute(null);
            var reverseUndo = denseLoaded.GuideNodes.Count == 2 && denseLoaded.Waypoints.Count == 998;
            var universalRoot = _viewModel.NavigationNodes.Single(node => ReferenceEquals(node.Model, denseLoaded));
            var universalTrack = universalRoot.Children.First(node => node.Model is HikingGuide.Core.Models.Track);
            var universalGuideGroup = universalTrack.Children.First(node => node.DisplayName.StartsWith("攻略节点", StringComparison.Ordinal));
            var universalSegmentGroup = universalTrack.Children.First(node => node.DisplayName.StartsWith("攻略分段", StringComparison.Ordinal));
            var universalWaypointGroup = universalTrack.Children.First(node => node.DisplayName.StartsWith("普通标注", StringComparison.Ordinal));
            var universalSplitGroup = universalTrack.Children.First(node => node.DisplayName.StartsWith("拆分分段", StringComparison.Ordinal));
            universalRoot.IsExpanded = true;
            universalTrack.IsExpanded = true;
            universalGuideGroup.IsExpanded = true;
            universalSegmentGroup.IsExpanded = true;
            universalWaypointGroup.IsExpanded = true;
            universalSplitGroup.IsExpanded = true;
            var selectedKinds = new[] { universalRoot, universalTrack, universalGuideGroup,
                universalGuideGroup.Children[0], universalSegmentGroup,
                universalSegmentGroup.Children[0], universalWaypointGroup,
                universalWaypointGroup.Children[0], universalSplitGroup,
                universalSplitGroup.Children[0] };
            _viewModel.UpdateProjectItemSelection(selectedKinds[0], System.Windows.Input.ModifierKeys.None);
            foreach (var item in selectedKinds.Skip(1))
                _viewModel.UpdateProjectItemSelection(item, System.Windows.Input.ModifierKeys.Control);
            var allKindsSelected = _viewModel.SelectedProjectItemCount == selectedKinds.Length &&
                selectedKinds.All(item => item.IsMultiSelected) &&
                _viewModel.SelectedSplitSectionIds.Contains(((SplitSection)universalSplitGroup.Children[0].Model!).Id);
            _viewModel.UpdateProjectItemSelection(universalGuideGroup, System.Windows.Input.ModifierKeys.None);
            _viewModel.UpdateProjectItemSelection(universalSegmentGroup, System.Windows.Input.ModifierKeys.Shift);
            var shiftIncludesHeadersAndChildren = _viewModel.SelectedProjectItemCount == 4 &&
                universalGuideGroup.IsMultiSelected &&
                universalGuideGroup.Children.All(item => item.IsMultiSelected) &&
                universalSegmentGroup.IsMultiSelected;
            _viewModel.UpdateProjectItemSelection(universalGuideGroup, System.Windows.Input.ModifierKeys.Control);
            var ctrlTogglesHeader = !universalGuideGroup.IsMultiSelected &&
                _viewModel.SelectedProjectItemCount == 3;
            _viewModel.UpdateProjectItemSelection(universalTrack, System.Windows.Input.ModifierKeys.Control);
            _viewModel.RecolorSelectedTracksCommand.Execute(null);
            var refreshedRoot = _viewModel.NavigationNodes.Single(node => ReferenceEquals(node.Model, denseLoaded));
            var refreshedTrack = refreshedRoot.Children.First(node => node.Model is HikingGuide.Core.Models.Track);
            var refreshedSegmentGroup = refreshedTrack.Children.First(node => node.DisplayName.StartsWith("攻略分段", StringComparison.Ordinal));
            var selectionSurvivesRefresh = refreshedSegmentGroup.IsMultiSelected &&
                refreshedTrack.IsMultiSelected && _viewModel.SelectedProjectItemCount == 4;
            _viewModel.UndoCommand.Execute(null);
            var projectPanel = FindVisual<GroupBox>(window, box => Equals(box.Header, "项目与节点"));
            var emptyAreaMenu = projectPanel?.ContextMenu;
            if (emptyAreaMenu is not null)
            {
                emptyAreaMenu.PlacementTarget = projectPanel;
                emptyAreaMenu.IsOpen = true;
                await Task.Delay(100);
            }
            var emptyAreaMenuReady = emptyAreaMenu?.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, "新建项目") && item.IsEnabled) == true &&
                emptyAreaMenu.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, "打开项目") && item.IsEnabled);
            if (emptyAreaMenu is not null) emptyAreaMenu.IsOpen = false;
            var secondRoot = _viewModel.NavigationNodes.Single(node => ReferenceEquals(node.Model, second));
            var projectLabel = FindVisual<TextBlock>(window, block => ReferenceEquals(block.DataContext, secondRoot));
            var projectMenu = projectLabel?.ContextMenu;
            if (projectMenu is not null)
            {
                projectMenu.PlacementTarget = projectLabel;
                projectMenu.IsOpen = true;
                await Task.Delay(100);
            }
            var rootCloseMenuReady = projectMenu?.Items.OfType<MenuItem>().Any(item =>
                Equals(item.Header, "关闭项目") && item.IsVisible && item.IsEnabled) == true;
            if (projectMenu is not null) projectMenu.IsOpen = false;
            second.MarkSaved();
            if (_viewModel.CloseProjectContextCommand.CanExecute(second))
                _viewModel.CloseProjectContextCommand.Execute(second);
            var inactiveProjectClosed = await WaitUntilAsync(() =>
                !projectService.OpenProjects.Any(item => ReferenceEquals(item.Project, second)), TimeSpan.FromSeconds(10));
            var report = new
            {
                成功 = twoRoots && switchedByChild && rendered && mainUnchanged && noDuplicate && denseTreeReady &&
                    multiSelected && batchRecolored && colorUndo && pickerRendered && forwardMenuReady &&
                    forwardConverted && segmentNamesReady && reverseMenuReady && reverseConverted && reverseUndo &&
                    emptyAreaMenuReady && rootCloseMenuReady && inactiveProjectClosed &&
                    allKindsSelected && shiftIncludesHeadersAndChildren && ctrlTogglesHeader &&
                    selectionSurvivesRefresh,
                真实项目同时打开数 = 2,
                项目树有两个根 = twoRoots,
                跨项目子对象切换 = switchedByChild,
                地图重新渲染 = rendered,
                选中轨迹未改变主攻略轨迹 = mainUnchanged,
                同一路径不重复打开 = noDuplicate,
                三轨迹千标注树 = denseTreeReady,
                项目树多选 = multiSelected,
                批量颜色不同 = batchRecolored,
                批量颜色撤销 = colorUndo,
                颜色选择器已渲染 = pickerRendered,
                颜色选择器截图 = pickerPath,
                批量转攻略菜单可用 = forwardMenuReady,
                批量转攻略结果 = forwardConverted,
                分段名称显示节点与距离 = segmentNamesReady,
                批量转普通菜单可用 = reverseMenuReady,
                批量转普通结果 = reverseConverted,
                批量转普通撤销 = reverseUndo,
                项目面板全部类型可多选 = allKindsSelected,
                Shift跨分组选择 = shiftIncludesHeadersAndChildren,
                Ctrl取消分组选择 = ctrlTogglesHeader,
                多选跨项目树刷新保持 = selectionSurvivesRefresh,
                项目面板空白菜单可用 = emptyAreaMenuReady,
                项目右键关闭菜单可用 = rootCloseMenuReady,
                右键关闭非当前项目 = inactiveProjectClosed,
                千标注树加载与展开毫秒 = treeTimer.Elapsed.TotalMilliseconds,
                地图截图 = rendered ? capture : null
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            Shutdown(report.成功 ? 0 : 1);
            return;
        }
        var mediaSmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p51-media-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (mediaSmokeIndex >= 0 && mediaSmokeIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[mediaSmokeIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var timings = new Dictionary<string, double>();
            var memory = new Dictionary<string, long>();
            void RecordMemory(string phaseName) => memory[phaseName] = Process.GetCurrentProcess().WorkingSet64;
            var phaseTimer = Stopwatch.StartNew();
            RecordMemory("项目打开前");
            var sampleStem = "p51-media-smoke-" + Guid.NewGuid().ToString("N");
            var kmzPath = Path.Combine(Path.GetDirectoryName(reportPath)!, sampleStem + ".kmz");
            var projectPath = Path.Combine(Path.GetDirectoryName(reportPath)!, sampleStem + ".hikeproj");
            var pixels = new byte[64 * 64 * 4];
            for (var pixelIndex = 0; pixelIndex < pixels.Length; pixelIndex += 4)
            { pixels[pixelIndex] = 220; pixels[pixelIndex + 1] = 150; pixels[pixelIndex + 2] = 40; pixels[pixelIndex + 3] = 255; }
            var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var pngBuffer = new MemoryStream();
            encoder.Save(pngBuffer);
            var png = pngBuffer.ToArray();
            using (var zip = ZipFile.Open(kmzPath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("doc.kml").Open(), Encoding.UTF8))
                    writer.Write("<kml><Document><Placemark><name>带图片标注</name><description><![CDATA[说明 <img src='files/sample.png'> https://example.com/image?id=1]]></description><Point><coordinates>113.3,23.1</coordinates></Point></Placemark></Document></kml>");
                using var image = zip.CreateEntry("files/sample.png").Open();
                image.Write(png);
            }
            phaseTimer.Restart();
            var parsed = await Task.Run(() => trackFileService.ImportAsync(kmzPath, TrackFileFormat.Kmz));
            timings["KMZ解析毫秒"] = phaseTimer.Elapsed.TotalMilliseconds;
            if (!parsed.IsSuccess) throw new InvalidOperationException(string.Join("；", parsed.Errors.Select(item => item.Message)));
            var sample = new HikingProject { Name = "P5-1媒体验收样本" };
            new ProjectImportService().Commit(ProjectImportSession.Create(parsed, kmzPath, sample), sample, true);
            phaseTimer.Restart();
            await Task.Run(() => projectFileService.SaveAsync(sample, projectPath, false));
            timings["首次项目保存毫秒"] = phaseTimer.Elapsed.TotalMilliseconds;
            File.Delete(kmzPath);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thumbnailReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => rendered.TrySetResult(true);
            mapViewModel.MediaThumbnailReady += (_, _) => thumbnailReady.TrySetResult(true);
            phaseTimer.Restart();
            await _viewModel.OpenForAutomationAsync(projectPath);
            timings["项目打开毫秒"] = phaseTimer.Elapsed.TotalMilliseconds;
            RecordMemory("项目打开后");
            var mapLoaded = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(45))) == rendered.Task;
            RecordMemory("地图加载后");
            phaseTimer.Restart();
            if (mapLoaded) await mapViewModel.RunP51MediaSmokeAsync();
            var thumbnailLoaded = mapLoaded && await Task.WhenAny(thumbnailReady.Task, Task.Delay(TimeSpan.FromSeconds(20))) == thumbnailReady.Task;
            timings["缩略图首次显示毫秒"] = phaseTimer.Elapsed.TotalMilliseconds;
            RecordMemory("缩略图显示后");
            if (thumbnailLoaded) await Task.Delay(450);
            var screenshot = Path.ChangeExtension(reportPath, ".png");
            if (mapLoaded) await mapViewModel.CapturePreviewAsync(screenshot);
            var reopened = projectService.CurrentProject!;
            var original = await new ProjectMediaContentService().ReadOriginalAsync(reopened.MediaBlobs.Single());
            reopened.MarkSaved();
            var openingDidNotDirty = !reopened.IsDirty;
            var handler = new SmokeImageHandler(png);
            using var fakeDownloadService = new HttpExternalMediaDownloadService(
                new ApplicationDataPaths(Path.Combine(Path.GetDirectoryName(reportPath)!, sampleStem + "-cache")),
                testHandler: handler,
                resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                imageValidator: imageValidator);
            var previewHandler = new SmokeImageHandler(png);
            using var previewDownloader = new HttpExternalMediaDownloadService(
                new ApplicationDataPaths(Path.Combine(Path.GetDirectoryName(reportPath)!, sampleStem + "-preview-cache")),
                testHandler: previewHandler,
                resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                imageValidator: imageValidator);
            var previewSession = ProjectImportSession.Create(parsed, kmzPath, new HikingProject());
            var importPreview = new ImportWindow(previewSession,
                new HikingProject(), new ProjectImportService(), previewDownloader) { Owner = window };
            importPreview.Show();
            var importMarkerGrid = FindVisual<DataGrid>(importPreview, grid =>
                grid.Columns.Any(column => string.Equals(column.Header?.ToString(), "图片", StringComparison.Ordinal)));
            if (importMarkerGrid is not null) importMarkerGrid.SelectedIndex = 0;
            await Task.Delay(100);
            var previewDownloadButton = FindVisual<Button>(importPreview, button =>
                string.Equals(button.Content?.ToString(), "下载并预览", StringComparison.Ordinal));
            var previewDownloadClickable = previewDownloadButton?.IsEnabled == true;
            var previewDownloadStarted = false;
            var previewTicks = 0;
            var previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            previewTimer.Tick += (_, _) =>
            {
                previewTicks++;
                var previewViewer = Windows.OfType<MediaViewerWindow>().FirstOrDefault(item => item.IsVisible && item.Owner == importPreview);
                if (previewViewer is null) return;
                if (!previewDownloadStarted)
                {
                    var downloadButton = FindVisual<Button>(previewViewer, button =>
                        string.Equals(button.Content?.ToString(), "下载并查看", StringComparison.Ordinal));
                    if (downloadButton?.IsVisible == true)
                    {
                        previewDownloadStarted = true;
                        downloadButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, downloadButton));
                    }
                }
                if (parsed.MediaAttachments.Count == 2 || previewTicks > 100)
                {
                    previewTimer.Stop();
                    previewViewer.Close();
                }
            };
            if (previewDownloadClickable)
            {
                previewTimer.Start();
                previewDownloadButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, previewDownloadButton));
                previewTimer.Stop();
            }
            var previewStaged = parsed.MediaAttachments.Count == 2 && previewHandler.RequestCount == 1 &&
                !reopened.IsDirty;
            importPreview.Close();
            var viewer = new MediaViewerWindow(reopened, reopened.Waypoints.Single().Id, fakeDownloadService,
                (reference, downloaded) => _viewModel.SaveExternalForAutomationAsync(reference, downloaded))
            { Owner = window };
            phaseTimer.Restart();
            var viewerCheck = await viewer.RunAutomationAsync();
            var autoSavedAfterDownload = reopened.IsDirty && reopened.MediaAttachments.Count == 2;
            var treeRoot = _viewModel.NavigationNodes.Single();
            treeRoot.IsExpanded = true;
            var markerGroup = treeRoot.Children.Single(child => child.DisplayName.StartsWith("未关联标注", StringComparison.Ordinal));
            markerGroup.IsExpanded = true;
            var markerTreeNode = markerGroup.Children.Single();
            _viewModel.SelectNavigationNode(markerTreeNode, false);
            var closeViewer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            closeViewer.Tick += (_, _) =>
            {
                var shownViewer = Windows.OfType<MediaViewerWindow>().FirstOrDefault(item => item.IsVisible);
                if (shownViewer is null) return;
                closeViewer.Stop();
                shownViewer.Close();
            };
            closeViewer.Start();
            _viewModel.OpenMediaCommand.Execute(null);
            closeViewer.Stop();
            var treeKeptAfterView = ReferenceEquals(treeRoot, _viewModel.NavigationNodes.Single()) &&
                treeRoot.IsExpanded && markerGroup.IsExpanded;
            timings["查看器项目及网络图显示毫秒"] = phaseTimer.Elapsed.TotalMilliseconds;
            RecordMemory("查看器关闭后");
            var requestCountAfterView = handler.RequestCount;
            RecordMemory("External保存后");
            _viewModel.UndoCommand.Execute(null);
            var undoWorked = reopened.MediaAttachments.Count == 1;
            var treeKeptAfterRefresh = _viewModel.NavigationNodes.Single().IsExpanded &&
                _viewModel.NavigationNodes.Single().Children.Single(child =>
                    child.DisplayName.StartsWith("未关联标注", StringComparison.Ordinal)).IsExpanded;
            _viewModel.RedoCommand.Execute(null);
            var redoWorked = reopened.MediaAttachments.Count == 2;
            phaseTimer.Restart();
            await Task.Run(() => projectFileService.SaveAsync(reopened, projectPath, false));
            timings["媒体项目保存毫秒"] = phaseTimer.Elapsed.TotalMilliseconds;
            RecordMemory("项目保存后");
            phaseTimer.Restart();
            var savedAgain = await Task.Run(() => projectFileService.LoadAsync(projectPath));
            timings["离线项目重开毫秒"] = phaseTimer.Elapsed.TotalMilliseconds;
            var offlineSaved = savedAgain.MediaAttachments.Count == 2 && savedAgain.MediaBlobs.Count == 1 &&
                (await new ProjectMediaContentService().ReadOriginalAsync(savedAgain.MediaBlobs.Single())).SequenceEqual(png);
            var largeImage = await RunLargeImageSmokeAsync(Path.GetDirectoryName(reportPath)!,
                sampleStem, imageValidator, window);
            var report = new
            {
                成功 = mapLoaded && thumbnailLoaded && original.SequenceEqual(png) &&
                    viewerCheck.InternalOpened && viewerCheck.ExternalWaitedForClick && viewerCheck.ExternalOpened &&
                    requestCountAfterView == 1 && openingDidNotDirty && autoSavedAfterDownload &&
                    undoWorked && redoWorked && offlineSaved && largeImage.Success &&
                    treeKeptAfterView && treeKeptAfterRefresh && previewDownloadClickable && previewStaged,
                地图加载 = mapLoaded,
                地图弹窗缩略图加载 = thumbnailLoaded,
                源KMZ已移除 = !File.Exists(kmzPath),
                项目可独立打开 = reopened.MediaBlobs.Count == 1 && original.SequenceEqual(png),
                普通标注数 = reopened.Waypoints.Count,
                项目图片数 = reopened.MediaAttachments.Count,
                网络图片引用数 = reopened.ExternalMediaReferences.Count,
                图片SHA256 = Convert.ToHexString(SHA256.HashData(original)),
                查看器项目原图打开 = viewerCheck.InternalOpened,
                网络图片未自动下载 = viewerCheck.ExternalWaitedForClick,
                点击后网络图片显示 = viewerCheck.ExternalOpened,
                模拟HTTP请求次数 = requestCountAfterView,
                打开项目未标Dirty = openingDidNotDirty,
                下载后自动保存并标Dirty = autoSavedAfterDownload,
                撤销有效 = undoWorked,
                重做有效 = redoWorked,
                无网络重开仍可看已保存图片 = offlineSaved,
                导入预览下载按钮可点击 = previewDownloadClickable,
                导入预览下载后待导入图片已暂存 = previewStaged,
                关闭图片后项目树展开保持 = treeKeptAfterView,
                重新整理项目树后展开保持 = treeKeptAfterRefresh,
                阶段耗时毫秒 = timings,
                工作集字节 = memory,
                大图6000x4000 = new
                {
                    成功 = largeImage.Success,
                    JPEG字节 = largeImage.JpegBytes,
                    缩略图字节 = largeImage.ThumbnailBytes,
                    JPEG编码毫秒 = largeImage.JpegEncodeMilliseconds,
                    缩略图毫秒 = largeImage.ThumbnailMilliseconds,
                    项目原图打开 = largeImage.InternalOpened,
                    网络图片点击后打开 = largeImage.ExternalOpened,
                    模拟HTTP请求次数 = largeImage.HttpRequests,
                    前工作集字节 = largeImage.WorkingSetBefore,
                    后工作集字节 = largeImage.WorkingSetAfter
                },
                截图 = mapLoaded ? screenshot : null,
                验收项目 = projectPath
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            window.Close();
            return;
        }
        if (restoreSmokeIndex >= 0 && restoreSmokeIndex + 1 < e.Args.Length)
        {
            var reportPath = e.Args[restoreSmokeIndex + 1];
            var rememberedPath = await activeProjectService.GetAsync();
            var restored = projectService.CurrentProject is not null &&
                string.Equals(projectService.CurrentProjectPath, rememberedPath, StringComparison.OrdinalIgnoreCase);
            var originalHash = rememberedPath is not null && File.Exists(rememberedPath)
                ? await HashFileAsync(rememberedPath) : null;
            projectService.CurrentProject?.MarkSaved();
            if (_viewModel.CloseProjectCommand.CanExecute(null)) _viewModel.CloseProjectCommand.Execute(null);
            var closed = await WaitUntilAsync(() => projectService.CurrentProject is null, TimeSpan.FromSeconds(10));
            var manualCloseCleared = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (await activeProjectService.GetAsync() is null) { manualCloseCleared = true; break; }
                await Task.Delay(100);
            }
            var report = new
            {
                成功 = restored && closed && manualCloseCleared && originalHash ==
                    (rememberedPath is not null && File.Exists(rememberedPath) ? await HashFileAsync(rememberedPath) : null),
                自动重开项目 = restored,
                上次项目文件 = rememberedPath,
                手动关闭项目 = closed,
                手动关闭后清除重开记录 = manualCloseCleared,
                原始项目未修改 = originalHash ==
                    (rememberedPath is not null && File.Exists(rememberedPath) ? await HashFileAsync(rememberedPath) : null)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            window.Close();
            return;
        }
        var followupSmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p5-followup-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (followupSmokeIndex >= 0 && followupSmokeIndex + 2 < e.Args.Length)
        {
            var sourcePath = e.Args[followupSmokeIndex + 1];
            var reportPath = e.Args[followupSmokeIndex + 2];
            var originalHash = await HashFileAsync(sourcePath);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => rendered.TrySetResult(true);
            await _viewModel.OpenForAutomationAsync(sourcePath);
            var ready = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(45))) == rendered.Task;
            var project = projectService.CurrentProject!;
            var originalPoints = project.Tracks.Sum(track => track.GetPointCount());
            var root = _viewModel.NavigationNodes.Single();
            var trackNode = root.Children[0].Children.Single();
            var guideSegmentNode = root.Children[2].Children.First();
            var normalMarkerNode = root.Children[3].Children.First();
            var initialDisplay = mapViewModel.LastDisplayPointCount;
            var twoDecimalDistances = _viewModel.GuideRows.All(row =>
                System.Text.RegularExpressions.Regex.IsMatch(row.DistanceKm, @"^\d+[.,]\d{2}$") &&
                (row.SegmentKm == "—" || System.Text.RegularExpressions.Regex.IsMatch(row.SegmentKm, @"^\d+[.,]\d{2}$")));
            var trackHidden = false;
            var trackShownAgain = false;
            var guideSegmentHidden = false;
            var ordinaryFocused = false;
            var guideFocusedFromTable = false;
            var trackFocusedFromTree = false;
            if (ready)
            {
                trackNode.IsMapVisible = false;
                trackHidden = await WaitUntilAsync(() => mapViewModel.LastDisplayPointCount == 0, TimeSpan.FromSeconds(12));
                trackNode.IsMapVisible = true;
                trackShownAgain = await WaitUntilAsync(() => mapViewModel.LastDisplayPointCount > 0, TimeSpan.FromSeconds(12));
                guideSegmentNode.IsMapVisible = false;
                guideSegmentHidden = new GuideMapGeoJsonBuilder().Build(project).Segments.GetProperty("features").GetArrayLength() ==
                    project.GuideSegments.Count - 1;
                guideSegmentNode.IsMapVisible = true;

                root.IsExpanded = true;
                root.Children[3].IsExpanded = true;
                normalMarkerNode.IsSelected = true;
                var ordinary = (Waypoint)normalMarkerNode.Model!;
                ordinaryFocused = await WaitUntilAsync(() => project.Settings.Map.Center is { } center &&
                    Math.Abs(center.Latitude - ordinary.Latitude) < 0.0001 &&
                    Math.Abs(center.Longitude - ordinary.Longitude) < 0.0001, TimeSpan.FromSeconds(12));

                var table = FindVisual<DataGrid>(window, grid => ReferenceEquals(grid.ItemsSource, _viewModel.GuideRows));
                var row = _viewModel.GuideRows.Last();
                if (table is not null) table.SelectedItem = row;
                var target = row.Node.SnappedCoordinate ?? row.Node.OriginalCoordinate;
                guideFocusedFromTable = await WaitUntilAsync(() => project.Settings.Map.Center is { } center &&
                    Math.Abs(center.Latitude - target.Latitude) < 0.0001 &&
                    Math.Abs(center.Longitude - target.Longitude) < 0.0001, TimeSpan.FromSeconds(12));

                root.Children[0].IsExpanded = true;
                trackNode.IsSelected = true;
                var lastGuideCenter = target;
                trackFocusedFromTree = await WaitUntilAsync(() => project.Settings.Map.Center is { } center &&
                    Math.Abs(center.Latitude - lastGuideCenter.Latitude) > 0.001, TimeSpan.FromSeconds(12));
            }
            var screenshot = Path.ChangeExtension(reportPath, ".png");
            if (ready) await mapViewModel.CapturePreviewAsync(screenshot);
            trackNode.IsMapVisible = false;
            guideSegmentNode.IsMapVisible = false;
            var savedCopy = Path.ChangeExtension(reportPath, ".hikeproj");
            await projectFileService.SaveAsync(project, savedCopy, false);
            var reopened = await projectFileService.LoadAsync(savedCopy);
            var report = new
            {
                成功 = ready && initialDisplay > 0 && twoDecimalDistances && trackHidden && trackShownAgain && guideSegmentHidden &&
                    ordinaryFocused && guideFocusedFromTable && trackFocusedFromTree && originalPoints == 125910 &&
                    reopened.Tracks.Sum(track => track.GetPointCount()) == originalPoints &&
                    !MapLayerVisibility.IsTrackVisible(reopened.Settings.Map, project.Tracks[0].Id) &&
                    !MapLayerVisibility.IsGuideSegmentVisible(reopened.Settings.Map, project.GuideSegments[0].Id) &&
                    originalHash == await HashFileAsync(sourcePath),
                真实WebView2加载 = ready,
                初始地图显示点数 = initialDisplay,
                距离显示两位小数 = twoDecimalDistances,
                关闭轨迹显示 = trackHidden,
                恢复轨迹显示 = trackShownAgain,
                隐藏单个攻略分段 = guideSegmentHidden,
                侧栏普通标注定位 = ordinaryFocused,
                下方攻略表定位 = guideFocusedFromTable,
                侧栏轨迹定位 = trackFocusedFromTree,
                原始轨迹点数 = originalPoints,
                保存重开轨迹点数 = reopened.Tracks.Sum(track => track.GetPointCount()),
                保存重开轨迹隐藏 = !MapLayerVisibility.IsTrackVisible(reopened.Settings.Map, project.Tracks[0].Id),
                保存重开分段隐藏 = !MapLayerVisibility.IsGuideSegmentVisible(reopened.Settings.Map, project.GuideSegments[0].Id),
                原始项目未修改 = originalHash == await HashFileAsync(sourcePath),
                截图 = ready ? screenshot : null,
                保存项目副本 = savedCopy
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            window.Close();
            return;
        }
        var p5ImportUiIndex = Array.FindIndex(e.Args, value => value.Equals("--p5-import-ui-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p5ImportUiIndex >= 0 && p5ImportUiIndex + 2 < e.Args.Length)
        {
            var source = e.Args[p5ImportUiIndex + 1];
            var reportPath = e.Args[p5ImportUiIndex + 2];
            var sourceHashBefore = await HashFileAsync(source);
            var parsed = await Task.Run(() => trackFileService.ImportAsync(source, TrackFileFormat.Kml));
            var project = new HikingProject { Name = "P5 导入窗口验收" };
            project.MarkSaved();
            var importService = new ProjectImportService();
            var cancelSession = ProjectImportSession.Create(parsed, source, project);
            await Task.Run(() => importService.Preview(cancelSession, project));
            var cancelWindow = new ImportWindow(cancelSession, project, importService) { Owner = window };
            var bulkResults = new List<bool>();
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                ClickButton(cancelWindow, "全选攻略");
                bulkResults.Add(cancelSession.WaypointSelections.All(s => s.Import && s.IsGuideNode));
                ClickButton(cancelWindow, "反选攻略");
                bulkResults.Add(cancelSession.WaypointSelections.All(s => s.Import && !s.IsGuideNode));
                ClickButton(cancelWindow, "全选导入");
                bulkResults.Add(cancelSession.WaypointSelections.All(s => s.Import));
                ClickButton(cancelWindow, "反选导入");
                bulkResults.Add(cancelSession.WaypointSelections.All(s => !s.Import && !s.IsGuideNode));
                cancelWindow.Close();
            }));
            _ = cancelWindow.ShowDialog();
            var cancelUnchanged = !project.IsDirty && project.Tracks.Count == 0 && project.Waypoints.Count == 0 && project.GuideNodes.Count == 0;

            var session = ProjectImportSession.Create(parsed, source, project);
            var requiredNames = new[] { "起点：后山村", "帽子峰", "后山顶", "梅花顶", "黄思脑", "第一登船底顶", "联山农庄民宿", "上天堂山顶", "第二登船底顶" };
            foreach (var choice in session.WaypointSelections.Where(choice => requiredNames.Contains(choice.Waypoint.Name)))
            {
                choice.IsGuideNode = true;
                choice.Waypoint.Type = choice.Waypoint.Name switch
                {
                    "起点：后山村" => WaypointType.Start,
                    "联山农庄民宿" => WaypointType.Accommodation,
                    _ => WaypointType.Normal
                };
            }
            foreach (var choice in session.WaypointSelections.Where(choice => !choice.IsGuideNode).Take(3)) choice.Import = false;
            await Task.Run(() => importService.Preview(session, project));
            var importWindow = new ImportWindow(session, project, importService) { Owner = window };
            var screenshot = Path.ChangeExtension(reportPath, ".png");
            var batchTypeSet = false;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var summitNames = new[] { "帽子峰", "后山顶", "梅花顶", "黄思脑" };
                foreach (var row in importWindow.WaypointGrid.Items.Cast<object>().Where(row =>
                             summitNames.Contains(row.GetType().GetProperty("Name")?.GetValue(row)?.ToString())))
                    importWindow.WaypointGrid.SelectedItems.Add(row);
                importWindow.BatchTypeCombo.SelectedValue = WaypointType.Summit;
                ClickButton(importWindow, "设置选中项类型");
                batchTypeSet = session.WaypointSelections.Where(s => summitNames.Contains(s.Waypoint.Name))
                    .All(s => s.Waypoint.Type == WaypointType.Summit);
                CaptureWindowPng(importWindow, screenshot);
                ClickButton(importWindow, "确认导入");
            }));
            var confirmed = importWindow.ShowDialog() == true;
            if (confirmed) importService.Apply(importService.Prepare(session, project), project, true);
            var savedCopy = Path.ChangeExtension(reportPath, ".hikeproj");
            if (confirmed) await projectFileService.SaveAsync(project, savedCopy, false);
            var reopened = confirmed ? await projectFileService.LoadAsync(savedCopy) : null;
            var report = new
            {
                成功 = bulkResults.Count == 4 && bulkResults.All(value => value) && cancelUnchanged && batchTypeSet &&
                    confirmed && reopened?.GuideNodes.Count == 9 && reopened.Waypoints.Count == 42,
                四个批量按钮 = bulkResults,
                取消后项目未修改 = cancelUnchanged,
                CtrlShift多选模式 = importWindow.WaypointGrid.SelectionMode.ToString(),
                四个山峰批量设置类型 = batchTypeSet,
                确认导入 = confirmed,
                轨迹点数 = reopened?.Tracks.Sum(track => track.GetPointCount()),
                攻略节点数 = reopened?.GuideNodes.Count,
                普通标注数 = reopened?.Waypoints.Count,
                攻略分段数 = reopened?.GuideSegments.Count,
                原始KML未修改 = sourceHashBefore == await HashFileAsync(source),
                截图 = screenshot,
                保存项目 = confirmed ? savedCopy : null
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            window.Close();
            return;
        }
        var p5MenuSmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p5-menu-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p5MenuSmokeIndex >= 0 && p5MenuSmokeIndex + 2 < e.Args.Length)
        {
            var projectPath = e.Args[p5MenuSmokeIndex + 1];
            var reportPath = e.Args[p5MenuSmokeIndex + 2];
            var sourceHash = await HashFileAsync(projectPath);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => rendered.TrySetResult(true);
            await _viewModel.OpenForAutomationAsync(projectPath);
            var ready = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(45))) == rendered.Task;
            var project = projectService.CurrentProject!;
            var menu = ((Grid)window.Content).Children.OfType<Menu>().Single();
            var markerMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "标注点"));
            var expected = new[] { "添加攻略节点", "添加普通标注", "编辑标注", "删除标注", "转为攻略节点",
                "转为普通标注", "设置标注类型", "重新吸附到轨迹", "选择轨迹位置", "关联到当前工作轨迹",
                "检查所有攻略节点", "重新计算当前轨迹攻略节点" };
            var actual = markerMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
            var allPresent = expected.SequenceEqual(actual);
            var noSelectionCorrect = _viewModel.AddGuideMenuCommand.CanExecute(null) &&
                _viewModel.AddWaypointMenuCommand.CanExecute(null) && !_viewModel.EditSelectedMenuCommand.CanExecute(null);
            _viewModel.SelectNavigationNode(new NavigationNode("测试标注", project.Waypoints.First()));
            var ordinarySelectionCorrect = _viewModel.ConvertToGuideMenuCommand.CanExecute(null) &&
                !_viewModel.ConvertToWaypointMenuCommand.CanExecute(null) && !_viewModel.ResnapSelectedMenuCommand.CanExecute(null);
            _viewModel.SelectNavigationNode(new NavigationNode("测试攻略节点", project.GuideNodes.First()));
            var guideSelectionCorrect = !_viewModel.ConvertToGuideMenuCommand.CanExecute(null) &&
                _viewModel.ConvertToWaypointMenuCommand.CanExecute(null) && _viewModel.ResnapSelectedMenuCommand.CanExecute(null) &&
                _viewModel.PickSelectedMenuCommand.CanExecute(null);
            var waypointCount = project.Waypoints.Count;
            var guideCount = project.GuideNodes.Count;
            var addedWaypoint = false;
            var addedGuide = false;
            if (ready)
            {
                _viewModel.AddWaypointMenuCommand.Execute(null);
                await mapViewModel.RunP5MarkerAddSmokeAsync(project.GuideNodes[0].OriginalCoordinate.Latitude,
                    project.GuideNodes[0].OriginalCoordinate.Longitude);
                addedWaypoint = await WaitUntilAsync(() => project.Waypoints.Count == waypointCount + 1, TimeSpan.FromSeconds(10));
                if (addedWaypoint) _viewModel.UndoCommand.Execute(null);
                _viewModel.AddGuideMenuCommand.Execute(null);
                await mapViewModel.RunP5MarkerAddSmokeAsync(project.GuideNodes[0].OriginalCoordinate.Latitude,
                    project.GuideNodes[0].OriginalCoordinate.Longitude);
                addedGuide = await WaitUntilAsync(() => project.GuideNodes.Count == guideCount + 1, TimeSpan.FromSeconds(10));
                if (addedGuide) _viewModel.UndoCommand.Execute(null);
            }
            var report = new
            {
                成功 = ready && allPresent && noSelectionCorrect && ordinarySelectionCorrect && guideSelectionCorrect &&
                    addedWaypoint && addedGuide && project.Waypoints.Count == waypointCount && project.GuideNodes.Count == guideCount &&
                    sourceHash == await HashFileAsync(projectPath),
                真实WebView2加载 = ready,
                菜单项完整 = allPresent,
                菜单项目 = actual,
                无选择EnableDisable正确 = noSelectionCorrect,
                普通标注EnableDisable正确 = ordinarySelectionCorrect,
                攻略节点EnableDisable正确 = guideSelectionCorrect,
                菜单添加普通标注 = addedWaypoint,
                菜单添加攻略节点 = addedGuide,
                两次撤销后数量恢复 = project.Waypoints.Count == waypointCount && project.GuideNodes.Count == guideCount,
                原始项目未修改 = sourceHash == await HashFileAsync(projectPath)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            window.Close();
            return;
        }
        var p5MapSmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p5-map-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p5MapSmokeIndex >= 0 && p5MapSmokeIndex + 2 < e.Args.Length)
        {
            var projectPath = e.Args[p5MapSmokeIndex + 1];
            var reportPath = e.Args[p5MapSmokeIndex + 2];
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var interacted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => rendered.TrySetResult(true);
            mapViewModel.InteractionSmokeCompleted += (_, message) => interacted.TrySetResult(message);
            await _viewModel.OpenForAutomationAsync(projectPath);
            var renderReady = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(45))) == rendered.Task;
            if (renderReady)
            {
                await Task.Delay(1200);
                await mapViewModel.FitAllForAutomationAsync();
                await Task.Delay(800);
                await mapViewModel.RunP5InteractionSmokeAsync();
            }
            var interactionsReady = renderReady && await Task.WhenAny(interacted.Task, Task.Delay(TimeSpan.FromSeconds(15))) == interacted.Task;
            var screenshot = Path.ChangeExtension(reportPath, ".png");
            if (renderReady) await mapViewModel.CapturePreviewAsync(screenshot);
            var project = projectService.CurrentProject;
            var report = new
            {
                成功 = renderReady && interactionsReady && _viewModel.MapSelectionSyncCount >= 17,
                地图加载 = renderReady,
                节点及分段点击联动 = interactionsReady ? interacted.Task.Result : "未完成",
                地图到项目树选择同步次数 = _viewModel.MapSelectionSyncCount,
                攻略节点数 = project?.GuideNodes.Count,
                攻略分段数 = project?.GuideSegments.Count,
                原始轨迹点数 = project?.Tracks.Sum(track => track.GetPointCount()),
                地图显示轨迹点数 = mapViewModel.LastDisplayPointCount,
                截图 = renderReady ? screenshot : null,
                地图状态 = mapViewModel.MapStatusText
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            window.Close();
            return;
        }
        var p5EditSmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--p5-edit-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p5EditSmokeIndex >= 0 && p5EditSmokeIndex + 2 < e.Args.Length)
        {
            var projectPath = e.Args[p5EditSmokeIndex + 1];
            var reportPath = e.Args[p5EditSmokeIndex + 2];
            var inputHashBefore = await HashFileAsync(projectPath);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, _) => rendered.TrySetResult(true);
            await _viewModel.OpenForAutomationAsync(projectPath);
            var ready = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(45))) == rendered.Task;
            var project = projectService.CurrentProject!;
            var waypoint = project.Waypoints.First();
            var guideNode = project.GuideNodes.First();
            var waypointBefore = waypoint.Coordinate;
            var guideBefore = guideNode.OriginalCoordinate;
            var waypointMoved = false;
            var guideMoved = false;
            var undoRestored = false;
            var redoRestored = false;
            if (ready)
            {
                await Task.Delay(1200);
                await mapViewModel.RunP5WaypointEditSmokeAsync();
                waypointMoved = await WaitUntilAsync(() => waypoint.Coordinate != waypointBefore, TimeSpan.FromSeconds(10));
                if (waypointMoved)
                {
                    await Task.Delay(1000);
                    await mapViewModel.RunP5GuideEditSmokeAsync();
                    guideMoved = await WaitUntilAsync(() => guideNode.OriginalCoordinate != guideBefore, TimeSpan.FromSeconds(10));
                }
                if (guideMoved && _viewModel.UndoCommand.CanExecute(null))
                {
                    _viewModel.UndoCommand.Execute(null);
                    _viewModel.UndoCommand.Execute(null);
                    undoRestored = waypoint.Coordinate == waypointBefore && guideNode.OriginalCoordinate == guideBefore;
                    _viewModel.RedoCommand.Execute(null);
                    _viewModel.RedoCommand.Execute(null);
                    redoRestored = waypoint.Coordinate != waypointBefore && guideNode.OriginalCoordinate != guideBefore;
                }
                await Task.Delay(1200);
            }
            var savedCopy = Path.ChangeExtension(reportPath, ".hikeproj");
            if (redoRestored) await projectFileService.SaveAsync(project, savedCopy, false);
            var reopened = redoRestored ? await projectFileService.LoadAsync(savedCopy) : null;
            var screenshot = Path.ChangeExtension(reportPath, ".png");
            if (ready) await mapViewModel.CapturePreviewAsync(screenshot);
            using var process = Process.GetCurrentProcess();
            var report = new
            {
                成功 = ready && waypointMoved && guideMoved && undoRestored && redoRestored &&
                    reopened?.GuideNodes.FirstOrDefault(n => n.Id == guideNode.Id)?.ExactPosition is not null,
                真实WebView2加载 = ready,
                地图协议模拟普通标注拖动 = waypointMoved,
                地图协议模拟攻略节点拖动 = guideMoved,
                两步撤销恢复 = undoRestored,
                两步重做恢复 = redoRestored,
                保存重开攻略节点位置 = reopened?.GuideNodes.FirstOrDefault(n => n.Id == guideNode.Id)?.ExactPosition is not null,
                保存重开普通标注坐标 = reopened?.Waypoints.FirstOrDefault(w => w.Id == waypoint.Id)?.Coordinate == waypoint.Coordinate,
                原始项目未修改 = inputHashBefore == await HashFileAsync(projectPath),
                主进程工作集字节 = process.WorkingSet64,
                主进程峰值工作集字节 = process.PeakWorkingSet64,
                截图 = ready ? screenshot : null
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            window.Close();
            return;
        }
        var mapSmokeIndex = Array.FindIndex(e.Args, value => value.Equals("--map-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (mapSmokeIndex >= 0 && mapSmokeIndex + 2 < e.Args.Length)
        {
            var projectPath = e.Args[mapSmokeIndex + 1];
            var reportPath = e.Args[mapSmokeIndex + 2];
            var renderCompletion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var interactionCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            mapViewModel.RenderCompleted += (_, duration) => renderCompletion.TrySetResult(duration);
            mapViewModel.InteractionSmokeCompleted += (_, message) => interactionCompletion.TrySetResult(message);
            var timer = Stopwatch.StartNew();
            var inputHashBefore = await HashFileAsync(projectPath);
            await _viewModel.OpenForAutomationAsync(projectPath);
            var project = projectService.CurrentProject!;
            var finished = await Task.WhenAny(renderCompletion.Task, Task.Delay(TimeSpan.FromSeconds(45)));
            var rendered = finished == renderCompletion.Task;
            var sourceSwitchCount = 0;
            string? overviewPreviewPath = null;
            if (rendered)
            {
                await mapViewModel.FitAllForAutomationAsync();
                await Task.Delay(600);
                overviewPreviewPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(reportPath))!,
                    Path.GetFileNameWithoutExtension(reportPath) + "-全览.png");
                await mapViewModel.CapturePreviewAsync(overviewPreviewPath);
                var firstSource = mapViewModel.MapSources.First();
                var alternate = firstSource.DeepCopy();
                alternate.Id = Guid.NewGuid();
                alternate.Name = "自动验收备用图源";
                GlobalSettingsService.Current.MapSources.Add(alternate);
                mapViewModel.RefreshMapSources();
                for (var index = 0; index < 10; index++)
                {
                    await mapViewModel.SelectMapSourceAsync(index % 2 == 0 ? alternate : firstSource);
                    sourceSwitchCount++;
                }
                await Task.Delay(250);
                await mapViewModel.RunInteractionSmokeAsync();
            }
            var interactionFinished = rendered && await Task.WhenAny(interactionCompletion.Task, Task.Delay(TimeSpan.FromSeconds(15))) == interactionCompletion.Task;
            await Task.Delay(750);
            var previewPath = Path.ChangeExtension(reportPath, ".png");
            if (rendered) await mapViewModel.CapturePreviewAsync(previewPath);
            var savedProjectPath = Path.ChangeExtension(reportPath, ".hikeproj");
            await projectFileService.SaveAsync(project, savedProjectPath, true);
            var reopened = await projectFileService.LoadAsync(savedProjectPath);
            var inputHashAfter = await HashFileAsync(projectPath);
            timer.Stop();
            var webProcesses = Process.GetProcessesByName("msedgewebview2")
                .Select(process =>
                {
                    try
                    {
                        return process.StartTime >= applicationStartedAt.LocalDateTime.AddSeconds(-1)
                            ? new { process.Id, 工作集字节 = process.WorkingSet64, 峰值工作集字节 = process.PeakWorkingSet64 }
                            : null;
                    }
                    catch { return null; }
                    finally { process.Dispose(); }
                }).Where(value => value is not null).ToArray();
            using var currentProcess = Process.GetCurrentProcess();
            var succeeded = rendered && interactionFinished && _viewModel.MapSelectionSyncCount >= 20 &&
                sourceSwitchCount == 10 && reopened.Tracks.Sum(track => track.GetPointCount()) == 125_910 &&
                reopened.Waypoints.Count == 54 && inputHashBefore == inputHashAfter;
            var report = new
            {
                测试名称 = "P4 真实项目地图加载",
                成功 = succeeded,
                项目文件 = projectPath,
                轨迹数量 = project.Tracks.Count,
                原始轨迹点数 = project.Tracks.Sum(track => track.GetPointCount()),
                地图显示输入点数 = mapViewModel.LastOriginalPointCount,
                地图显示点数 = mapViewModel.LastDisplayPointCount,
                MapLibreReady毫秒 = mapViewModel.LastMapLibreReadyMilliseconds,
                浏览器渲染毫秒 = rendered ? await renderCompletion.Task : (double?)null,
                交互测试 = interactionFinished ? await interactionCompletion.Task : "未完成",
                地图到项目树选择同步次数 = _viewModel.MapSelectionSyncCount,
                图源切换次数 = sourceSwitchCount,
                保存重开轨迹点数 = reopened.Tracks.Sum(track => track.GetPointCount()),
                保存重开标注点数 = reopened.Waypoints.Count,
                地图中心保存 = reopened.Settings.Map.Center is not null,
                地图缩放保存 = reopened.Settings.Map.Zoom is not null,
                当前图源保存 = reopened.Settings.Map.SelectedMapSourceId is not null,
                输入项目未改写 = inputHashBefore == inputHashAfter,
                地图预览图 = rendered ? previewPath : null,
                地图全览预览图 = overviewPreviewPath,
                WPF工作集字节 = currentProcess.WorkingSet64,
                WPF峰值工作集字节 = currentProcess.PeakWorkingSet64,
                WebView2相关进程 = webProcesses,
                总耗时毫秒 = timer.Elapsed.TotalMilliseconds,
                地图状态 = mapViewModel.MapStatusText
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            window.ApproveAutomationClose();
            window.Close();
            return;
        }
        var p8UiIndex = Array.FindIndex(e.Args, value =>
            value.Equals("--p8-ui-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (p8UiIndex >= 0 && p8UiIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[p8UiIndex + 1]);
            var testProject = new HikingProject { Name = "P8图表界面验收副本" };
            var testTrack = new HikingGuide.Core.Models.Track { Name = "高程曲线测试" };
            testTrack.Segments.Add(new TrackSegment { Points =
            [
                new TrackPoint { Latitude = 23.1382, Longitude = 113.3270, SourceElevation = 100 },
                new TrackPoint { Latitude = 23.1387, Longitude = 113.3270, SourceElevation = 125 },
                new TrackPoint { Latitude = 23.1392, Longitude = 113.3270, SourceElevation = 110 }
            ] });
            testTrack.RecalculateDistances();
            testProject.AddTrack(testTrack);
            bool chartPassed;
            bool noGpsPassed = false;
            string? chartError = null;
            string? screenshot = null;
            string? screenshotError = null;
            bool gpsVsDemPassed = false;
            bool demVsDemPassed = false;
            bool p81SelectionPassed = false;
            (bool LeftZoom, bool RightZoom, bool LeftPan, bool RightPan,
                bool FullRange, bool Hover) p81Viewport = default;
            string? p81ViewportScreenshot = null;
            bool realLongTrackPassed = false;
            int realLongTrackPoints = 0;
            long realLongTrackMilliseconds = 0;
            string? realLongScreenshot = null;
            string? comparisonScreenshot = null;
            try
            {
                chartPassed = await window.RunP8GpsUiSmokeAsync(testProject, testTrack);
                if (chartPassed)
                {
                    try
                    {
                        screenshot = Path.ChangeExtension(reportPath, ".png");
                        Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
                        window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        await using var screenshotStream = new FileStream(screenshot, FileMode.Create,
                            FileAccess.Write, FileShare.None, 65536, useAsync: true);
                        encoder.Save(screenshotStream);
                    }
                    catch (Exception error) { screenshotError = error.Message; screenshot = null; }
                }
                var noGpsTrack = new HikingGuide.Core.Models.Track { Name = "BRouter无GPS海拔测试" };
                noGpsTrack.Segments.Add(new TrackSegment { Points =
                [
                    new TrackPoint { Latitude = 23.1382, Longitude = 113.3270 },
                    new TrackPoint { Latitude = 23.1392, Longitude = 113.3270 }
                ] });
                noGpsTrack.RecalculateDistances();
                testProject.AddTrack(noGpsTrack);
                noGpsPassed = await window.RunP8NoGpsUiSmokeAsync(testProject, noGpsTrack);
                if (p8UiIndex + 3 < e.Args.Length)
                {
                    if (DemCatalog.Databases.Count == 0)
                    {
                        await DemCatalog.MountAsync("P8界面测试 ALOS", e.Args[p8UiIndex + 2],
                            unitMetersConfirmed: true);
                        await DemCatalog.MountAsync("P8界面测试 FathomDEM", e.Args[p8UiIndex + 3],
                            unitMetersConfirmed: true);
                        if (p8UiIndex + 4 < e.Args.Length)
                            await DemCatalog.MountAsync("P8界面测试第三套", e.Args[p8UiIndex + 4],
                                unitMetersConfirmed: true);
                    }
                    window.RefreshP8Sources();
                    (gpsVsDemPassed, demVsDemPassed) =
                        await window.RunP8ComparisonUiSmokeAsync(testProject, testTrack);
                    if (demVsDemPassed)
                    {
                        comparisonScreenshot = Path.Combine(Path.GetDirectoryName(reportPath)!,
                            Path.GetFileNameWithoutExtension(reportPath) + "-双DEM.png");
                        window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        await using var stream = new FileStream(comparisonScreenshot,
                            FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                        encoder.Save(stream);
                    }
                    if (p8UiIndex + 5 < e.Args.Length)
                    {
                        var realWatch = Stopwatch.StartNew();
                        var imported = await new KmlTrackImporter().ImportAsync(e.Args[p8UiIndex + 5]);
                        var realTrack = imported.Tracks.Single();
                        realTrack.RecalculateDistances();
                        realLongTrackPoints = realTrack.GetPointCount();
                        var realProject = new HikingProject { Name = "P8真实长轨迹界面验收副本" };
                        realProject.AddTrack(realTrack);
                        realLongTrackPassed = await window.RunP8GpsUiSmokeAsync(realProject, realTrack);
                        var realComparisons = await window.RunP8ComparisonUiSmokeAsync(realProject,
                            realTrack);
                        realLongTrackPassed &= realComparisons.GpsVsDem && realComparisons.DemVsDem;
                        realLongTrackMilliseconds = realWatch.ElapsedMilliseconds;
                        if (realLongTrackPassed)
                        {
                            realLongScreenshot = Path.Combine(Path.GetDirectoryName(reportPath)!,
                                Path.GetFileNameWithoutExtension(reportPath) + "-真实长轨迹.png");
                            window.UpdateLayout();
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                                (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            await using var stream = new FileStream(realLongScreenshot,
                                FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                            encoder.Save(stream);
                        }
                    }
                    p81Viewport = window.RunP81ViewportUiSmoke();
                    if (p81Viewport.Hover)
                    {
                        p81ViewportScreenshot = Path.Combine(Path.GetDirectoryName(reportPath)!,
                            Path.GetFileNameWithoutExtension(reportPath) + "-P81双图悬停.png");
                        window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        await using var stream = new FileStream(p81ViewportScreenshot,
                            FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                        encoder.Save(stream);
                    }
                    var automationTracks = new List<HikingGuide.Core.Models.Track>();
                    for (var index = 0; index < 3; index++)
                    {
                        var candidate = new HikingGuide.Core.Models.Track
                        { Name = $"P8-1自动选择轨迹{index + 1}",
                            DisplayColor = new[] { "#D63B3B", "#308D52", "#465EE8" }[index] };
                        candidate.Segments.Add(new TrackSegment { Points =
                        [
                            new TrackPoint { Latitude = 23.1 + index * 0.01, Longitude = 113,
                                SourceElevation = 100 + index * 10 },
                            new TrackPoint { Latitude = 23.101 + index * 0.01, Longitude = 113,
                                SourceElevation = 120 + index * 10 }
                        ] });
                        candidate.RecalculateDistances();
                        testProject.AddTrack(candidate);
                        automationTracks.Add(candidate);
                    }
                    p81SelectionPassed = await window.RunP81SelectionUiSmokeAsync(testProject,
                        automationTracks);
                }
            }
            catch (Exception error) { chartPassed = false; chartError = error.ToString(); }
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                成功 = chartPassed && noGpsPassed &&
                    (p8UiIndex + 3 >= e.Args.Length || gpsVsDemPassed && demVsDemPassed &&
                        p81SelectionPassed && p81Viewport.LeftZoom && p81Viewport.RightZoom &&
                        p81Viewport.LeftPan && p81Viewport.RightPan && p81Viewport.FullRange &&
                        p81Viewport.Hover) &&
                    (p8UiIndex + 5 >= e.Args.Length || realLongTrackPassed),
                GPS图表 = chartPassed,
                无GPS海拔提示 = noGpsPassed, 错误 = chartError, 截图 = screenshot,
                截图错误 = screenshotError,
                GPS对比DEM = gpsVsDemPassed, DEM对比DEM = demVsDemPassed,
                真实长轨迹图表 = realLongTrackPassed, 真实长轨迹点数 = realLongTrackPoints,
                真实长轨迹界面毫秒 = realLongTrackMilliseconds,
                真实长轨迹截图 = realLongScreenshot,
                峰值工作集字节 = Process.GetCurrentProcess().PeakWorkingSet64,
                双DEM截图 = comparisonScreenshot,
                P81自动选择 = p81SelectionPassed,
                P81左缩放 = p81Viewport.LeftZoom,
                P81右缩放 = p81Viewport.RightZoom,
                P81左平移 = p81Viewport.LeftPan,
                P81右平移 = p81Viewport.RightPan,
                P81适应全轨迹 = p81Viewport.FullRange,
                P81双图气泡 = p81Viewport.Hover,
                P81双图视口总毫秒 = window.LastP81ViewportMilliseconds,
                P81双图悬停截图 = p81ViewportScreenshot,
                DEM数据库数 = DemCatalog.Databases.Count,
                GDAL = DemGdalRuntime.EnsureReady().Item1
            }, new JsonSerializerOptions { WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            window.ApproveAutomationClose();
            window.Close();
            return;
        }
        if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            window.ApproveAutomationClose();
            window.Close();
        }
        else if(e.Args.Length==0&&ApplicationDataPaths.IsInstalledApplication(AppContext.BaseDirectory)&&!GlobalSettingsService.Current.FirstRunSetupCompleted)
            new FirstRunSetupWindow(window,mapViewModel).ShowDialog();
    }

    private static async Task<LargeImageSmokeResult> RunLargeImageSmokeAsync(string reportDirectory,
        string sampleStem, IImageDecodeValidator validator, Window owner)
    {
        var before = Process.GetCurrentProcess().WorkingSet64;
        var timer = Stopwatch.StartNew();
        var jpeg = await Task.Run(CreateLargeJpeg);
        var encodeMs = timer.Elapsed.TotalMilliseconds;
        var valid = await Task.Run(() => validator.CanDecode(jpeg));
        var project = new HikingProject { Name = "6000×4000大图验收" };
        var waypoint = new Waypoint { Name = "大图标注", Latitude = 23.1, Longitude = 113.3 };
        project.Waypoints.Add(waypoint);
        var blob = new MediaBlob
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(jpeg)), MimeType = "image/jpeg",
            FileExtension = ".jpg", ByteLength = jpeg.LongLength,
            Width = 6000, Height = 4000, PendingBytes = jpeg
        };
        blob.StoredPath = $"media/originals/{blob.Id:D}.jpg";
        project.MediaBlobs.Add(blob);
        project.MediaAttachments.Add(new MediaAttachment
        { OwnerType = MediaOwnerType.Waypoint, OwnerId = waypoint.Id, MediaBlobId = blob.Id });
        var paths = new ApplicationDataPaths(Path.Combine(reportDirectory, sampleStem + "-large-cache"));
        timer.Restart();
        var thumbnail = await new ProjectThumbnailService(paths).GetInternalThumbnailAsync(project, blob);
        var thumbMs = timer.Elapsed.TotalMilliseconds;
        var reference = new ExternalMediaReference
        { OwnerType = MediaOwnerType.Waypoint, OwnerId = waypoint.Id, Url = "https://example.com/large.jpg" };
        project.ExternalMediaReferences.Add(reference);
        var handler = new SmokeImageHandler(jpeg, "image/jpeg");
        using var downloader = new HttpExternalMediaDownloadService(paths, testHandler: handler,
            resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
            imageValidator: validator);
        var viewer = new MediaViewerWindow(project, waypoint.Id, downloader, (_, _) => Task.CompletedTask)
        { Owner = owner };
        var shown = await viewer.RunAutomationAsync();
        return new LargeImageSmokeResult(valid && shown.InternalOpened && shown.ExternalOpened &&
            shown.ExternalWaitedForClick && handler.RequestCount == 1,
            jpeg.LongLength, thumbnail.LongLength, encodeMs, thumbMs,
            shown.InternalOpened, shown.ExternalOpened, handler.RequestCount,
            before, Process.GetCurrentProcess().WorkingSet64);
    }

    private static byte[] CreateLargeJpeg()
    {
        const int width = 6000;
        const int height = 4000;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            pixels[index] = (byte)(x % 256);
            pixels[index + 1] = (byte)(y % 256);
            pixels[index + 2] = 110;
            pixels[index + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private sealed record LargeImageSmokeResult(bool Success, long JpegBytes, long ThumbnailBytes,
        double JpegEncodeMilliseconds, double ThumbnailMilliseconds, bool InternalOpened,
        bool ExternalOpened, int HttpRequests, long WorkingSetBefore, long WorkingSetAfter);

    private sealed class SmokeImageHandler(byte[] image, string mimeType = "image/png") : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
            return Task.FromResult(response);
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (predicate()) return true;
            await Task.Delay(100);
        }
        return predicate();
    }

    private static void ClickButton(DependencyObject root, string caption)
    {
        var button = FindVisual<Button>(root, value => string.Equals(value.Content?.ToString(), caption, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"找不到导入窗口按钮：{caption}");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T candidate && predicate(candidate)) return candidate;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindVisual(VisualTreeHelper.GetChild(root, index), predicate);
            if (found is not null) return found;
        }
        return null;
    }

    private static void CaptureWindowPng(Window window, string path)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loggingService?.Information("应用程序退出");
        _viewModel?.Dispose();
        if (BRouterController is not null)
        {
            try { Task.Run(async () => {if(_offlineDownloads is not null)await _offlineDownloads.StopAsync();if(_osmRoads is not null)await _osmRoads.StopAsync();await BRouterController.DisposeAsync();}).GetAwaiter().GetResult(); }
            catch (Exception exception) { _loggingService?.Error(exception, "停止本地BRouter失败"); }
        }
        try
        {
            if (_sessionStateService is not null)
                Task.Run(() => _sessionStateService.MarkCleanShutdownAsync()).GetAwaiter().GetResult();
        }
        catch (Exception exception) { _loggingService?.Error(exception, "写入正常退出标记失败"); }
        _loggingService?.Dispose();
        _mapHttpClient?.Dispose();
        _externalMediaDownloadService?.Dispose();
        TerrainTiles?.Dispose();
        _osmRoads?.Dispose();
        _offlineFetcher?.Dispose();
        DemProvider?.Dispose();
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                _loggingService?.Error(exception, "未处理的应用程序异常");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            _loggingService?.Error(eventArgs.Exception, "未观察到的后台任务异常");
            eventArgs.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _loggingService?.Error(e.Exception, "未处理的界面线程异常");
        MessageBox.Show(
            "程序发生未预期错误，详细信息已经写入日志。",
            "未预期错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}




