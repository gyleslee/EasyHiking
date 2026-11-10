using System.IO;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Application.Maps;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunT195SmokeAsync(MainWindow owner,MainViewModel main,string output)
    {
        var diagnostics=new List<object>();var checks=new Dictionary<string,bool>();var timings=new Dictionary<string,double>();string? error=null;
        var folder=Path.GetDirectoryName(output)!;
        var readyDeadline=DateTime.UtcNow.AddSeconds(20);
        while(!FavoritesPanel.Descendants<WebView2>(owner).Any(w=>w.CoreWebView2 is not null&&w.Source?.Host=="hikingguide.local")&&DateTime.UtcNow<readyDeadline)await Task.Delay(50);
        var web=FavoritesPanel.Descendants<WebView2>(owner).First(w=>w.CoreWebView2 is not null&&w.Source?.Host=="hikingguide.local");
        var bridge=FavoritesPanel.Descendants<MapView>(owner).First().BridgeForAutomation!;
        async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
        async Task Wait(Func<Task<bool>> predicate,string label,int ms=120000){var until=DateTime.UtcNow.AddMilliseconds(ms);while(DateTime.UtcNow<until){if(await predicate())return;await Task.Delay(30);}throw new TimeoutException(label);}
        try
        {
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");
            checks["实际桌面进程SQLite已包含CVE-2025-6965修复"]=Version.Parse(Favorites.SqliteVersion)>=new Version(3,50,2);diagnostics.Add(new{SqliteVersion=Favorites.SqliteVersion});
            await main.Map.ApplyGlobalMapLayersAsync();await Task.Delay(200);
            using var surface=SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(256,256));surface.Canvas.Clear(new SkiaSharp.SKColor(211,228,192));using var png=surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();
            var downloads=0;var downloadDelay=20;
            bridge.TileFetchForTest=async(request,token)=>{Interlocked.Increment(ref downloads);await Task.Delay(downloadDelay,token);var content=new ByteArrayContent(bytes);content.Headers.ContentType=new("image/png");var response=new HttpResponseMessage(HttpStatusCode.OK){Content=content};response.Headers.CacheControl=new(){MaxAge=TimeSpan.FromDays(1)};return response;};
            var source=new MapSourceDefinition{Name="连续显示验证底图",UrlTemplate="https://continuity-base.invalid/{z}/{x}/{y}.png",MaxZoom=19};
            var overlay=new MapSourceDefinition{Name="连续显示验证叠加",UrlTemplate="https://continuity-overlay.invalid/{z}/{x}/{y}.png",MaxZoom=19,Role=MapSourceRole.Overlay};
            GlobalSettingsService.Current.MapSources.Add(source);GlobalSettingsService.Current.MapSources.Add(overlay);
            GlobalSettingsService.Current.MapLayerStack=GlobalSettingsService.Current.MapLayerStack! with{BaseSourceId=source.Id,BaseOpacity=1,Overlay1SourceId=overlay.Id,Overlay1Visible=true,Overlay1Opacity=.15,Overlay2Visible=false};
            var dem=DemCatalog.Databases.First(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            GlobalSettingsService.Current.OfflineTerrain=new(){DemDatabaseId=dem.Id,ContoursVisible=true,HillshadeVisible=true,ContourOpacity=.8,HillshadeOpacity=.3};
            GlobalSettingsService.Current.OsmRoads=new(){Enabled=true,Opacity=1};
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            await JS("window.hikingGuide3DTest.at([113.269571,24.464423],16);true");
            await main.Map.ApplyGlobalMapLayersAsync(true);
            async Task Ready(){await Wait(async()=>{
                var terrain=await JS("window.hikingGuideTerrainTest.inspect()");var roads=await JS("window.hikingGuideOsmRoadTest.inspect()");
                return terrain.GetProperty("layers").EnumerateArray().Where(l=>l.ValueKind!=JsonValueKind.Null&&l.GetProperty("id").GetString()!="dem-contours-low").All(l=>l.GetProperty("loaded").GetBoolean()&&l.GetProperty("visible").GetBoolean())&&roads.GetProperty("features").GetInt32()>0;
            },"真实DEM派生图层或rd5路网未显示");}
            await Ready();checks["真实FathomDEM等高线阴影与rd5路网同时显示"]=OsmRoads.ChunksDecoded>0&&OsmRoads.TilesEncoded>0;
            var startupProject=new HikingProject{Name="启动加载顺序隔离验证"};
            startupProject.Settings.Map.Center=new HikingGuide.Core.Geometry.GeoCoordinate(24.464423,113.269571);startupProject.Settings.Map.Zoom=16;
            var startupPath=Path.Combine(folder,"startup.hikeproj");await new ProjectFileService().SaveAsync(startupProject,startupPath,false);await main.OpenForAutomationAsync(startupPath);await main.WaitForMapRefreshForAutomationAsync();
            async Task Startup(string label,bool cold,bool is3D){
                if(cold){var fresh=new MapSourceDefinition{Name="启动底图隔离验证",UrlTemplate="https://startup-base.invalid/"+Guid.NewGuid().ToString("N")+"/{z}/{x}/{y}.png",MaxZoom=19};GlobalSettingsService.Current.MapSources.Add(fresh);GlobalSettingsService.Current.MapLayerStack=GlobalSettingsService.Current.MapLayerStack! with{BaseSourceId=fresh.Id};}
                GlobalSettingsService.Current.Terrain3D=new(){Enabled=is3D,TerrainDemDatabaseId=dem.Id,DefaultPitch=45,Exaggeration=1.5};
                GlobalSettingsService.Current.TerrainCamera=new(24.464423,113.269571,16,0,is3D?45:0);
                var oldOrigin=(await JS("performance.timeOrigin")).GetDouble();downloadDelay=300;var beforeDownloads=downloads;
                await bridge.ReloadAsync();
                await Wait(async()=>{var s=await JS("({origin:performance.timeOrigin,ready:!!window.hikingGuideStartupTest})");return s.GetProperty("origin").GetDouble()!=oldOrigin&&s.GetProperty("ready").GetBoolean();},"启动测试地图页面未重载");
                var samples=0;var sawWaiting=false;var noEarlyLayers=true;JsonElement final=default;
                await Wait(async()=>{
                    var s=await JS("({startup:window.hikingGuideStartupTest.inspect(),layers:window.hikingGuideLayerContinuityTest.inspect().layers})");
                    var state=s.GetProperty("startup");samples++;
                    if(state.GetProperty("waiting").GetBoolean()){
                        sawWaiting=true;noEarlyLayers&=!s.GetProperty("layers").EnumerateArray().Any(l=>l.GetProperty("visible").GetBoolean());return false;
                    }
                    final=state;return true;
                },"启动底图优先阶段未结束",10000);
                checks[label+"底图绘制前没有启动其他叠加层"]=sawWaiting&&noEarlyLayers;
                checks[label+"底图实际绘制后放行而非超时"]=final.GetProperty("reason").GetString()=="base-rendered"&&final.GetProperty("baseFrameMs").GetDouble()>0;
                await Ready();
                if(is3D)await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean(),"启动3D地形未恢复");
                checks[label+"随后恢复所有已开启图层"]=!(await JS("window.hikingGuideStartupTest.inspect().waiting")).GetBoolean();
                diagnostics.Add(new{Startup=label,Samples=samples,State=final,Downloads=downloads-beforeDownloads});
                if(final.GetProperty("baseFrameMs").ValueKind==JsonValueKind.Number)timings[label+"底图首帧毫秒"]=final.GetProperty("baseFrameMs").GetDouble();downloadDelay=20;
            }
            await Startup("冷缓存2D启动",true,false);await Startup("热缓存2D启动",false,false);await Startup("热缓存3D启动",false,true);
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};await main.Map.ApplyGlobalMapLayersAsync(true);
            await JS("window.hikingGuide3DTest.at([113.269571,24.464423],16);true");await Ready();
            async Task Motion(string label,bool is3D,string gesture){
                await JS("window.hikingGuideLayerContinuityTest.remember();true");
                var count=0;var moving=0;var stable=true;var visible=true;var terrainStable=true;var minRoads=int.MaxValue;var minRoadPixels=int.MaxValue;var minZoom=double.MaxValue;var maxZoom=double.MinValue;
                async Task Sample(){var state=await JS("window.hikingGuideLayerContinuityTest.inspect()");count++;var sampleZoom=state.GetProperty("zoom").GetDouble();minZoom=Math.Min(minZoom,sampleZoom);maxZoom=Math.Max(maxZoom,sampleZoom);if(state.GetProperty("moving").GetBoolean())moving++;
                    stable&=state.GetProperty("sameSources").GetBoolean();
                    var layers=state.GetProperty("layers").EnumerateArray().ToArray();
                    visible&=layers.Count(l=>l.GetProperty("id").GetString() is "raster-overlay-1-layer" or "dem-hillshade" or "dem-contours-high")==3&&layers.All(l=>l.GetProperty("visible").GetBoolean());
                    minRoads=Math.Min(minRoads,state.GetProperty("roads").GetInt32());
                    if(count is 2 or 7 or 12){
                        using var imageStream=new MemoryStream();await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,imageStream);var imageBytes=imageStream.ToArray();
                        using var image=SkiaSharp.SKBitmap.Decode(imageBytes);var pixels=0;
                        for(var y=45;y<image.Height-60;y++)for(var x=15;x<image.Width-160;x++){var pixel=image.GetPixel(x,y);if(pixel.Alpha>0&&pixel.Red<90&&pixel.Green<90&&pixel.Blue<90)pixels++;}
                        minRoadPixels=Math.Min(minRoadPixels,pixels);await File.WriteAllBytesAsync(Path.Combine(folder,label+"-motion-"+count+".png"),imageBytes);
                    }
                    terrainStable&=state.GetProperty("terrain").GetBoolean()==is3D&&(!is3D||state.GetProperty("effective").GetBoolean());
                }
                var button=gesture=="rotate"?"right":"left";var mask=gesture=="rotate"?2:1;
                if(gesture=="zoom"){
                    await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent","{\"type\":\"mouseWheel\",\"x\":420,\"y\":260,\"deltaX\":0,\"deltaY\":-100}");
                    for(var i=0;i<18;i++){await Task.Delay(30);await Sample();}
                }else{
                    await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type="mousePressed",x=420,y=260,button,buttons=mask,clickCount=1}));
                    for(var i=1;i<=12;i++){
                        await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type="mouseMoved",x=420+i*5,y=260+i*2,button,buttons=mask}));await Task.Delay(55);await Sample();
                    }
                    await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type="mouseReleased",x=480,y=284,button,buttons=0,clickCount=1}));
                }
                for(var i=0;i<8;i++){await Task.Delay(45);await Sample();}
                checks[label+"过程中所有已启用图层保持显示"]=visible;
                checks[label+"不重建图源且地形模式保持"]=stable&&terrainStable;
                checks[label+"运动中采样且道路实际像素持续可见"]=moving>0&&minRoadPixels>20;
                diagnostics.Add(new{Motion=label,Samples=count,MovingSamples=moving,MinRoadFeatures=minRoads,MinRoadPixels=minRoadPixels,MinZoom=minZoom,MaxZoom=maxZoom,StableSources=stable,Visible=visible,TerrainStable=terrainStable});
            }
            await Motion("2D左键拖动",false,"pan");await Motion("2D滚轮缩放",false,"zoom");
            await using(var shot=File.Create(Path.Combine(folder,"2D-layer-continuity.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,shot);
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id,DefaultPitch=45,Exaggeration=1.5};await main.Map.ApplyGlobalMapLayersAsync(true);
            await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean(),"3D地形未就绪");
            await JS("window.hikingGuide3DTest.at([113.269571,24.464423],16);window.hikingGuide3DTest.camera(45,0);true");await Task.Delay(350);await Ready();
            await Motion("3D左键拖动",true,"pan");await Motion("3D滚轮缩放",true,"zoom");await Motion("3D右键旋转",true,"rotate");
            await using(var shot=File.Create(Path.Combine(folder,"3D-layer-continuity.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,shot);
            GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=false,HillshadeVisible=false};GlobalSettingsService.Current.OsmRoads.Enabled=false;await main.Map.ApplyGlobalMapLayersAsync(true);
            await JS("window.hikingGuideTrackSceneTest.camera(16,0,0,[113.2698,24.4645]);true");await Task.Delay(350);
            var hidden=await JS("window.hikingGuideTerrainTest.inspect()");checks["用户关闭的等高线阴影和路网不会因移动被开启"]=hidden.GetProperty("layers").EnumerateArray().Where(l=>l.ValueKind!=JsonValueKind.Null).All(l=>!l.GetProperty("visible").GetBoolean())&&!(await JS("window.hikingGuideOsmRoadTest.inspect().enabled")).GetBoolean();
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(200);
            timings["网络瓦片请求次数"]=downloads;
            // 点击画布建立真实DOM焦点，再用Windows输入路径及中文IME物理码验证。
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent","{\"type\":\"mousePressed\",\"x\":180,\"y\":160,\"button\":\"left\",\"buttons\":1,\"clickCount\":1}");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent","{\"type\":\"mouseReleased\",\"x\":180,\"y\":160,\"button\":\"left\",\"buttons\":0,\"clickCount\":1}");await Task.Delay(100);
            checks["点击地图后画布获得焦点并停用地图IME"]= (await JS("document.activeElement===document.querySelector('canvas.maplibregl-canvas')")).GetBoolean()&&!InputMethod.GetIsInputMethodEnabled(web);
            var before=await JS("window.hikingGuideT10Test.inspect().camera");
            var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(web),0,Key.W){RoutedEvent=Keyboard.PreviewKeyDownEvent};web.RaiseEvent(key);await Task.Delay(150);
            var after=await JS("window.hikingGuideT10Test.inspect().camera");checks["WPF物理W键可移动地图且被消费"]=key.Handled&&after.GetProperty("latitude").GetDouble()>before.GetProperty("latitude").GetDouble();
            var ime=await JS("(()=>{let c=document.querySelector('canvas.maplibregl-canvas'),e=new KeyboardEvent('keydown',{key:'Process',code:'KeyA',isComposing:true,bubbles:true,cancelable:true});c.dispatchEvent(e);return e.defaultPrevented;})()");checks["中文IME的Process按键仍识别物理方向"]=ime.GetBoolean();
            await JS("window.t195input=document.createElement('input');document.body.appendChild(window.t195input);window.t195input.focus();true");await Task.Delay(80);checks["文字输入框恢复IME不抢占WASD"]=InputMethod.GetIsInputMethodEnabled(web);await JS("window.t195input.remove();true");
            var project=new HikingProject{Name="规划光标隔离验证"};var path=Path.Combine(folder,"planning.hikeproj");await new ProjectFileService().SaveAsync(project,path,false);await main.OpenForAutomationAsync(path);await main.WaitForMapRefreshForAutomationAsync();main.ConfigureRouting(new T194Router());main.StartMixedPlanCommand.Execute(null);
            await Wait(()=>Task.FromResult(main.IsRoutePlanningActive),"混合规划未开始");
            await Wait(async()=>(await JS("getComputedStyle(document.querySelector('canvas.maplibregl-canvas')).cursor")).GetString()=="crosshair","规划光标未就绪");
            foreach(var (label,command) in new[]{("手绘",main.NextManualLegCommand),("BRouter",main.NextBRouterLegCommand),("再次手绘",main.NextManualLegCommand)}){
                command.Execute(null);await JS("document.querySelector('canvas.maplibregl-canvas').style.cursor='';true");
                checks["混合规划切换"+label+"保持十字光标"]=(await JS("getComputedStyle(document.querySelector('canvas.maplibregl-canvas')).cursor")).GetString()=="crosshair";
            }
            await Wait(()=>Task.FromResult(FavoritesPanel.Descendants<System.Windows.Controls.TextBlock>(owner).Any(t=>t.Text=="右击撤销最后一个点")),"规划栏提示未显示");
            checks["规划栏显示右击撤销提示"]=FavoritesPanel.Descendants<System.Windows.Controls.TextBlock>(owner).Any(t=>t.Text=="右击撤销最后一个点");
            await main.Map.RunP7PlanningClickSmokeAsync(24.44,113.23);await Wait(()=>Task.FromResult(main.RoutePlanningPoints.Count==1),"截图起点未加入");await main.Map.RunP7PlanningClickSmokeAsync(24.441,113.231);
            await Wait(()=>Task.FromResult(main.RoutePlanningPoints.Count==2),"截图规划点未加入");owner.UpdateLayout();await Task.Delay(100);
            var panel=(FrameworkElement)owner.FindName("BottomTabs");
            var visual=new System.Windows.Media.DrawingVisual();using(var context=visual.RenderOpen())context.DrawRectangle(new System.Windows.Media.VisualBrush(panel),null,new Rect(0,0,panel.ActualWidth,panel.ActualHeight));
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),(int)Math.Ceiling(panel.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(visual);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(folder,"69-routing-toolbar.png")))encoder.Save(stream);
            main.UndoPlanningPointCommand.Execute(null);main.UndoPlanningPointCommand.Execute(null);
            await Wait(()=>Task.FromResult(main.RoutePlanningPoints.Count==0),"截图规划清理未完成");
            main.CancelPlanningCommand.Execute(null);await Task.Delay(80);
            checks["退出规划恢复普通光标"]=(await JS("getComputedStyle(document.querySelector('canvas.maplibregl-canvas')).cursor")).GetString()!="crosshair";
        }catch(Exception e){error=e.ToString();}
        finally{bridge.TileFetchForTest=null;}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Timings=timings,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
