using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Diagnostics;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunTerrainPerformanceSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;object? metrics=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            var dem=DemCatalog.Databases.First(d=>d.Status==DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=false,TerrainDemDatabaseId=dem.Id};
            GlobalSettingsService.Current.OfflineTerrain=new(){DemDatabaseId=dem.Id,ContoursVisible=false,HillshadeVisible=false};
            await GlobalSettingsService.SaveAsync();
            for(var i=0;i<250&&!main.Map.IsReady;i++)await Task.Delay(100);
            if(!main.Map.IsReady)throw new InvalidOperationException("地图未就绪");
            await main.Map.ApplyGlobalMapLayersAsync(true);
            var view=FavoritesPanel.Descendants<MapView>(window).Single();var bridge=T10ManualField<WebViewMapBridge>(view,"_bridge");
            var service=T10ManualField<DemTerrainTiles>(bridge,"_terrainTiles");
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            async Task Wait(Func<Task<bool>> ready,string message)
            {for(var i=0;i<900;i++){if(await ready())return;await Task.Delay(50);}throw new InvalidOperationException(message);}
            await JS("window.hikingGuide3DTest.at([113.32,24.42],16)");
            await TerrainTiles!.ClearAsync();await service.ClearCacheAsync(true);await service.ClearCacheAsync(false);
            var beginGrids=service.SampledGrids;var clock=Stopwatch.StartNew();double? terrainMs=null;double? derivedMs=null;var violated=false;
            GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D! with{Enabled=true};
            GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=true,HillshadeVisible=true};
            await main.Map.ApplyGlobalMapLayersAsync(true);
            var heartbeat=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
            var ticks=new List<double>();var last=clock.Elapsed.TotalMilliseconds;
            heartbeat.Tick+=(_,_)=>{var now=clock.Elapsed.TotalMilliseconds;ticks.Add(now-last);last=now;};heartbeat.Start();
            await Wait(async()=>{
                var state=await JS("window.hikingGuide3DTest.inspect()");var active=state.GetProperty("effective").GetBoolean();
                if(active)terrainMs??=clock.Elapsed.TotalMilliseconds;
                if(service.SampledGrids>beginGrids){derivedMs??=clock.Elapsed.TotalMilliseconds;if(!active)violated=true;}
                var layers=(await JS("window.hikingGuideTerrainTest.inspect()")).GetProperty("layers");
                return active&&layers[0].ValueKind!=JsonValueKind.Null&&layers[0].GetProperty("loaded").GetBoolean()&&layers[2].ValueKind!=JsonValueKind.Null&&layers[2].GetProperty("loaded").GetBoolean();
            },"地形和派生效果未完成加载");
            checks["冷缓存先形成3D再计算派生网格"]=terrainMs.HasValue&&derivedMs.HasValue&&!violated&&derivedMs>=terrainMs;
            checks["真实FathomDEM地形有效且阴影等高线加载"]=main.Map.EffectiveTerrainState&&service.SampledGrids>beginGrids;
            await using(var capture=File.Create(Path.Combine(folder,"T183-3D地形优先完成.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
            for(var i=0;i<6;i++){await JS($"window.hikingGuide3DTest.at([{113.32+i*.002:0.000},{24.42:0.00}],16)");await Task.Delay(65);}
            await Wait(async()=>{var s=await JS("window.hikingGuide3DTest.inspect()");return s.GetProperty("effective").GetBoolean()&&TerrainGenerationPriority.Shared.WaitForDerivedAsync().IsCompleted;},"拖动停止后派生任务未恢复");
            heartbeat.Stop();checks["连续移动后地形仍有效并恢复派生计算"]=main.Map.EffectiveTerrainState;
            checks["密集采样没有长时间阻塞WPF界面"]=ticks.Count>0&&ticks.Max()<1000;
            // 缓存命中即使处于地形优先阶段，也应立即返回。
            var offline=T10ManualField<OfflineTerrainSettings>(service,"_settings");
            var effect=JsonSerializer.SerializeToElement(service.Configure(offline));var signature=effect.GetProperty("signature").GetString();
            var x=(int)((113.32+180)/360*(1<<15));var y=(int)((1-Math.Asinh(Math.Tan(24.42*Math.PI/180))/Math.PI)/2*(1<<15));
            var uri=new Uri($"https://hikingguide-terrain.local/{signature}/contours/15/{x}/{y}.png");
            var expected=await service.GetAsync(uri);TerrainGenerationPriority.Shared.SetTerrainReady(false);
            try{var cached=await service.GetAsync(uri).WaitAsync(TimeSpan.FromSeconds(2));checks["优先阶段已缓存等高线仍可立即复用"]=expected.AsEnumerable().SequenceEqual(cached);}
            finally{TerrainGenerationPriority.Shared.SetTerrainReady(true);}
            metrics=new{FirstTerrainMs=terrainMs,FirstDerivedMs=derivedMs,TotalMs=clock.Elapsed.TotalMilliseconds,MaximumUiTickMs=ticks.Max(),service.SampledGrids,TerrainTiles.GeneratedTiles,Status=main.Map.MapStatusText};
            await main.Map.ToggleTerrainAsync();await Task.Delay(300);
            checks["回2D释放等待并保持生成器开关"]=!GlobalSettingsService.Current.Terrain3D!.Enabled&&GlobalSettingsService.Current.OfflineTerrain is{ContoursVisible:true,HillshadeVisible:true};
        }
        catch(Exception exception){error=exception.ToString();}
        var passed=error is null&&checks.Count>0&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Metrics=metrics,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}
