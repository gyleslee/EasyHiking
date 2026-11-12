using System.IO;
using HikingGuide.Application.Logging;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App.Services;

public sealed partial class WebViewMapBridge(WebView2 webView, ILoggingService log, string webRoot) : IMapBridge, IMediaMapBridge
{
    private readonly Dictionary<string, byte[]> _mediaThumbnails = new(StringComparer.Ordinal);
    private MapSourceDefinition? _activeSource;
    private string? _activeTileHost;
    private readonly MapTileRequestRegistry _tileRequests=new();
    private readonly System.Net.Http.HttpClient _tileClient=HikingGuide.Infrastructure.Maps.MapTileHttpTransport.CreateClient();
    private readonly Dictionary<string,CancellationTokenSource> _tileProfiles=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _baseTileGate=new(6,6);
    private readonly SemaphoreSlim _overlayTileGate=new(2,2);
    private TaskCompletionSource<HikingGuide.Core.Maps.OfflineMapRegion?>? _offlineSelection;
    public async Task<HikingGuide.Core.Maps.OfflineMapRegion?> SelectOfflineRegionAsync(CancellationToken token=default)
    {
        if(!IsReady)throw new InvalidOperationException("地图尚未就绪，请稍后选区。");
        if(_offlineSelection is not null)throw new InvalidOperationException("正在选择离线范围。");
        var completion=new TaskCompletionSource<HikingGuide.Core.Maps.OfflineMapRegion?>(TaskCreationOptions.RunContinuationsAsynchronously);_offlineSelection=completion;
        try{await SendAsync(MapProtocol.SerializeCommand("BeginOfflineRegion",null),token);return await completion.Task.WaitAsync(token);}
        finally{_offlineSelection=null;if(token.IsCancellationRequested)await SendAsync(MapProtocol.SerializeCommand("CancelOfflineRegion",null));}
    }
    private RasterTileReprojectionService? _reprojection;
    private DemTerrainTiles? _terrainTiles;
    public Task<object> ConfigureOsmRoadsAsync(OsmRoadSettings settings)=>((App)System.Windows.Application.Current).OsmRoads.ConfigureAsync(settings);
    public TerrainMapConfiguration ConfigureTerrain3D(Terrain3DSettings settings)=>
        ((App)System.Windows.Application.Current).TerrainTiles?.Configure(settings)??new(Error:settings.Enabled?"GDAL未就绪，无法启用3D地形。":null);
    public object ConfigureOfflineTerrain(OfflineTerrainSettings settings)
    {
        var app=(App)System.Windows.Application.Current;
        if(app.DemProvider is null)return new {contours=false,hillshade=false};
        if(_terrainTiles is null)
        {
            _terrainTiles=new(app.DemCatalog,DemTerrainTiles.CacheRoot);
            app.Exit+=(_,_)=>_terrainTiles?.Dispose();
        }
        var shade=System.Text.Json.JsonSerializer.SerializeToElement(_terrainTiles.Configure(settings with{ContoursVisible=false}));
        var surface=app.TerrainTiles?.ConfigureGpuSurface(settings)??new{ready=false};
        return new{signature=shade.GetProperty("signature").GetString(),hillshadeSignature=shade.GetProperty("hillshadeSignature").GetString(),
            hillshade=shade.GetProperty("hillshade").GetBoolean(),opacity=settings.HillshadeOpacity,
            contours=settings.ContoursVisible,surface,color=settings.ContourColor,contourOpacity=settings.ContourOpacity,
            majorEnabled=settings.MajorContoursEnabled,majorInterval=settings.MajorContourIntervalMeters,
            lowInterval=settings.LowContourIntervalMeters,highInterval=settings.HighContourIntervalMeters,smooth=settings.SmoothContours,
            lineWidth=settings.ContourLineWidth,majorLineWidth=settings.MajorContourLineWidth,majorColor=settings.MajorContourColor??settings.ContourColor};
    }
    internal object ConfigureGpuContourProbe(OfflineTerrainSettings settings)=>
        ((App)System.Windows.Application.Current).TerrainTiles?.ConfigureGpuSurface(settings)??new {ready=false};
    public async Task ClearOfflineTerrainCacheAsync(bool contours)
    {
        var app=(App)System.Windows.Application.Current;
        if(contours&&app.TerrainTiles is {} generator)
        {
            await SendAsync(MapProtocol.SerializeCommand("ClearGpuContours",new{}));
            await generator.ClearGpuSurfaceAsync();
            await SendAsync(MapProtocol.SerializeCommand("SetOfflineTerrain",ConfigureOfflineTerrain(app.GlobalSettingsService.Current.OfflineTerrain with{DemDatabaseId=app.GlobalSettingsService.Current.Terrain3D?.TerrainDemDatabaseId})));
        }
        else if(_terrainTiles is not null)await _terrainTiles.ClearCacheAsync(contours);
        else await HikingGuide.Infrastructure.Maps.MapTileDiskCache.Shared(DemTerrainTiles.CacheRoot).ClearAsync(contours?DemTerrainTiles.ContourCacheId:DemTerrainTiles.HillshadeCacheId);
    }
    private readonly HikingGuide.Infrastructure.Maps.MapTileDiskCache _tileCache=HikingGuide.Infrastructure.Maps.MapTileDiskCache.Shared(
        Path.Combine(new HikingGuide.Infrastructure.Projects.ApplicationDataPaths().RootDirectory,"MapTileCache"));
    public IReadOnlyList<RasterLayerSlot> ConfigureRasterLayers(IReadOnlyList<RasterLayerSlot> slots)
    {
        foreach(var slot in slots)if(slot.Source is {} source)_tileCache.Configure(source.Id,(long)source.CacheCapacityMb*1024*1024);
        var registered=_tileRequests.Register(slots);
        var active=registered.SelectMany(s=>s.Templates).Select(t=>new Uri(t).AbsolutePath.Split('/')[1]).ToHashSet(StringComparer.Ordinal);
        foreach(var key in _tileProfiles.Keys.Where(k=>!active.Contains(k)).ToArray())
        {var old=_tileProfiles[key];_tileProfiles.Remove(key);old.Cancel();old.Dispose();}
        foreach(var key in active)if(!_tileProfiles.ContainsKey(key))_tileProfiles.Add(key,new());
        return registered;
    }
    internal Func<System.Net.Http.HttpRequestMessage,CancellationToken,Task<System.Net.Http.HttpResponseMessage>>? TileFetchForTest {get;set;}
    private bool _initialized;
    private CancellationTokenSource? _readyTimeout;

    public bool IsReady { get; private set; }
    public event EventHandler<MapEventMessage>? EventReceived;
    public event EventHandler<string>? InitializationFailed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        try
        {
            if (!Directory.Exists(webRoot)) throw new DirectoryNotFoundException("本地地图资源目录不存在。");
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var configuredRoot = Environment.GetEnvironmentVariable("HIKINGGUIDE_APPDATA_DIR");
            var applicationRoot = string.IsNullOrWhiteSpace(configuredRoot)
                ? HikingGuide.Infrastructure.Projects.ApplicationDataPaths.DefaultRootDirectory(AppContext.BaseDirectory)
                : Path.GetFullPath(configuredRoot);
            var userData = Path.Combine(applicationRoot, "WebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await webView.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping("hikingguide.local", webRoot, CoreWebView2HostResourceAccessKind.Allow);
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            webView.PreviewKeyDown += OnMapPreviewKeyDown;
            webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            webView.CoreWebView2.ProcessFailed += OnProcessFailed;
            webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            webView.Source = new Uri("https://hikingguide.local/index.html");
            _readyTimeout = new CancellationTokenSource();
            _ = WatchReadyAsync(_readyTimeout.Token);
            _initialized = true;
        }
        catch (Exception exception)
        {
            log.Error(exception, "WebView2 地图初始化失败");
            InitializationFailed?.Invoke(this, "地图初始化失败。请确认已安装 Microsoft Edge WebView2 运行时，然后重试。轨迹与项目数据未受影响。");
        }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _offlineSelection?.TrySetResult(null);
        IsReady = false;
        _readyTimeout?.Cancel();
        _readyTimeout?.Dispose();
        _readyTimeout = new CancellationTokenSource();
        _ = WatchReadyAsync(_readyTimeout.Token);
        if (webView.CoreWebView2 is null) return InitializeAsync(cancellationToken);
        webView.CoreWebView2.Reload();
        return Task.CompletedTask;
    }

    public async Task SendAsync(string commandJson, CancellationToken cancellationToken = default)
    {
        if (webView.CoreWebView2 is null) return;
        await webView.Dispatcher.InvokeAsync(() => webView.CoreWebView2.PostWebMessageAsJson(commandJson));
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task CapturePreviewAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        if (webView.CoreWebView2 is null) throw new InvalidOperationException("地图尚未初始化，无法保存预览图。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var captureTask = await webView.Dispatcher.InvokeAsync(async () =>
        {
            await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        });
        await captureTask;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void SetActiveMapSource(MapSourceDefinition? source)
    {
        _activeSource = source;
        _activeTileHost = null;
        if (source is null) return;
        var sample = new MapSourceTemplateService().BuildTileUrl(source.UrlTemplate, 1, 1, 1);
        if (Uri.TryCreate(sample, UriKind.Absolute, out var uri)) _activeTileHost = uri.Host;
    }

    public string RegisterMediaThumbnail(byte[] pngBytes)
    {
        if (pngBytes.Length == 0 || pngBytes.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("地图缩略图大小无效。");
        var token = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pngBytes))[..32].ToLowerInvariant();
        while(!_mediaThumbnails.ContainsKey(token)&&_mediaThumbnails.Count>0&&(_mediaThumbnails.Count>=512||_mediaThumbnails.Values.Sum(v=>(long)v.Length)+pngBytes.Length>32L*1024*1024))_mediaThumbnails.Remove(_mediaThumbnails.Keys.First());
        _mediaThumbnails[token] = pngBytes;
        return $"https://hikingguide-media.local/media/{token}.png";
    }

    public void ClearMediaThumbnails() => _mediaThumbnails.Clear();

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var packet=System.Text.Json.JsonDocument.Parse(args.WebMessageAsJson);
            if(packet.RootElement.TryGetProperty("type",out var keyboardType)&&keyboardType.GetString()=="MapKeyboardFocus")
            {
                _mapKeyboardActive=packet.RootElement.TryGetProperty("active",out var active)&&active.ValueKind==System.Text.Json.JsonValueKind.True;
                System.Windows.Input.InputMethod.SetIsInputMethodEnabled(webView,!_mapKeyboardActive);
                return;
            }
            if(packet.RootElement.TryGetProperty("type",out var diagnosticType)&&diagnosticType.GetString()=="TerrainRenderDiagnostic")
            {
                if(packet.RootElement.TryGetProperty("message",out var diagnosticMessage))log.Warning("[地图][3D恢复] "+diagnosticMessage.GetString());
                return;
            }
            if(packet.RootElement.TryGetProperty("type",out var offlineType)&&offlineType.GetString() is "OfflineRegionSelected" or "OfflineRegionCanceled")
            {
                if(offlineType.GetString()=="OfflineRegionCanceled")_offlineSelection?.TrySetResult(null);
                else try
                {
                    var data=packet.RootElement.TryGetProperty("payload",out var payload)?payload:packet.RootElement;
                    var points=data.GetProperty("points").EnumerateArray().Select(p=>new HikingGuide.Core.Maps.OfflineMapPoint(p.GetProperty("longitude").GetDouble(),p.GetProperty("latitude").GetDouble())).ToList();
                    var region=new HikingGuide.Core.Maps.OfflineMapRegion(points);HikingGuide.Core.Maps.OfflineMapCoverage.CreateGeometry(region);_offlineSelection?.TrySetResult(region);
                }catch(Exception e)when(e is ArgumentException or InvalidOperationException or KeyNotFoundException){_offlineSelection?.TrySetException(new ArgumentException("选区无效，请选择不交叉的多边形。"));}
                return;
            }
            if(packet.RootElement.TryGetProperty("type",out var packetType)&&packetType.GetString()=="TerrainHealthCheck")
            {
                if(packet.RootElement.TryGetProperty("latitude",out var la)&&la.TryGetDouble(out var lat)&&packet.RootElement.TryGetProperty("longitude",out var lo)&&lo.TryGetDouble(out var lon)&&double.IsFinite(lat)&&double.IsFinite(lon)&&lat is >=-90 and <=90&&lon is >=-180 and <=180)
                    _=CheckTerrainHealthAsync(lat,lon);
                return;
            }
            if(packet.RootElement.TryGetProperty("type",out var workType)&&workType.GetString()=="TerrainWorkState")
            {
                if(packet.RootElement.TryGetProperty("requestId",out var rev)&&rev.TryGetInt64(out var revision)&&
                    packet.RootElement.TryGetProperty("ready",out var ready)&&ready.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    ((App)System.Windows.Application.Current).TerrainTiles?.SetWorkReady(revision,ready.GetBoolean());
                return;
            }
            if(packet.RootElement.TryGetProperty("type",out var type)&&type.GetString()=="TerrainViewport")
            {
                var bounds=packet.RootElement.TryGetProperty("payload",out var nested)?nested:packet.RootElement;
                if(bounds.TryGetProperty("west",out var w)&&w.TryGetDouble(out var west)&&bounds.TryGetProperty("south",out var s)&&s.TryGetDouble(out var south)&&bounds.TryGetProperty("east",out var e)&&e.TryGetDouble(out var east)&&bounds.TryGetProperty("north",out var n)&&n.TryGetDouble(out var north))
                {_terrainTiles?.SetViewport(west,south,east,north);((App)System.Windows.Application.Current).OsmRoads.SetViewport(west,south,east,north);}
                return;
            }
        }
        catch(System.Text.Json.JsonException){return;}
        var value = MapProtocol.TryParseEvent(args.WebMessageAsJson);
        if (value is null) return;
        if (value.Type == "MapReady")
        {
            IsReady = true;
            _readyTimeout?.Cancel();
        }
        EventReceived?.Invoke(this, value);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess) return;
        IsReady = false;
        InitializationFailed?.Invoke(this, $"本地地图页面加载失败（错误代码：{args.WebErrorStatus}）。可使用“重新加载地图”重试。");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        IsReady = false;
        log.Warning($"WebView2 地图进程异常：{args.ProcessFailedKind}");
        InitializationFailed?.Invoke(this, "地图进程意外停止，可使用“重新加载地图”恢复。项目和原始轨迹数据未受影响。");
    }

    private async Task CheckTerrainHealthAsync(double latitude,double longitude)
    {
        var generator=((App)System.Windows.Application.Current).TerrainTiles;if(generator is null)return;
        var revision=generator.Revision;
        var connected=await generator.CheckHealthAsync(latitude,longitude);
        if(revision!=generator.Revision)return;
        await SendAsync(MapProtocol.SerializeCommand("TerrainHealthResult",new{terrainRevision=revision,connected,
            message="当前3D地形DEM未连接、数据已更新或当前视野缺测，地图已切回2D。"}));
    }
    private async Task ServeTerrainAsync(CoreWebView2WebResourceRequestedEventArgs args,Uri uri,CoreWebView2Deferral deferral)
    {
        const string cors="Access-Control-Allow-Origin: https://hikingguide.local";
        try
        {
            var is3D=uri.AbsolutePath.StartsWith("/terrain/",StringComparison.Ordinal);
            var isSurface=uri.AbsolutePath.StartsWith("/contour-dem/",StringComparison.Ordinal);
            var generator=((App)System.Windows.Application.Current).TerrainTiles;
            if(is3D||isSurface?generator is null:_terrainTiles is null)throw new OperationCanceledException();
            var bytes=await Task.Run(()=>isSurface?generator!.GetGpuSurfaceAsync(uri):is3D?generator!.GetAsync(uri):_terrainTiles!.GetAsync(uri));
            args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(bytes,false),200,"OK",
                $"Content-Type: {(isSurface?"application/octet-stream":uri.AbsolutePath.EndsWith(".json",StringComparison.Ordinal)?"application/json":"image/png")}\r\nAccess-Control-Allow-Origin: https://hikingguide.local\r\nCache-Control: no-store");
        }
        catch(OperationCanceledException) {args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),410,"Gone","Access-Control-Allow-Origin: https://hikingguide.local");}
        catch(ArgumentException){args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),400,"Bad Request",cors);}
        catch(FileNotFoundException){args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),404,"Not Found",cors);}
        catch(Exception exception)
        {
            log.Error(exception,"DEM地图瓦片生成失败，请检查DEM挂载、单位及缓存目录");
            args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),503,"Unavailable","Access-Control-Allow-Origin: https://hikingguide.local");
        }
        finally {deferral.Complete();}
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if(Uri.TryCreate(args.Request.Uri,UriKind.Absolute,out var roadUri)&&roadUri.Host=="hikingguide-roads.local")
        {var deferral=args.GetDeferral();_=ServeRoadsAsync(args,roadUri,deferral);return;}
        if(Uri.TryCreate(args.Request.Uri,UriKind.Absolute,out var terrainUri)&&terrainUri.Host=="hikingguide-terrain.local")
        {var deferral=args.GetDeferral();_=ServeTerrainAsync(args,terrainUri,deferral);return;}
        if(Uri.TryCreate(args.Request.Uri,UriKind.Absolute,out var tileUri)&&tileUri.Host=="hikingguide-tiles.local")
        {
            var request=_tileRequests.Resolve(tileUri);
            if(request is null) {args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),410,"Gone","Content-Type: text/plain");return;}
            var deferral=args.GetDeferral(); _=ServeTileAsync(args,request,deferral); return;
        }
        if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var mediaUri) &&
            mediaUri.Host.Equals("hikingguide-media.local", StringComparison.OrdinalIgnoreCase) &&
            mediaUri.AbsolutePath.StartsWith("/media/", StringComparison.Ordinal) &&
            mediaUri.AbsolutePath.EndsWith(".png", StringComparison.Ordinal))
        {
            var token = mediaUri.AbsolutePath["/media/".Length..^".png".Length];
            if (_mediaThumbnails.TryGetValue(token, out var bytes))
                args.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(bytes, writable: false), 200, "OK",
                    "Content-Type: image/png\r\nAccess-Control-Allow-Origin: https://hikingguide.local\r\nCache-Control: no-store");
            else args.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(), 404, "Not Found", "Content-Type: text/plain");
            return;
        }
        var source = _activeSource;
        if (source is null || string.IsNullOrWhiteSpace(_activeTileHost) ||
            !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requestUri) ||
            !string.Equals(requestUri.Host, _activeTileHost, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(source.UserAgent)) args.Request.Headers.SetHeader("User-Agent", source.UserAgent);
            if (!string.IsNullOrWhiteSpace(source.Referer)) args.Request.Headers.SetHeader("Referer", source.Referer);
            foreach (var header in source.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                args.Request.Headers.SetHeader(header.Key, header.Value);
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "应用地图图源请求头失败（请求头内容未写入日志）");
        }
    }

    private async Task ServeRoadsAsync(CoreWebView2WebResourceRequestedEventArgs args,Uri uri,CoreWebView2Deferral deferral)
    {
        const string cors="Access-Control-Allow-Origin: https://hikingguide.local";
        try
        {
            var source=((App)System.Windows.Application.Current).OsmRoads;
            var bytes=await Task.Run(()=>source.GetAsync(uri));
            args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(bytes,false),200,"OK","Content-Type: application/vnd.mapbox-vector-tile\r\n"+cors+"\r\nCache-Control: no-store");
        }
        catch(OperationCanceledException){args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),410,"Gone",cors);}
        catch(ArgumentException){args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),400,"Bad Request",cors);}
        catch(Exception e){log.Error(e,"OSM路网只读生成失败");args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),503,"Unavailable",cors);}
        finally{deferral.Complete();}
    }

    private async Task ServeTileAsync(CoreWebView2WebResourceRequestedEventArgs args,System.Net.Http.HttpRequestMessage request,CoreWebView2Deferral deferral)
    {
        var tileUri=new Uri(args.Request.Uri);
        var profileKey=tileUri.AbsolutePath.Split('/')[1];
        var profileToken=_tileProfiles.TryGetValue(profileKey,out var profile)?profile.Token:CancellationToken.None;
        var baseRequest=tileUri.AbsolutePath.StartsWith("/base-",StringComparison.Ordinal);
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(profileToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using(request)
            {
                async Task<HikingGuide.Infrastructure.Maps.CachedMapTile> Download(System.Net.Http.HttpRequestMessage message)
                {
                    // 缓存及PMTiles读取不占网络名额；慢叠加图源不能占满底图的下载通道。
                    var gate=baseRequest?_baseTileGate:_overlayTileGate;
                    await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                    try {
                    using var response=await (TileFetchForTest is {} testFetch?testFetch(message,timeout.Token):HikingGuide.Infrastructure.Maps.MapTileHttpTransport.SendAsync(_tileClient,message,timeout.Token)).ConfigureAwait(false);
                    if(!response.IsSuccessStatusCode)throw new System.Net.Http.HttpRequestException("瓦片下载失败",null,response.StatusCode);
                    await using var input=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);using var output=new MemoryStream();var buffer=new byte[65536];
                    while(true){var count=await input.ReadAsync(buffer,timeout.Token).ConfigureAwait(false);if(count==0)break;if(output.Length+count>8*1024*1024)throw new InvalidDataException("瓦片超过大小限制。");await output.WriteAsync(buffer.AsMemory(0,count),timeout.Token).ConfigureAwait(false);}
                    var type=response.Content.Headers.ContentType?.MediaType??"image/png";var control=response.Headers.CacheControl;
                    var now=DateTimeOffset.UtcNow;
                    var expires=control?.MaxAge is {} age ? now+age-TimeSpan.FromSeconds(response.Headers.Age?.TotalSeconds??0)
                        : response.Content.Headers.Expires ?? now.AddHours(24);
                    var cacheable=control?.NoStore!=true && control?.NoCache!=true && type.StartsWith("image/",StringComparison.OrdinalIgnoreCase);
                    return new(output.ToArray(),type,expires,cacheable);
                    }finally{gate.Release();}
                }
                Task<HikingGuide.Infrastructure.Maps.CachedMapTile> Fetch()=>Download(request);
                var hasCache=request.Options.TryGetValue(MapTileRequestRegistry.CacheRequestKey,out var info);
                var library=((App)System.Windows.Application.Current).OfflineMaps;
                _reprojection??=new(_tileCache);
                var tile=await Task.Run(async()=>{
                    HikingGuide.Application.Maps.OfflineTileData? offline=null;
                    if(request.Options.TryGetValue(MapTileRequestRegistry.OfflineRequestKey,out var lookup))
                        offline=await library.ReadTileAsync(lookup.Fingerprint,new(lookup.Zoom,lookup.X,lookup.Y),timeout.Token).ConfigureAwait(false);
                    return offline is not null?new HikingGuide.Infrastructure.Maps.CachedMapTile(offline.Bytes,offline.ContentType,DateTimeOffset.MaxValue):await (request.Options.TryGetValue(MapTileRequestRegistry.ReprojectionRequestKey,out var correction)
                        ?_reprojection.GetAsync(correction,Download,timeout.Token)
                        :hasCache?_tileCache.GetAsync(info!.SourceId,info.Key,info.CapacityBytes,Fetch):Fetch()).ConfigureAwait(false);
                });
                var age=Math.Max(0,Math.Min(300,(int)(tile.ExpiresAt-DateTimeOffset.UtcNow).TotalSeconds));
                // 已由按图源计费的磁盘缓存管理的瓦片不再另存浏览器缓存，避免清空后绕过所选图源缓存。
                // MapLibre当前视野内存图块仍保留，不触发额外下载或强制刷新。
                var browserPolicy=!hasCache&&tile.CanCache?$"max-age={age}":"no-store";
                args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(tile.Bytes,false),200,"OK",$"Content-Type: {tile.ContentType}\r\nAccess-Control-Allow-Origin: https://hikingguide.local\r\nCache-Control: {browserPolicy}");
            }
        }
        catch(System.Net.Http.HttpRequestException e)
        {
            args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),(int)(e.StatusCode??System.Net.HttpStatusCode.BadGateway),"Tile network error","Content-Type: text/plain");
        }
        catch(Exception)
        {
            args.Response=webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),502,"Tile network error","Content-Type: text/plain");
        }
        finally {deferral.Complete();}
    }

    private async Task WatchReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            if (IsReady) return;
            log.Warning("本地地图页面在 15 秒内未发送 MapReady。");
            await webView.Dispatcher.InvokeAsync(() =>
                InitializationFailed?.Invoke(this, "地图模块启动失败，本地地图页面未能完成初始化。可使用“重新加载地图”重试。"));
        }
        catch (OperationCanceledException) { }
    }
}
