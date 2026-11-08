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
    private async Task<bool> RunRoadStationarySmokeAsync(MainWindow window,MainViewModel main,string output)
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
            GlobalSettingsService.Current.OfflineTerrain=new(){DemDatabaseId=dem.Id,ContoursVisible=false,HillshadeVisible=false,HillshadeOpacity=.2};GlobalSettingsService.Current.OsmRoads=new(){Enabled=true};
            await JS("window.hikingGuide3DTest.at([113.260419,24.477892],15.45)");GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id,DefaultPitch=59,Exaggeration=1.5};await main.Map.ApplyGlobalMapLayersAsync(true);
            await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect()")).GetProperty("effective").GetBoolean(),"地形未激活");
            async Task<SKBitmap> Capture(string name){using var ms=new MemoryStream();await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,ms);var bytes=ms.ToArray();await File.WriteAllBytesAsync(Path.Combine(folder,name+".png"),bytes);return SKBitmap.Decode(bytes);}
            static int Missing(SKBitmap before,SKBitmap after){var count=0;for(var y=30;y<after.Height-30;y++)for(var x=30;x<after.Width-140;x++){var p=after.GetPixel(x,y);if(p.Red>45||p.Green>45||p.Blue>45)continue;var found=false;for(var dy=-3;dy<=3&&!found;dy++)for(var dx=-3;dx<=3;dx++){var b=before.GetPixel(x+dx,y+dy);if(b.Red<65&&b.Green<65&&b.Blue<65){found=true;break;}}if(!found)count++;}return count;}
            for(var i=0;i<5;i++)
            {
                var lon=113.269571+(i%3-1)*.008;var lat=24.464423+(i/3)*.008;
                await JS(FormattableString.Invariant($"window.hikingGuide3DTest.at([{lon},{lat}],15.495014);window.hikingGuide3DTest.camera(42.029412,287.2)"));
                await Wait(async()=>(await JS("window.hikingGuideOsmRoadTest.inspect().loaded")).GetBoolean(),"道路未就绪");await Task.Delay(1600);
                using var a=await Capture($"T191-{i}-A静止");var sa=await JS("window.hikingGuideStationaryTest.inspect()");checks[$"场景{i}匹配的道路均已生成绘制几何"]=sa.GetProperty("coverage").EnumerateArray().All(r=>r.GetProperty("bucket").GetBoolean());
                await JS("window.hikingGuideStationaryTest.redraw()");await Task.Delay(700);using var b=await Capture($"T191-{i}-B只刷新贴图");
                await JS("window.hikingGuideStationaryTest.cover()");await Wait(async()=>(await JS("window.hikingGuideOsmRoadTest.inspect().loaded")).GetBoolean(),"重新计算覆盖未完成");await Task.Delay(700);using var c=await Capture($"T191-{i}-C只更新覆盖");
                await JS("window.hikingGuideStationaryTest.nudge()");await Task.Delay(150);await JS("window.hikingGuideStationaryTest.restore()");await Wait(async()=>(await JS("window.hikingGuideOsmRoadTest.inspect().loaded")).GetBoolean(),"拖动对照未就绪");await Task.Delay(1300);using var d=await Capture($"T191-{i}-D拖动返回");
                var ab=Missing(a,b);var ac=Missing(a,c);var ad=Missing(a,d);samples.Add(new{Scene=i,MissingAfterRedraw=ab,MissingAfterCover=ac,MissingAfterNudge=ad,Before=sa,After=await JS("window.hikingGuideStationaryTest.inspect()")});checks[$"场景{i}不需拖动补全道路"]=ad<80;
            }
            checks["没有脚本异常"]=(await JS("window.terrainFaults.length")).GetInt32()==0;checks["始终保持3D"]=(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean();
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Samples=samples,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}