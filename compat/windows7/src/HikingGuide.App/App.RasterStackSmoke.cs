using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Core.Favorites;
using HikingGuide.Application.Guides;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
using Track = HikingGuide.Core.Models.Track;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunRasterStackSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var states=new List<object>();string? error=null;object? network=null;
        using var listener=new HttpListener();var requests=new List<(string Path,string? Header)>();
        try
        {
            var dir=Path.GetDirectoryName(Path.GetFullPath(output))!;
            if(!dir.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase)||!Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase)||!GlobalSettingsService.SettingsFilePath.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("三层地图验收必须隔离全局设置和收藏数据库。");
            Directory.CreateDirectory(dir);
            var socket=new System.Net.Sockets.LegacyTcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
            byte[] Tile(string kind)
            {
                var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
                {
                    dc.DrawRectangle(kind=="a"?new SolidColorBrush(Color.FromRgb(210,230,240)):Brushes.Transparent,null,new Rect(0,0,256,256));
                    if(kind=="b") for(var i=0;i<256;i+=40)dc.DrawLine(new Pen(Brushes.Red,8),new(i,0),new(i,256));
                    if(kind=="c") for(var i=0;i<256;i+=40)dc.DrawLine(new Pen(Brushes.Green,8),new(0,i),new(256,i));
                }
                var bitmap=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();
            }
            var images=new Dictionary<string,byte[]>{{"a",Tile("a")},{"b",Tile("b")},{"c",Tile("c")}};
            var serve=Task.Run(async()=>{while(listener.IsListening){HttpListenerContext ctx;try{ctx=await listener.GetContextAsync();}catch{break;}var key=ctx.Request.Url!.AbsolutePath.Trim('/').Split('/')[0];lock(requests)requests.Add((ctx.Request.Url.AbsolutePath,ctx.Request.Headers["X-Slot-Test"]));try{var bytes=images.GetValueOrDefault(key,images["a"]);ctx.Response.ContentType="image/png";await ctx.Response.OutputStream.WriteAsync(bytes);ctx.Response.Close();}catch{}}});
            var sources=new[]{"a","b","c"}.Select(k=>new MapSourceDefinition{Name="隔离瓦片"+k,UrlTemplate=$"http://127.0.0.1:{port}/{k}/{{z}}/{{x}}/{{y}}.png",Usage=MapSourceUsage.Both,Attribution=k=="c"?"第三图源署名":"重复署名",Headers=new(){["X-Slot-Test"]=k}}).ToArray();
            sources[0].Usage=MapSourceUsage.Base;sources[2].Usage=MapSourceUsage.Overlay;
            GlobalSettingsService.Current.MapSources.AddRange(sources);
            var stack=new GlobalMapLayerStackSettings{BaseSourceId=sources[0].Id,Overlay1SourceId=sources[1].Id,Overlay1Visible=true,Overlay1Opacity=.6,Overlay2SourceId=sources[2].Id,Overlay2Visible=true,Overlay2Opacity=.3};
            GlobalSettingsService.Current.MapLayerStack=stack;await GlobalSettingsService.SaveAsync();
            var paths=new List<string>();var projects=new List<HikingProject>();var legacy=new List<Guid>();
            for(var n=0;n<3;n++)
            {
                var p=new HikingProject{Name="P10-1隔离项目"+n};var t=new Track{Name="业务轨迹",Segments=[new(){Points=[new(){Latitude=23.08,Longitude=113.28,SourceElevation=100},new(){Latitude=23.13,Longitude=113.33,SourceElevation=200}]}]};t.RecalculateDistances();p.Tracks.Add(t);p.MainGuideTrackId=t.Id;
                var guide=new GuideStructureService();foreach(var point in t.Segments[0].Points){var node=new GuideNode{Name="攻略节点",TrackId=t.Id,OriginalCoordinate=point.Coordinate};guide.SnapNode(node,t);p.GuideNodes.Add(node);}GuideStructureService.ReplaceSegments(p,guide.RebuildSegments(p));
                p.Waypoints.Add(new(){Name="普通标注",Latitude=23.11,Longitude=113.31,AssociatedTrackId=t.Id});var old=Guid.NewGuid();p.Settings.Map.SelectedMapSourceId=old;legacy.Add(old);p.Settings.Map.Center=new(23.1,113.3);p.Settings.Map.Zoom=12;
                HikingGuide.Core.Guides.GuideDataReconciler.Reconcile(p.GuideData,t.Id,p.GuideNodes.Select(v=>v.Id).ToArray());
                p.GuideData.Settings=p.GuideData.Settings with {ElevationSourceKind=HikingGuide.Core.Guides.GuideElevationSourceKind.Gps};
                await new HikingGuide.Infrastructure.Guides.GuideDataCoordinator(DemCatalog,DemProvider!).CalculateAsync(p);
                var path=Path.Combine(dir,$"项目{n}.hikeproj");await new ProjectFileService().SaveAsync(p,path,true);paths.Add(path);
            }
            foreach(var path in paths){await main.OpenForAutomationAsync(path);await main.WaitForMapRefreshForAutomationAsync();projects.Add(main.MapContextProject!);}
            window.Width=1500;window.Height=1000;window.Activate();
            for(var i=0;i<150&&!main.Map.IsReady;i++)await Task.Delay(100);await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(500);
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> Script(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            Task<JsonElement> State()=>Script("window.hikingGuideRasterTest.inspect()");
            async Task Wait(Func<JsonElement,bool> test){for(var i=0;i<120;i++){if(test(await State()))return;await Task.Delay(100);}throw new TimeoutException("真实地图状态未达到预期。");}
            // loaded是随相机和瓦片请求变化的临时状态，不属于配置；只比较三层配置。
            string Stack(JsonElement s)=>JsonSerializer.Serialize(s.GetProperty("slots").EnumerateArray().Select(v=>new{Slot=v.GetProperty("slot").GetString(),Source=v.GetProperty("sourceId").GetString(),Templates=v.GetProperty("templates").GetRawText(),Opacity=v.GetProperty("opacity").GetDouble(),Visible=v.GetProperty("visible").GetString()}));
            string Camera(JsonElement s)=>string.Join(";",new[]{s.GetProperty("center").GetRawText(),s.GetProperty("zoom").GetRawText(),s.GetProperty("bearing").GetRawText(),s.GetProperty("pitch").GetRawText()});
            await Wait(s=>s.GetProperty("slots").EnumerateArray().All(slot=>slot.GetProperty("loaded").GetBoolean()));
            var favorite=new FavoritePoint{Name="三层地图收藏点",Latitude=23.1,Longitude=113.3};await Favorites.SaveAsync(favorite,[]);await main.Map.UpdateFavoritesAsync([favorite],true);await Task.Delay(400);
            await Script("window.hikingGuideRasterTest.camera()");await Task.Delay(700);foreach(var p in projects)p.MarkSaved();
            var initial=await State();states.Add(new{Case="三层初始",State=initial});checks["实际三个Raster Source和Layer存在"]=initial.GetProperty("layers").EnumerateArray().Count(l=>l.GetProperty("type").GetString()=="raster")==3;
            var layers=initial.GetProperty("layers").EnumerateArray().Select(l=>l.GetProperty("id").GetString()).ToArray();var rasters=Array.FindLastIndex(layers,l=>l!.StartsWith("raster-"));checks["全部业务图层高于Raster"]=new[]{"tracks-normal","guide-segments","waypoints","guide-nodes","favorites-symbol-layer","favorites-label-layer"}.All(l=>Array.IndexOf(layers,l)>rasters);
            checks["轨迹节点标注收藏点数据存在"]=initial.GetProperty("tracks").GetInt32()>0&&initial.GetProperty("nodes").GetInt32()==2&&initial.GetProperty("waypoints").GetInt32()==1&&initial.GetProperty("favorites").GetInt32()==1;
            checks["署名去重组合"]=initial.GetProperty("attribution").GetArrayLength()==2;
            await main.Map.CapturePreviewAsync(Path.Combine(dir,"三层真实地图.png"));
            var bitmap=new BitmapImage(new Uri(Path.Combine(dir,"三层真实地图.png")));var converted=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);var pixels=new byte[converted.PixelWidth*converted.PixelHeight*4];converted.CopyPixels(pixels,converted.PixelWidth*4,0);
            checks["实际像素显示底图而非空背景"]=Enumerable.Range(0,pixels.Length/4).Any(i=>pixels[i*4]==240&&pixels[i*4+1]==230&&pixels[i*4+2]==210);
            FavoriteSelection.SelectFavorite(favorite);await Task.Delay(180);
            var star=await Script("window.hikingGuideFavoriteTest.inspect()");checks["三层之上金色收藏点选择有效"]=star.GetProperty("goldIds").EnumerateArray().Any(id=>id.GetString()==favorite.Id.ToString())&&star.GetProperty("goldImage").GetBoolean();await main.Map.CapturePreviewAsync(Path.Combine(dir,"三层金色收藏点.png"));
            // 通过WebView原生输入协议验证真实浏览器事件，不依赖桌面是否解锁；不是直接调用业务回调。
            await Script($"window.hikingGuideFavoriteTest.center('{favorite.Id:D}',16)");await Task.Delay(500);
            var marker=await Script($"window.hikingGuideFavoriteTest.point('{favorite.Id:D}')");var mx=marker.GetProperty("x").GetDouble();var my=marker.GetProperty("y").GetDouble();
            Task Input(string type,double x,double y,string button="none",int buttons=0)=>web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button,buttons,clickCount=type=="mouseMoved"?0:1}));
            await Input("mouseMoved",10,10);await Task.Delay(180);await Input("mouseMoved",mx,my);await Task.Delay(700);
            var hovered=await Script("window.hikingGuideFavoriteTest.inspect()");checks["三层之上原生浏览器Hover显示信息牌"]=hovered.GetProperty("hoverId").GetString()==favorite.Id.ToString()&&hovered.GetProperty("popupCount").GetInt32()==1;
            GlobalSettingsService.Current.MapLayerStack=stack with{Overlay1Opacity=.55};await GlobalSettingsService.SaveAsync();await Task.Delay(150);checks["改变Raster透明度保留收藏Hover"]= (await Script("window.hikingGuideFavoriteTest.inspect()")).GetProperty("popupCount").GetInt32()==1;
            await Input("mousePressed",mx,my,"left",1);await Input("mouseMoved",mx+40,my+30,"left",1);await Task.Delay(180);var preview=await Script("window.hikingGuideFavoriteTest.inspect()");checks["三层之上收藏拖动预览禁止地图平移"]=preview.GetProperty("dragActive").GetBoolean()&&!preview.GetProperty("panEnabled").GetBoolean();
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyDown\",\"key\":\"Escape\",\"code\":\"Escape\",\"windowsVirtualKeyCode\":27}");await Input("mouseReleased",mx+40,my+30,"left");await Task.Delay(200);var cancelled=await Script("window.hikingGuideFavoriteTest.inspect()");checks["Esc取消拖动恢复且未写收藏坐标"]=!cancelled.GetProperty("dragActive").GetBoolean()&&cancelled.GetProperty("panEnabled").GetBoolean()&&(await Favorites.GetAsync(favorite.Id))!.Point.Latitude==favorite.Latitude;
            GlobalSettingsService.Current.MapLayerStack=stack;await GlobalSettingsService.SaveAsync();await Script("window.hikingGuideRasterTest.camera()");await Task.Delay(800);foreach(var p in projects)p.MarkSaved();
            var camera=Camera(initial);var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();await Task.Delay(200);CaptureWindowPng(manager,Path.Combine(dir,"地图管理器.png"));
            checks["管理器下拉显隐滑块存在"]=manager.BaseBox.Items.Count>=3&&manager.Overlay1Box.Items.Count>=4&&manager.Overlay2Box.Items.Count>=4&&manager.Overlay1Visible.IsChecked==true;
            checks["管理器按Usage过滤"]=!manager.BaseBox.Items.OfType<MapSourceDefinition>().Any(v=>v.Id==sources[2].Id)&&!manager.Overlay1Box.Items.OfType<MapSourceDefinition>().Any(v=>v.Id==sources[0].Id);
            await Script("(window.rasterOld=window.hikingGuideRasterTest.sourceInstance('overlay1'),true)");
            foreach(var percent in new[]{100d,60d,30d}){manager.Overlay1Opacity.Value=percent;await Task.Delay(500);var s=await State();checks["透明度实际生效:"+percent]=Math.Abs(s.GetProperty("slots")[1].GetProperty("opacity").GetDouble()-percent/100)<1e-6&&Camera(s)==camera;}
            checks["仅改透明度未重建RasterSource"]=(await Script("window.rasterOld===window.hikingGuideRasterTest.sourceInstance('overlay1')")).GetBoolean();
            var humanId=HikingGuide.Infrastructure.Maps.GlobalSettingsService.HumanitarianSourceId;
            manager.BaseBox.SelectedValue=humanId;await Task.Delay(500);checks["管理器切底图相机四项保持"]=Camera(await State())==camera&&main.Map.SelectedMapSource?.Id==humanId;
            manager.BaseBox.SelectedValue=sources[0].Id;await Task.Delay(150);
            foreach(var check in new[]{manager.Overlay1Visible,manager.Overlay2Visible}){check.IsChecked=false;check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Task.Delay(250);var s=await State();var index=check==manager.Overlay1Visible?1:2;checks["隐藏叠加层:"+index]=s.GetProperty("slots")[index].GetProperty("visible").GetString()=="none"&&Camera(s)==camera;if(index==2)checks["隐藏第三图源同步移除其署名"]=s.GetProperty("attribution").GetArrayLength()==1;check.IsChecked=true;check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Task.Delay(250);}
            manager.Close();await GlobalSettingsService.SaveAsync();await Task.Delay(100);
            checks["全局地图修改不使项目Dirty"]=projects.All(p=>!p.IsDirty);
            var baseline=Stack(await State());var allStable=true;
            for(var cycle=0;cycle<5;cycle++)foreach(var path in paths.Concat([paths[0]])){await main.OpenForAutomationAsync(path);await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(80);allStable&=Stack(await State())==baseline;}
            checks["三项目切换五轮图层始终不变"]=allStable;checks["旧图源字段原样保留"]=projects.Select((p,i)=>p.Settings.Map.SelectedMapSourceId==legacy[i]).All(v=>v);checks["旧项目打开及切换不Dirty"]=projects.All(p=>!p.IsDirty);
            var staleBefore=Stack(await State());await Script("window.chrome.webview.dispatchEvent(new MessageEvent('message',{data:{protocolVersion:1,type:'SetMapLayerStack',payload:{settingsRevision:-1,base:null,overlay1:null,overlay2:null}}}))");await Task.Delay(120);checks["旧地图设置版本不会覆盖新配置"]=Stack(await State())==staleBefore;
            main.Map.ReloadCommand.Execute(null);await Task.Delay(2500);for(var i=0;i<150&&!main.Map.IsReady;i++)await Task.Delay(100);await Wait(s=>s.GetProperty("slots").EnumerateArray().All(slot=>slot.GetProperty("loaded").GetBoolean()));
            var reloaded=await State();states.Add(new{Case="WebView重新加载",State=reloaded});checks["WebView重新加载恢复三层及业务图层"]=Stack(reloaded)==baseline&&reloaded.GetProperty("tracks").GetInt32()>0;
            await Script("window.hikingGuideRasterTest.failOverlay()");await Task.Delay(120);checks["叠加层错误独立且业务保留"]=(await State()).GetProperty("tracks").GetInt32()>0&&main.Map.LastTileErrorMessage?.Contains("叠加层1")==true;
            for(var i=0;i<3;i++){if(window.GuideWorkbench.CalculationTask is {} calculating)await calculating;foreach(var p in projects)p.MarkSaved();await main.CloseForAutomationAsync();await main.WaitForMapRefreshForAutomationAsync();}await Task.Delay(200);
            var none=await State();states.Add(new{Case="无项目",State=none});checks["关闭全部项目保持三层及收藏点"]=main.MapContextProject is null&&Stack(none)==baseline&&none.GetProperty("favorites").GetInt32()==1&&none.GetProperty("tracks").GetInt32()==0;
            var z=(await State()).GetProperty("zoom").GetDouble();main.Map.ZoomInCommand.Execute(null);await Task.Delay(500);var z1=(await State()).GetProperty("zoom").GetDouble();main.Map.ZoomInCommand.Execute(null);await Task.Delay(500);checks["无项目连续缩放正常"]=Math.Abs(z1-z-1)<.01&&Math.Abs((await State()).GetProperty("zoom").GetDouble()-z1-1)<.01;
            var saved=GlobalSettingsService.Current.MapLayerStack;var repo=new GlobalSettingsService(new ApplicationDataPaths(Path.GetDirectoryName(GlobalSettingsService.SettingsFilePath)),LoggingService,MapSourceTemplateService);await repo.LoadAsync();checks["重开SettingsRepository完整恢复"]=repo.Current.MapLayerStack==saved;
            sources[1].Enabled=false;await GlobalSettingsService.SaveAsync();await Task.Delay(300);var disabled=await State();checks["禁用当前叠加图源自动清空且其它层保留"]=GlobalSettingsService.Current.MapLayerStack!.Overlay1SourceId is null&&disabled.GetProperty("slots")[0].GetProperty("sourceId").GetString()==sources[0].Id.ToString()&&disabled.GetProperty("slots")[2].GetProperty("sourceId").GetString()==sources[2].Id.ToString();
            sources[1].Enabled=true;GlobalSettingsService.Current.MapLayerStack=saved;await GlobalSettingsService.SaveAsync();
            GlobalSettingsService.Current.MapSources.Remove(sources[0]);await GlobalSettingsService.SaveAsync();await Task.Delay(300);checks["删除当前底图回退Germany并同步工具栏"]=GlobalSettingsService.Current.MapLayerStack!.BaseSourceId==GlobalMapLayerStackSettings.GermanyId&&main.Map.SelectedMapSource?.Id==GlobalMapLayerStackSettings.GermanyId;
            GlobalSettingsService.Current.MapSources.Add(sources[0]);GlobalSettingsService.Current.MapLayerStack=saved;await GlobalSettingsService.SaveAsync();
            var actual=GlobalSettingsService.Current.MapSources.First(s=>s.Id==HikingGuide.Infrastructure.Maps.GlobalSettingsService.HumanitarianSourceId);
            var result=await MapSourceTestService.TestAsync(actual);network=result;
            await Script("window.hikingGuideRasterTest.camera(2)");GlobalSettingsService.Current.MapLayerStack=saved! with{BaseSourceId=actual.Id};await GlobalSettingsService.SaveAsync();await Task.Delay(2500);var human=await State();states.Add(new{Case="Humanitarian底图",State=human});checks["Humanitarian实际三服务器模板进入同一Source"]=human.GetProperty("slots")[0].GetProperty("templates").GetArrayLength()==3;
            await main.Map.CapturePreviewAsync(Path.Combine(dir,"Humanitarian底图.png"));
            GlobalSettingsService.Current.MapLayerStack=saved! with{BaseSourceId=HikingGuide.Infrastructure.Maps.GlobalSettingsService.OsmGermanySourceId,Overlay1SourceId=actual.Id,Overlay1Visible=true,Overlay1Opacity=.5};await GlobalSettingsService.SaveAsync();await Task.Delay(2500);var overlay=await State();states.Add(new{Case="Humanitarian50%叠加层",State=overlay});checks["Humanitarian可作50%叠加层"]=overlay.GetProperty("slots")[1].GetProperty("templates").GetArrayLength()==3&&overlay.GetProperty("slots")[1].GetProperty("opacity").GetDouble()==.5;
            await main.Map.CapturePreviewAsync(Path.Combine(dir,"Humanitarian叠加层.png"));
            GlobalSettingsService.Current.MapLayerStack=saved! with{BaseSourceId=actual.Id,Overlay1SourceId=HikingGuide.Infrastructure.Maps.GlobalSettingsService.OsmGermanySourceId,Overlay1Visible=true,Overlay1Opacity=.45,Overlay2SourceId=HikingGuide.Infrastructure.Maps.GlobalSettingsService.OpenStreetMapSourceId,Overlay2Visible=true,Overlay2Opacity=.7};await GlobalSettingsService.SaveAsync();
            lock(requests)checks["各槽位网络请求头正确隔离"]=new[]{"a","b","c"}.All(k=>requests.Any(r=>r.Path.StartsWith("/"+k+"/")&&r.Header==k))&&requests.All(r=>r.Header==r.Path.Trim('/').Split('/')[0]);
            checks["图层配置三Source实际服务请求完成"]=requests.Count>0;
            await File.WriteAllTextAsync(Path.Combine(dir,"expected-stack.json"),JsonSerializer.Serialize(GlobalSettingsService.Current.MapLayerStack));
        }
        catch(Exception ex){error=ex.ToString();}
        finally{listener.Stop();}
        var success=error is null&&checks.Count>0&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Success=success,Checks=checks,States=states,Network=network,Error=error},new JsonSerializerOptions{WriteIndented=true}));return success;
    }
    private async Task<bool> RunRasterStackReopenAsync(MainWindow window,MainViewModel main,string output,string expectedPath)
    {
        var checks=new Dictionary<string,bool>();string? error=null;JsonElement? state=null;
        try
        {
            if(!Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase)||!GlobalSettingsService.SettingsFilePath.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("必须隔离设置与收藏数据库。");
            var expected=JsonSerializer.Deserialize<GlobalMapLayerStackSettings>(await File.ReadAllTextAsync(expectedPath));checks["新进程设置完整恢复"]=expected==GlobalSettingsService.Current.MapLayerStack;
            for(var i=0;i<150&&!main.Map.IsReady;i++)await Task.Delay(100);await main.Map.ApplyGlobalMapLayersAsync(true);await main.Map.UpdateFavoritesAsync(await Favorites.SearchAsync(),true);await Task.Delay(1200);
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");state=JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync("window.hikingGuideRasterTest.inspect()"));var slots=state.Value.GetProperty("slots");
            checks["新进程无项目仍显示三层"]=main.MapContextProject is null&&slots.GetArrayLength()==3&&slots[0].GetProperty("sourceId").GetString()==expected!.BaseSourceId.ToString()&&slots[1].GetProperty("sourceId").GetString()==expected.Overlay1SourceId.ToString()&&slots[2].GetProperty("sourceId").GetString()==expected.Overlay2SourceId.ToString();
            checks["新进程恢复显隐透明度"]=Math.Abs(slots[1].GetProperty("opacity").GetDouble()-.45)<1e-6&&Math.Abs(slots[2].GetProperty("opacity").GetDouble()-.7)<1e-6&&slots.EnumerateArray().All(s=>s.GetProperty("visible").GetString()=="visible");
            checks["新进程收藏点数据库保留"]=state.Value.GetProperty("favorites").GetInt32()==1;await main.Map.CapturePreviewAsync(Path.ChangeExtension(output,".png"));
        }
        catch(Exception e){error=e.ToString();}
        var success=error is null&&checks.Count>0&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Success=success,Checks=checks,State=state,Error=error},new JsonSerializerOptions{WriteIndented=true}));return success;
    }
}
