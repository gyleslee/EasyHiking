using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Core.Maps;
using HikingGuide.Infrastructure.Maps;
using Microsoft.Web.WebView2.Wpf;
using SkiaSharp;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunOfflineMapSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;object? metrics=null;var folder=Path.GetDirectoryName(output)!;var listener=new TcpListener(IPAddress.Loopback,0);var requests=0;var stopping=false;
        try
        {
            Directory.CreateDirectory(folder);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
            byte[] png;using(var bitmap=new SKBitmap(256,256)){using var canvas=new SKCanvas(bitmap);canvas.Clear(new SKColor(239,232,199));using var paint=new SKPaint{Color=new SKColor(104,158,91),StrokeWidth=5};for(var i=0;i<8;i++)canvas.DrawLine(0,i*40,256,i*40+100,paint);using var bytes=bitmap.Encode(SKEncodedImageFormat.Png,100);png=bytes.ToArray();}
            var server=Task.Run(async()=>{
                while(!stopping)
                {
                    TcpClient client;try{client=await listener.AcceptTcpClientAsync();}catch(SocketException){break;}catch(ObjectDisposedException){break;}
                    _=Task.Run(async()=>{using(client){try{using var stream=client.GetStream();var header=new List<byte>();var one=new byte[1];while(header.Count<16000&&await stream.ReadAsync(one)>0){header.Add(one[0]);if(header.Count>=4&&header[^4]==13&&header[^3]==10&&header[^2]==13&&header[^1]==10)break;}Interlocked.Increment(ref requests);var request=Encoding.ASCII.GetString(header.ToArray());var authorized=request.Contains("X-Offline-Test: private-local-test",StringComparison.OrdinalIgnoreCase);var body=authorized?png:[];var response=Encoding.ASCII.GetBytes($"HTTP/1.1 {(authorized?"200 OK":"401 Unauthorized")}\r\nContent-Type: image/png\r\nContent-Length: {body.Length}\r\nCache-Control: max-age=3600\r\nConnection: close\r\n\r\n");await stream.WriteAsync(response);await stream.WriteAsync(body);}catch(IOException){}}});
                }
            });
            for(var i=0;i<300&&!main.Map.IsReady;i++)await Task.Delay(100);if(!main.Map.IsReady)throw new InvalidOperationException("地图未就绪");
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            async Task Wait(Func<Task<bool>> ready,string message){for(var i=0;i<1800;i++){if(await ready())return;await Task.Delay(50);}throw new InvalidOperationException(message);}
            var source=new MapSourceDefinition{Name="离线功能验证图源",UrlTemplate=$"http://127.0.0.1:{port}/{{z}}/{{x}}/{{y}}.png",Headers=new(){["X-Offline-Test"]="private-local-test"},MaxZoom=18};GlobalSettingsService.Current.MapSources.Add(source);
            await main.Map.ApplyGlobalMapLayersAsync(true);await JS("window.hikingGuide3DTest.at([113.32,24.42],13)");
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};
            _=Dispatcher.BeginInvoke(new Action(()=>manager.ShowDialog()));await Task.Delay(300);
            var tabs=T10ManualField<TabControl>(manager,"LibraryTabs");tabs.SelectedIndex=3;var panel=T10ManualField<OfflineMapDownloadPanel>(manager,"OfflinePanel");
            T10ManualField<ComboBox>(panel,"SourceBox").SelectedItem=((IEnumerable<MapSourceDefinition>)T10ManualField<ComboBox>(panel,"SourceBox").ItemsSource).First(s=>s.Id==source.Id);
            T10ManualField<ComboBox>(panel,"MinZoomBox").SelectedItem=13;T10ManualField<ComboBox>(panel,"MaxZoomBox").SelectedItem=16;T10ManualField<TextBox>(panel,"IntervalBox").Text="100";
            checks["下载离线地图位于OSM路网右侧第四个标签"]=tabs.Items.Count==4&&((TabItem)tabs.SelectedItem).Header.ToString()=="下载离线地图";
            T10ManualField<Button>(panel,"SelectRegionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(async()=>!manager.IsVisible&&(await JS("window.hikingGuideOfflineTest.inspect()")).GetProperty("active").GetBoolean(),"地图选区没有启动");
            checks["选区期间隐藏模态管理窗口并允许操作主地图"]=window.IsEnabled;
            foreach(var p in new[]{(113.29,24.39),(113.35,24.39),(113.35,24.45),(113.29,24.45)})await JS(FormattableString.Invariant($"window.hikingGuideOfflineTest.click({p.Item1},{p.Item2})"));
            await using(var capture=File.Create(Path.Combine(folder,"T185-地图多边形选区.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
            await JS("window.hikingGuideOfflineTest.finish()");await Wait(()=>Task.FromResult(manager.IsVisible),"选区完成后地图管理没有恢复");
            checks["完成四点多边形并恢复下载面板"]=T10ManualField<TextBlock>(panel,"RegionInfo").Text.Contains("4 个");
            T10ManualField<Button>(panel,"EstimateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>Task.FromResult(T10ManualField<Button>(panel,"StartButton").IsEnabled),"瓦片数量和大小预估未完成");
            checks["真实HTTP瓦片抽样获得大小预估"]=requests>0&&T10ManualField<TextBlock>(panel,"EstimateInfo").Text.Contains("预估");
            T10ManualField<TextBox>(panel,"MapNameBox").Text="离线地图操作演示";T10ManualField<Button>(panel,"StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>Task.FromResult(AppOfflineTask()?.CompletedTiles>=2),"下载任务未启动");
            await OfflineDownloads.PauseAsync();var task=AppOfflineTask()!;var completed=task.CompletedTiles;checks["下载暂停后保留阶段数据"]=completed>0&&task.State==OfflineDownloadState.Paused;
            await OfflineDownloads.ResumeAsync(task.Id);await Wait(()=>Task.FromResult(!OfflineDownloads.IsBusy),"续传未完成");
            if(task.State!=OfflineDownloadState.Completed)throw new InvalidOperationException(task.Message);
            var pack=OfflineMaps.Packs.Single();checks["完成文件按图源名称归入专门离线目录"]=new DirectoryInfo(Path.GetDirectoryName(pack.FilePath)!).Name==OfflineMapDownloads.SafeName(source.Name)&&new DirectoryInfo(OfflineMaps.RootDirectory).Name=="离线地图";checks["续传生成完整PMTiles和规范文件名"]=pack.TileCount==task.PlannedTiles&&!pack.IsPartial&&Path.GetFileName(pack.FilePath).StartsWith("离线地图操作演示-离线功能验证图源-")&&pack.FilePath.EndsWith(".pmtiles");
            await T10ManualLayoutAsync(manager);await CaptureT10ClientAsync(manager,Path.Combine(folder,"T185-下载离线地图面板.png"));manager.Close();await Wait(()=>Task.FromResult(!manager.IsVisible),"地图管理未关闭");
                        GlobalSettingsService.Current.MapLayerStack=GlobalSettingsService.Current.MapLayerStack! with{BaseSourceId=source.Id,Overlay1Visible=false,Overlay2Visible=false};await main.Map.ApplyGlobalMapLayersAsync(true);
            var onlineBefore=requests;await JS("window.hikingGuide3DTest.at([113.32,24.42],16)");
            await Wait(async()=>requests>onlineBefore&&(await JS("window.hikingGuideRasterTest.inspect()")).GetProperty("slots")[0].GetProperty("loaded").GetBoolean(),"超出下载层级后没有在线加载");
            checks["超出离线最高层级继续从在线图源加载"]=requests>onlineBefore;
            var count=requests;stopping=true;listener.Stop();await server;
            await MapTileDiskCache.Shared(Path.Combine(new HikingGuide.Infrastructure.Projects.ApplicationDataPaths().RootDirectory,"MapTileCache")).ClearAsync(source.Id);
            var hits=OfflineMaps.ReadHits;GlobalSettingsService.Current.MapLayerStack=GlobalSettingsService.Current.MapLayerStack! with{BaseSourceId=source.Id,Overlay1Visible=false,Overlay2Visible=false};await main.Map.ApplyGlobalMapLayersAsync(true);
            await JS("window.hikingGuide3DTest.at([113.32,24.42],15)");
                        await Wait(async()=>{var state=await JS("window.hikingGuideRasterTest.inspect()");await File.WriteAllTextAsync(Path.Combine(folder,"offline-debug.json"),JsonSerializer.Serialize(new{State=state,Hits=OfflineMaps.ReadHits,Before=hits,SourceFingerprint=HikingGuide.Application.Maps.OfflineMapSourceIdentity.Fingerprint(source),PackFingerprint=pack.SourceFingerprint,Warning=OfflineMaps.Warning,MapStatus=main.Map.MapStatusText,WindowVisible=window.IsVisible,WebVisible=web.IsVisible,window.IsEnabled}));return OfflineMaps.ReadHits>hits&&state.GetProperty("slots")[0].GetProperty("loaded").GetBoolean();},"断网后离线地图未显示");
            checks["关闭本地图源服务器并清空普通缓存仍从PMTiles显示地图"]=OfflineMaps.ReadHits>hits&&requests==count;
            await using(var capture=File.Create(Path.Combine(folder,"T185-PMTiles离线显示.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T185_3D_CHECK")=="1")
            {
                var dem=DemCatalog.Databases.First(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
                GlobalSettingsService.Current.OfflineTerrain=new(){DemDatabaseId=dem.Id,ContoursVisible=false,HillshadeVisible=false};
                GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id};
                await main.Map.ApplyGlobalMapLayersAsync(true);
                await JS("window.hikingGuide3DTest.at([113.32,24.42],15)");
                await JS("window.hikingGuide3DTest.camera(40,25)");
                await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect()")).GetProperty("effective").GetBoolean(),"PMTiles底图上的DEM三维地形未就绪");
                var before3D=OfflineMaps.ReadHits;
                await JS("(()=>{const source=window.hikingGuideRasterTest.sourceInstance('base');source.setTiles([...source.tiles]);return true;})()");
                await Wait(async()=>OfflineMaps.ReadHits>before3D&&(await JS("window.hikingGuideRasterTest.inspect()")).GetProperty("slots")[0].GetProperty("loaded").GetBoolean(),"3D重新读取PMTiles未完成");
                var state3D=await JS("window.hikingGuide3DTest.inspect()");var camera3D=await JS("window.hikingGuideRasterTest.inspect()");
                checks["3D地形有效时从PMTiles重新读取底图"]=state3D.GetProperty("effective").GetBoolean()&&OfflineMaps.ReadHits>before3D&&camera3D.GetProperty("pitch").GetDouble()>=39;
                checks["3D读取未依赖演示服务器或普通瓦片缓存"]=requests==count;
                await using(var capture=File.Create(Path.Combine(folder,"T185-PMTiles-DEM三维离线显示.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
                GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D with{Enabled=false};await main.Map.ApplyGlobalMapLayersAsync(true);
                await JS("window.hikingGuide3DTest.camera(0,0)");
                checks["PMTiles底图从3D切回2D保持可读"]=await OfflineMaps.ReadTileAsync(pack.SourceFingerprint,OfflineMapCoverage.Enumerate(pack.Region,16,16).First()) is not null;
            }
            var address=OfflineMapCoverage.Enumerate(pack.Region,16,16).First();await OfflineMaps.SetEnabledAsync(pack.Id,false);checks["离线包停用后不命中"]=await OfflineMaps.ReadTileAsync(pack.SourceFingerprint,address) is null;await OfflineMaps.SetEnabledAsync(pack.Id,true);
            var renamed=source.DeepCopy();renamed.Name="改名后同一图源";checks["图源改名仍可复用离线包"]=await OfflineMaps.ReadTileAsync(HikingGuide.Application.Maps.OfflineMapSourceIdentity.Fingerprint(renamed),address) is not null;renamed.CoordinateSystem=MapSourceCoordinateSystem.Gcj02;checks["坐标系改变后不会误用旧离线包"]=await OfflineMaps.ReadTileAsync(HikingGuide.Application.Maps.OfflineMapSourceIdentity.Fingerprint(renamed),address) is null;
            File.Copy(pack.FilePath,Path.Combine(folder,"standard-validation.pmtiles"),true);
            metrics=new{pack.TileCount,pack.FileBytes,Requests=count,OfflineHits=OfflineMaps.ReadHits,pack.FilePath};
            OfflineDownloadTask? AppOfflineTask()=>OfflineDownloads.Tasks.FirstOrDefault();
        }
        catch(Exception exception){error=exception.ToString();}
        finally{stopping=true;listener.Stop();await OfflineDownloads.StopAsync();}
        var passed=error is null&&checks.Count>0&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Metrics=metrics,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}





