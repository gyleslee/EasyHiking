using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunGpuContourSmokeAsync(MainWindow owner,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var diagnostics=new List<object>();string? error=null;
        var folder=Path.GetDirectoryName(output)!;
        var readyDeadline=DateTime.UtcNow.AddSeconds(30);
        while(!FavoritesPanel.Descendants<WebView2>(owner).Any(w=>w.CoreWebView2 is not null&&w.Source?.Host=="hikingguide.local")&&DateTime.UtcNow<readyDeadline)await Task.Delay(50);
        var web=FavoritesPanel.Descendants<WebView2>(owner).First(w=>w.CoreWebView2 is not null&&w.Source?.Host=="hikingguide.local");
        var bridge=FavoritesPanel.Descendants<MapView>(owner).First().BridgeForAutomation!;
        async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
        async Task Wait(Func<Task<bool>> test,string label,int milliseconds=120000){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<milliseconds){if(await test())return;await Task.Delay(100);}throw new TimeoutException(label);}
        async Task<int> Capture(string name)
        {
            using var stream=new MemoryStream();await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream);
            var bytes=stream.ToArray();await File.WriteAllBytesAsync(Path.Combine(folder,name+".png"),bytes);
            using var bitmap=SkiaSharp.SKBitmap.Decode(bytes);var pixels=0;
            for(var y=50;y<bitmap.Height-80;y++)for(var x=50;x<bitmap.Width-150;x++){var p=bitmap.GetPixel(x,y);if(p.Red>180&&p.Green<80&&p.Blue<120)pixels++;}
            return pixels;
        }
        try
        {
            await Wait(()=>Task.FromResult(main.Map.IsReady),"原地图未就绪");
            await JS("window.hikingGuide3DTest.at([113.269571,24.464423],16);true");
            GlobalSettingsService.Current.OfflineTerrain=new(){ContoursVisible=false,HillshadeVisible=false};
            GlobalSettingsService.Current.OsmRoads=new(){Enabled=false};GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            await main.Map.ApplyGlobalMapLayersAsync(true);
            await Wait(async()=>!(await JS("window.hikingGuideStartupTest.inspect().waiting")).GetBoolean(),"首次底图优先初始化尚未完成");await Task.Delay(300);
            async Task Start(object surface,string color="#FF0033")
            {
                var settings=JsonSerializer.Serialize(new{surface,contours=true,color,contourOpacity=1,majorEnabled=true,majorInterval=50});
                await JS("window.gpuProbePromise=window.hikingGuideGpuTest.start("+settings+");true");
                await Wait(async()=>{var s=await JS("window.hikingGuideGpuTest.inspect()");return s.TryGetProperty("active",out var active)&&active.GetBoolean()&&s.GetProperty("tiles").GetInt32()>0&&s.TryGetProperty("draws2D",out var d)&&d.GetInt32()>0&&s.GetProperty("pending").GetInt32()==0&&s.GetProperty("queued").GetInt32()==0;},"GPU纹理或2D绘制未完成");
                await Task.Delay(300);
            }
            byte[] Synthetic(Func<int,int,double?> height)
            {
                var bytes=new byte[516*516*4];for(var y=0;y<516;y++)for(var x=0;x<516;x++)if(height(x,y) is {} h){var rgb=HikingGuide.Core.Elevation.TerrainRgb.Encode(h);var p=(y*516+x)*4;bytes[p]=rgb.R;bytes[p+1]=rgb.G;bytes[p+2]=rgb.B;bytes[p+3]=255;}return bytes;
            }
            async Task SyntheticCase(string name,Func<int,int,double?> height,bool visible)
            {
                var surface=new{surfaceUrlTemplate="data:application/octet-stream;base64,"+Convert.ToBase64String(Synthetic(height)),maxNativeZoom=16};
                await Start(surface);var state=await JS("window.hikingGuideGpuTest.inspect()");var pixels=await Capture(name);
                checks[name]=state.GetProperty("active").GetBoolean()&&state.GetProperty("tiles").GetInt32()>0&&(visible?pixels>100:pixels==0);diagnostics.Add(new{Name=name,RedPixels=pixels,State=state});
                await JS("window.hikingGuideGpuTest.clear()");
            }
            await SyntheticCase("GPU-规则斜坡",(x,y)=>1000+(x-2)*100d/512,true);
            await SyntheticCase("GPU-平地不填色",(x,y)=>1050,false);
            await SyntheticCase("GPU-NoData透明",(x,y)=>null,false);
            await SyntheticCase("GPU-负高程",(x,y)=>-50+(x-2)*100d/512,true);
            var databases=DemCatalog.Databases.Where(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready).Take(2).ToArray();
            if(databases.Length<2)throw new InvalidOperationException("GPU验收需要两套真实DEM。");
            foreach(var dem in databases)
            {
                var settings=new OfflineTerrainSettings{DemDatabaseId=dem.Id,ContoursVisible=true,HillshadeVisible=false,ContourColor="#FF0033"};
                GlobalSettingsService.Current.OfflineTerrain=settings;GlobalSettingsService.Current.Terrain3D=new(){Enabled=false,TerrainDemDatabaseId=dem.Id};await main.Map.ApplyGlobalMapLayersAsync(true);
                await Start(bridge.ConfigureGpuContourProbe(settings));
                var state=await JS("window.hikingGuideGpuTest.inspect()");var pixels=await Capture("GPU-真实DEM-"+dem.Id.ToString("N"));
                checks[dem.Name+"-真实DEM2D显示"]=pixels>100;
                checks["真实WebView2加载固定6.17.0"]=state.GetProperty("version").GetString()=="6.17.0";
                var compiled=state.GetProperty("compiles").GetInt32();
                GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id,DefaultPitch=55,Exaggeration=1.5};
                await main.Map.ApplyGlobalMapLayersAsync(true);
                await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean(),"真实3D地形未就绪");
                await Wait(async()=>(await JS("window.hikingGuideGpuTest.inspect().draws3D")).GetInt32()>0,"GPU未贴合Terrain合成");
                pixels=await Capture("GPU-真实DEM3D-"+dem.Id.ToString("N"));
                checks[dem.Name+"-真实3D合成"]=pixels>100;
                foreach(var exaggeration in new[]{.5,1d,1.5,2,3})
                {
                    GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D with{Exaggeration=exaggeration};await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(300);
                    var s=await JS("window.hikingGuideGpuTest.inspect()");checks[dem.Name+"-夸张"+exaggeration+"不重编译"]=s.GetProperty("compiles").GetInt32()==compiled;
                }
                state=await JS("window.hikingGuideGpuTest.inspect()");checks[dem.Name+"-无GPU请求异常"]=state.GetProperty("errors").GetArrayLength()==0;
                diagnostics.Add(new{DEM=dem.Name,State=state,Generated=TerrainTiles!.SurfaceGeneratedTiles});
                await JS("window.hikingGuideGpuTest.clear()");GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};await main.Map.ApplyGlobalMapLayersAsync(true);
            }
        }catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true}));
        return passed;
    }
}
