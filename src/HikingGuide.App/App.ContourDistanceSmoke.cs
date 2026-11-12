using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunContourDistanceSmokeAsync(MainWindow owner,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var diagnostics=new List<object>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        var web=FavoritesPanel.Descendants<WebView2>(owner).First();
        async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
        async Task Wait(Func<Task<bool>> test,string reason,int ms=150000){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){if(await test())return;await Task.Delay(100);}throw new TimeoutException(reason);}
        try
        {
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");
            web=FavoritesPanel.Descendants<WebView2>(owner).First(w=>w.Source?.Host=="hikingguide.local");
            var bridge=FavoritesPanel.Descendants<MapView>(owner).First().BridgeForAutomation!;
            using var surface=SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(256,256));surface.Canvas.Clear(new SkiaSharp.SKColor(211,228,192));using var png=surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();
            bridge.TileFetchForTest=(request,token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
            var source=new MapSourceDefinition{Name="等高线距离显示隔离验证",UrlTemplate="https://contour-distance.invalid/{z}/{x}/{y}.png",MaxZoom=20};
            GlobalSettingsService.Current.MapSources.Add(source);GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=source.Id,BaseOpacity=1};
            var dem=DemCatalog.Databases.First(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            GlobalSettingsService.Current.OfflineTerrain=new(){DemDatabaseId=dem.Id,ContoursVisible=true,HillshadeVisible=true,ContourColor="#E27B0C",MajorContourColor="#E27B0C",ContourOpacity=1};
            GlobalSettingsService.Current.OsmRoads.Enabled=false;GlobalSettingsService.Current.Terrain3D=new(){Enabled=false,TerrainDemDatabaseId=dem.Id,DefaultPitch=55,Exaggeration=1.5};
            await JS("window.hikingGuide3DTest.at([113.2613403,24.4816224],14);true");await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(1000);
            GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D with{Enabled=true};
            GlobalSettingsService.Current.TerrainCamera=new(24.4816224,113.2613403,14,111,55);
            await main.Map.ApplyGlobalMapLayersAsync(true);
            await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean(),"真实3D地形未就绪");
            await Task.Delay(2000);
            foreach(var zoom in new[]{13,14,15,16,18})
            {
                await JS($"window.hikingGuide3DTest.at([113.2613403,24.4816224],{zoom});window.hikingGuide3DTest.camera(72,111);true");
                await Wait(async()=>{var s=await JS("window.hikingGuideGpuTest.inspect()");return s.GetProperty("tiles").GetInt32()>0&&s.GetProperty("pending").GetInt32()==0&&s.GetProperty("queued").GetInt32()==0;},"真实DEM显示瓦片未就绪");
                await Wait(async()=>!(await JS("window.hikingGuideLayerContinuityTest.inspect().moving")).GetBoolean(),"相机尚未稳定");await Task.Delay(1600);
                var state=await JS("window.hikingGuideGpuTest.inspect()");var depth=await JS("window.hikingGuideGpuTest.distance()");var labels=await JS("window.hikingGuideGpuTest.labels()");
                checks[$"Z{zoom}配置近中远等高距正确"]=state.GetProperty("distanceIntervals").EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(new[]{zoom<15?100:10,100,200});
                checks[$"Z{zoom}数字预算不超过原三分之一"]=state.GetProperty("labels").GetInt32()<=state.GetProperty("labelLimit").GetInt32()&&state.GetProperty("labelLimit").GetInt32()<=27;
                checks[$"Z{zoom}远景数字只使用200米倍数"]=labels.EnumerateArray().Where(x=>x.GetProperty("properties").GetProperty("band").GetInt32()==2).All(x=>Math.Abs(x.GetProperty("properties").GetProperty("elevation").GetDouble()%200)<.001);
                checks[$"Z{zoom}保留3D且无GPU错误"]=(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean()&&state.GetProperty("errors").GetArrayLength()==0;
                checks[$"Z{zoom}实景分区有效且中间不是固定10米"]=depth.EnumerateArray().All(x=>double.IsFinite(x.GetProperty("relative").GetDouble()));
                checks[$"Z{zoom}近处仍保留细节"]=depth.EnumerateArray().Any(x=>x.GetProperty("interval").GetInt32()==(zoom<15?100:10));
                diagnostics.Add(new{Zoom=zoom,State=state,DepthSamples=depth,Labels=labels});
                using var image=File.Create(Path.Combine(folder,$"3D-Z{zoom}-distance.png"));await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,image);
            }
            await JS("window.hikingGuide3DTest.at([113.269571,24.464423],15);window.hikingGuide3DTest.camera(60,336);true");await Task.Delay(2000);
            checks["山坡定位时船底顶仍处于10米近景"]=(await JS("window.hikingGuideGpuTest.groundPoint(113.2613403,24.4816224).interval")).GetInt32()==10;
            using(var shot=File.Create(Path.Combine(folder,"3D-Z15-summit-ground.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,shot);
            await JS("window.hikingGuide3DTest.at([113.25409313334228,24.48003202387339],15);window.hikingGuide3DTest.camera(53.7794,122.7);true");await Task.Delay(2200);
            checks["用户当前Z15视角船底顶保留细节"]=(await JS("window.hikingGuideGpuTest.groundPoint(113.2613403,24.4816224).interval")).GetInt32()==10;
            using(var shot=File.Create(Path.Combine(folder,"3D-current-Z15.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,shot);
            var compiled=(await JS("window.hikingGuideGpuTest.inspect().compiles")).GetInt32();
            await JS("window.hikingGuideGpuTest.animate({bearing:220,pitch:60,zoom:16});true");
            var continuous=true;
            for(var i=0;i<18;i++){await Task.Delay(60);continuous&=(await JS("window.hikingGuideGpuTest.inspect().active")).GetBoolean()&&(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean();}
            checks["旋转缩放期间保留3D与等高线"]=continuous;
            checks["距离分区变化不重新编译Shader"]=(await JS("window.hikingGuideGpuTest.inspect().compiles")).GetInt32()==compiled;
            await JS("window.hikingGuide3DTest.at([113.269571,24.464423],16);window.hikingGuide3DTest.camera(0,111);true");await Task.Delay(500);
            checks["3D俯视保持近景等高距"]=(await JS("window.hikingGuideGpuTest.distance()")).EnumerateArray().All(x=>x.GetProperty("interval").GetInt32()==10);
            GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D with{Enabled=false};await main.Map.ApplyGlobalMapLayersAsync(true);
            await Wait(async()=>{var s=await JS("window.hikingGuideGpuTest.inspect()");return s.GetProperty("active").GetBoolean()&&s.GetProperty("pending").GetInt32()==0&&s.GetProperty("labels").GetInt32()>0;},"2D等高线与数字未就绪");await Task.Delay(1200);
            var flat=await JS("window.hikingGuideGpuTest.inspect()");diagnostics.Add(new{Flat=flat});checks["2D数字减少且无GPU错误"]=flat.GetProperty("labels").GetInt32()<=16&&flat.GetProperty("labels").GetInt32()>0&&flat.GetProperty("errors").GetArrayLength()==0;
            using(var shot=File.Create(Path.Combine(folder,"2D-Z16-labels.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,shot);
            checks["2D保持原有等高距"]=(await JS("window.hikingGuideGpuTest.distance()")).EnumerateArray().All(x=>x.GetProperty("interval").GetInt32()==10);
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=owner};manager.Show();
            T10ManualField<TabControl>(manager,"LibraryTabs").SelectedIndex=1;await T10ManualLayoutAsync(manager);
            checks["动态等高线标题和缓存名称正确"]=FavoritesPanel.Descendants<TextBlock>(manager).Any(t=>t.Text=="动态等高线（独立叠加，不占用在线图源位置）")&&T10ManualField<Button>(manager,"ClearContoursButton").Content?.ToString()=="清除高程采样缓存";
            checks["已移除红框说明"]=!FavoritesPanel.Descendants<TextBlock>(manager).Any(t=>t.Text.StartsWith("主等高线按所设米数的倍数加粗"));
            await CaptureT10ClientAsync(manager,Path.Combine(folder,"contour-panel.png"));manager.Close();await Wait(()=>Task.FromResult(!manager.IsVisible),"地图管理未关闭");

        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
