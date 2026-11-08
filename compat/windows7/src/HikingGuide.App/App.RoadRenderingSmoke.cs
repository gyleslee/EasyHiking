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
    private async Task<bool> RunRoadRenderingSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var samples=new List<object>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            async Task Wait(Func<Task<bool>> ready,string message){for(var i=0;i<1200;i++){if(await ready())return;await Task.Delay(50);}throw new InvalidOperationException(message);}
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");window.Width=1800;window.Height=1000;await T10ManualLayoutAsync(window);
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            await JS("window.terrainFaults=[];window.addEventListener('error',e=>{if(window.terrainFaults.length<20)window.terrainFaults.push({message:e.message,stack:e.error?.stack,source:e.filename,line:e.lineno,col:e.colno});});true");
            var source=new MapSourceDefinition{Name="3D恢复验证离线底图",UrlTemplate="http://127.0.0.1:1/{z}/{x}/{y}.png",MaxZoom=18};GlobalSettingsService.Current.MapSources.Add(source);
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
            await Wait(async()=>(await JS("window.hikingGuideOsmRoadTest.inspect().loaded")).GetBoolean(),"初始道路未加载");
            var warm=await JS("window.hikingGuideOsmRoadTest.warmDashes()");samples.Add(new{Warm=warm});checks["完整主题各层级虚线纹理不溢出"]=warm.GetProperty("missing").GetInt32()==0;
            for(var i=0;i<100;i++)
            {
                await JS(FormattableString.Invariant($"window.hikingGuide3DTest.at([{113.260419+i%4*.003},{24.477892+i%3*.003}],{15.2+i%8});window.hikingGuide3DTest.camera({40+i%4*10},{i*47%360})"));await Task.Delay(90);
            }
            await JS("window.hikingGuide3DTest.at([113.260419,24.477892],16);window.hikingGuide3DTest.camera(59,222)");
            await Wait(async()=>{var r=await JS("window.hikingGuideOsmRoadTest.inspect()");return r.GetProperty("loaded").GetBoolean()&&r.GetProperty("features").GetInt32()>0;},"道路停止移动后未加载");
            var state=await JS("window.hikingGuide3DTest.inspect()");var faults=await JS("window.terrainFaults");samples.Add(new{State=state,Faults=faults,Roads=await JS("window.hikingGuideOsmRoadTest.inspect()")});
            checks["100次高层级旋转缩放无绘制异常"]=faults.GetArrayLength()==0;checks["3D已在视野之外预生成路网"]=(await JS("window.hikingGuideOsmRoadTest.inspect().prefetched")).GetInt32()>0;checks["连续操作始终保留3D"]=state.GetProperty("effective").GetBoolean()&&state.GetProperty("state").GetProperty("enabled").GetBoolean();
            await Task.Delay(1500);await using(var image=File.Create(Path.Combine(folder,"T190-3D道路.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,image);
            GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D with{Enabled=false};GlobalSettingsService.Current.OfflineTerrain=new(){ContoursVisible=false,HillshadeVisible=false};GlobalSettingsService.Current.MapLayerStack=GlobalSettingsService.Current.MapLayerStack! with{BaseOpacity=0};
            var settings=new OsmRoadSettings{Enabled=true,Opacity=1};foreach(var s in settings.Styles.Where(s=>s.Id!="highway-path")){s.MinZoom=0;s.MaxZoom=0;}GlobalSettingsService.Current.OsmRoads=settings;await main.Map.ApplyGlobalMapLayersAsync(true);
            await JS("window.hikingGuide3DTest.at([113.32,24.42],15);window.hikingGuide3DTest.camera(0,0)");
            await Wait(async()=>(await JS("window.hikingGuideOsmRoadTest.sample('path')")).ValueKind==JsonValueKind.Array,"未找到真实小径");var coordinate=(await JS("window.hikingGuideOsmRoadTest.sample('path')")).GetRawText();

            for(var z=17;z<=22;z++)
            {
                await JS("window.hikingGuide3DTest.at("+coordinate+","+z+")");await Wait(async()=>(await JS("window.hikingGuideOsmRoadTest.inspect().loaded")).GetBoolean(),"小径未加载");await Task.Delay(650);
                using var memory=new MemoryStream();await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,memory);var bytes=memory.ToArray();await File.WriteAllBytesAsync(Path.Combine(folder,$"T190-小径Z{z}.png"),bytes);
                using var bitmap=SKBitmap.Decode(bytes);var dark=0;var light=0;for(var y=bitmap.Height/2-60;y<bitmap.Height/2+60;y++)for(var x=bitmap.Width/2-60;x<bitmap.Width/2+60;x++){var c=bitmap.GetPixel(x,y);if(c.Red<20&&c.Green<20&&c.Blue<20)dark++;if(c.Red>245&&c.Green>245&&c.Blue>245)light++;}
                samples.Add(new{Zoom=z,Dark=dark,Light=light,Coordinate=coordinate,Atlas=await JS("window.hikingGuideOsmRoadTest.inspect().atlas")});checks[$"Z{z}小径有黑色线芯和白色衬底"]=dark>30&&light>5;
            }
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Samples=samples,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}