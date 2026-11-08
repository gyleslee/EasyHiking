using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Core.Maps;
using HikingGuide.Application.Maps;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Elevation;
using Microsoft.Web.WebView2.Wpf;
using SkiaSharp;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunTerrainRecoverySmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var samples=new List<object>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            async Task Wait(Func<Task<bool>> ready,string message){for(var i=0;i<1200;i++){if(await ready())return;await Task.Delay(50);}throw new InvalidOperationException(message);}
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");window.Width=1800;window.Height=1000;await T10ManualLayoutAsync(window);
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            await JS("window.terrainFaults=[];window.addEventListener('error',e=>{if(window.terrainFaults.length<20)window.terrainFaults.push({message:e.message,stack:e.error?.stack,source:e.filename,line:e.lineno,col:e.colno});});true");
            var source=new MapSourceDefinition{Name="3D恢复验证离线底图",UrlTemplate="http://127.0.0.1:1/{z}/{x}/{y}.png",MaxZoom=19};GlobalSettingsService.Current.MapSources.Add(source);
            var region=new OfflineMapRegion([new(113.23,24.39),new(113.35,24.39),new(113.35,24.51),new(113.23,24.51)]);
            var tiles=OfflineMapCoverage.Enumerate(region,13,18).ToArray();byte[] png;
            using(var bitmap=new SKBitmap(256,256)){using var canvas=new SKCanvas(bitmap);canvas.Clear(new SKColor(211,228,192));using var pen=new SKPaint{Color=new(115,155,90),StrokeWidth=3};for(var i=0;i<8;i++)canvas.DrawLine(0,i*32,256,i*32+50,pen);png=bitmap.Encode(SKEncodedImageFormat.Png,90).ToArray();}
            var path=Path.Combine(OfflineMaps.RootDirectory,"3D恢复验证.pmtiles");Directory.CreateDirectory(OfflineMaps.RootDirectory);
            async IAsyncEnumerable<(long,byte[])> Data(){foreach(var tile in tiles.OrderBy(t=>t.TileId)){yield return(tile.TileId,png);await Task.Yield();}}
            await PmTilesArchive.WriteAsync(path,new(){Name="3D恢复验证",SourceName=source.Name,SourceFingerprint=OfflineMapSourceIdentity.Fingerprint(source),Region=region,MinZoom=13,MaxZoom=18,TileCount=tiles.Length,PlannedTiles=tiles.Length},2,Data());await OfflineMaps.RefreshAsync();
            GlobalSettingsService.Current.MapLayerStack=GlobalSettingsService.Current.MapLayerStack! with{BaseSourceId=source.Id,BaseOpacity=1,Overlay1Visible=false,Overlay2Visible=false};
            var dem=DemCatalog.Databases.First(d=>d.Status==DemDatabaseStatus.Ready&&d.Name.Contains(Environment.GetEnvironmentVariable("HIKINGGUIDE_T189_DEM")??"Fathom",StringComparison.OrdinalIgnoreCase));
            GlobalSettingsService.Current.OfflineTerrain=new(){DemDatabaseId=dem.Id,ContoursVisible=true,HillshadeVisible=true,HillshadeOpacity=.2};GlobalSettingsService.Current.OsmRoads=new(){Enabled=true};
            await JS("window.hikingGuide3DTest.at([113.260419,24.477892],15.45)");GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id,DefaultPitch=59,Exaggeration=1.5};await main.Map.ApplyGlobalMapLayersAsync(true);
            await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect()")).GetProperty("effective").GetBoolean(),"地形未激活");
            for(var i=0;i<80;i++)
            {
                var lon=113.260419+(i%4)*.006;var lat=24.477892+(i%3)*.004;
                await JS(FormattableString.Invariant($"window.hikingGuide3DTest.at([{lon},{lat}],{14.6+i%4*.4});window.hikingGuide3DTest.camera({(i%5==0?75:50+i%3*5)},{(i*47)%360})"));
                await Task.Delay(60);
            }
            await JS("window.hikingGuide3DTest.at([113.260419,24.477892],15.45);window.hikingGuide3DTest.camera(59,222)");
            await Wait(async()=>{var r=await JS("window.hikingGuideOsmRoadTest.inspect()");return r.GetProperty("loaded").GetBoolean()&&r.GetProperty("features").GetInt32()>0;},"停止移动后道路未自动完整加载");
            await Wait(async()=>(await JS("window.hikingGuideRasterTest.inspect().slots[0].loaded")).GetBoolean(),"离线底图未加载");
            var state=await JS("window.hikingGuide3DTest.inspect()");var faults=await JS("window.terrainFaults");var raster=await JS("window.hikingGuideRasterTest.inspect()");var roads=await JS("window.hikingGuideOsmRoadTest.inspect()");
            samples.Add(new{State=state,Faults=faults,Raster=raster,Roads=roads,OfflineHits=OfflineMaps.ReadHits});checks["离线地图读取成功"]=OfflineMaps.ReadHits>0;checks["快速旋转平移缩放不出现空对象异常"]=faults.GetArrayLength()==0;
            checks["无需再次移动即可完成道路显示"]=roads.GetProperty("loaded").GetBoolean()&&roads.GetProperty("features").GetInt32()>0;
            checks["连续操作后3D仍有效"]=state.GetProperty("effective").GetBoolean();
            checks["层级Z15在缩放按钮左侧"]= (await JS("(()=>{const z=document.querySelector('.map-zoom-level'),n=z?.nextElementSibling;return z?.textContent==='Z15'&&z.getBoundingClientRect().right<=n.getBoundingClientRect().left;})()")).GetBoolean();
            await using(var image=File.Create(Path.Combine(folder,"T189-3D连续操作.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,image);
            await JS("window.dispatchEvent(new ErrorEvent('error',{message:\"Cannot read properties of null (reading 'y')\",error:new TypeError(\"Cannot read properties of null (reading 'y')\")}));true");
            await Wait(async()=>{var s=await JS("window.hikingGuide3DTest.inspect()");return !s.GetProperty("state").GetProperty("enabled").GetBoolean()&&s.GetProperty("terrain").ValueKind==JsonValueKind.Null&&s.GetProperty("camera").GetProperty("pitch").GetDouble()==0;},"绘制异常后未自动切回2D");
            await Wait(async()=>(await JS("window.hikingGuideRasterTest.inspect().slots[0].loaded")).GetBoolean(),"恢复2D后离线底图未重新显示");
            var recovered=await JS("window.hikingGuide3DTest.inspect()");var recoveredRaster=await JS("window.hikingGuideRasterTest.inspect()");
            checks["异常后撤下天空并恢复原位置2D"]=!recovered.TryGetProperty("sky",out var sky)||sky.ValueKind==JsonValueKind.Null;
            checks["异常恢复保留离线底图和层级"]=recoveredRaster.GetProperty("slots")[0].GetProperty("sourceId").GetString()==source.Id.ToString()&&Math.Abs(recovered.GetProperty("camera").GetProperty("zoom").GetDouble()-15.45)<.01;
            checks["一次异常只触发一次恢复"]=recovered.GetProperty("renderFaults").GetInt32()==1;
            await Task.Delay(1000);
            await using(var image=File.Create(Path.Combine(folder,"T189-异常后离线2D恢复.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,image);
            samples.Add(new{Recovery=recovered,Raster=recoveredRaster,OfflineHits=OfflineMaps.ReadHits});
            GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D with{Enabled=true};await main.Map.ApplyGlobalMapLayersAsync(true);
            await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect()")).GetProperty("effective").GetBoolean(),"恢复后不能重新启用3D");
            checks["恢复后可再次启用3D"]=true;
            await File.WriteAllTextAsync(Path.Combine(folder,"stress-details.json"),JsonSerializer.Serialize(samples,new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Samples=samples,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}
