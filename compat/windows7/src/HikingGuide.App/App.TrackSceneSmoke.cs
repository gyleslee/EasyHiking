using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Guides;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
using HikingGuide.Core.Maps;
using HikingGuide.Infrastructure.Maps;
using SkiaSharp;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunTrackSceneSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var samples=new List<JsonElement>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            async Task Wait(Func<Task<bool>> test,string reason){for(int i=0;i<600;i++){if(await test())return;await Task.Delay(100);}throw new InvalidOperationException(reason);}
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");
            var original=Path.GetFullPath(Environment.GetEnvironmentVariable("HIKINGGUIDE_TRACK_SCENE_PROJECT")??throw new InvalidOperationException("未指定隔离项目副本"));
            if(!original.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("显示验收仅允许读取临时项目副本");
            await File.WriteAllTextAsync(Path.Combine(folder,"verified-data-paths.json"),JsonSerializer.Serialize(new{
                Settings=GlobalSettingsService.SettingsFilePath,Favorites=Favorites.Paths.FavoritesRoot,
                Data=new HikingGuide.Infrastructure.Projects.ApplicationDataPaths().RootDirectory,
                Functions=Environment.GetEnvironmentVariable("HIKINGGUIDE_FUNCTION_DATA_ROOT"),SourceProject=original}));
            var hash=SHA256.HashData(await File.ReadAllBytesAsync(original));
            Track track;
            using(var zip=ZipFile.OpenRead(original))
            {
                using var meta=JsonDocument.Parse(zip.GetEntry("project.json")!.Open());var id=meta.RootElement.GetProperty("mainGuideTrackId").GetGuid();
                using var stream=zip.GetEntry($"tracks/{id:D}.json")!.Open();var opt=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};opt.Converters.Add(new JsonStringEnumConverter());track=JsonSerializer.Deserialize<Track>(stream,opt)!;
            }
            var project=new HikingProject{Name="轨迹分级与同源贴线验证"};project.AddTrack(track);project.MainGuideTrackId=track.Id;
            var service=new GuideStructureService();var points=track.Segments.SelectMany(p=>p.Points).ToArray();
            foreach(var i in new[]{0,points.Length/3,points.Length*2/3,points.Length-1}){var node=new GuideNode{Name="验证节点",TrackId=track.Id,OriginalCoordinate=points[i].Coordinate};service.SnapNode(node,track);project.GuideNodes.Add(node);}
            foreach(var g in service.RebuildSegments(project))project.GuideSegments.Add(g);
            var centerPoint=points[points.Length/2].Coordinate;
            foreach(var(color,offset) in new[]{("#b9c548",.001),("#24578f",-.001)}){
                var sample=new Track{Name=color=="#b9c548"?"浅色轨迹验证":"深色轨迹验证",DisplayColor=color,
                    Segments=[new(){Points=Enumerable.Range(0,101).Select(i=>new TrackPoint{Longitude=centerPoint.Longitude-.003+i*.00006,Latitude=centerPoint.Latitude+offset+Math.Sin(i*.12)*.00012}).ToList()}]};
                sample.RecalculateDistances();project.AddTrack(sample);
            }
            var path=Path.Combine(folder,"隔离显示测试.hikeproj");await new ProjectFileService().SaveAsync(project,path,false);
            await main.OpenForAutomationAsync(path);await main.WaitForMapRefreshForAutomationAsync();
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            var center=points[points.Length/2].Coordinate;var centerJson=JsonSerializer.Serialize(new[]{center.Longitude,center.Latitude});
            // 自建临时PMTiles底图，排除在线图源网络波动；实际地形仍读取已挂载DEM。
            var source=new MapSourceDefinition{Name="隔离验证离线示意底图",UrlTemplate="http://127.0.0.1:1/{z}/{x}/{y}.png",MaxZoom=19};
            GlobalSettingsService.Current.MapSources.Add(source);
            var region=new OfflineMapRegion([new(center.Longitude-.07,center.Latitude-.07),new(center.Longitude+.07,center.Latitude-.07),new(center.Longitude+.07,center.Latitude+.07),new(center.Longitude-.07,center.Latitude+.07)]);
            var tiles=OfflineMapCoverage.Enumerate(region,13,18).ToArray();byte[] png;
            using(var bitmap=new SKBitmap(256,256)){using var canvas=new SKCanvas(bitmap);canvas.Clear(new SKColor(211,228,192));using var pen=new SKPaint{Color=new(115,155,90),StrokeWidth=2};for(var i=0;i<8;i++)canvas.DrawLine(0,i*32,256,i*32+50,pen);png=bitmap.Encode(SKEncodedImageFormat.Png,90).ToArray();}
            Directory.CreateDirectory(OfflineMaps.RootDirectory);
            async IAsyncEnumerable<(long,byte[])> Data(){foreach(var tile in tiles.OrderBy(t=>t.TileId)){yield return(tile.TileId,png);await Task.Yield();}}
            await PmTilesArchive.WriteAsync(Path.Combine(OfflineMaps.RootDirectory,"显示验证.pmtiles"),new(){Name="显示验证",SourceName=source.Name,SourceFingerprint=OfflineMapSourceIdentity.Fingerprint(source),Region=region,MinZoom=13,MaxZoom=18,TileCount=tiles.Length,PlannedTiles=tiles.Length},2,Data());await OfflineMaps.RefreshAsync();
            GlobalSettingsService.Current.MapLayerStack=GlobalSettingsService.Current.MapLayerStack! with{BaseSourceId=source.Id,BaseOpacity=1,Overlay1Visible=false,Overlay2Visible=false};
            GlobalSettingsService.Current.OfflineTerrain=new(){ContoursVisible=false,HillshadeVisible=false};GlobalSettingsService.Current.OsmRoads.Enabled=false;
            await main.Map.ApplyGlobalMapLayersAsync(true);await main.Map.FocusObjectAsync(main.MapContextProject!.Tracks[0]);
            foreach(var z in new[]{14d,14.99,15,16,19})
            {
                await JS($"window.hikingGuideTrackSceneTest.camera({z.ToString(System.Globalization.CultureInfo.InvariantCulture)},0,0,{centerJson})");
                await Wait(async()=>{var v=await JS("window.hikingGuideTrackSceneTest.inspect()");return v.GetProperty("visibleChunks").GetInt32()>0&& (z>=15?v.GetProperty("vertices").GetInt32()>=points.Length:v.GetProperty("vertices").GetInt32()<points.Length);},"层级显示精度未切换");
                var state=await JS("window.hikingGuideTrackSceneTest.inspect()");samples.Add(state);
                checks[$"Z{z}显示精度符合阈值"]=z>=15?state.GetProperty("vertices").GetInt32()>=points.Length:state.GetProperty("vertices").GetInt32()<points.Length;
                checks["轨迹攻略高亮箭头同源"]=state.GetProperty("sources").EnumerateArray().All(v=>v.GetString()=="tracks");
                checks["引擎二次抽稀关闭"]=state.GetProperty("tolerance").GetDouble()==0;
                checks["双尖角为贴线图案且四种颜色尺寸资源齐全"]=state.GetProperty("arrowType").GetString()=="line"&&state.GetProperty("arrowImage").GetBoolean();
                checks["亮暗轨迹分别选择黑白双尖角"]=state.GetProperty("arrowTones").EnumerateArray().Any(v=>v.GetString()=="dark")&&state.GetProperty("arrowTones").EnumerateArray().Any(v=>v.GetString()=="light");
                if(z==15){await Wait(async()=>(await JS("window.hikingGuideRasterTest.inspect().slots[0].loaded")).GetBoolean(),"临时离线底图未加载");await T10ManualLayoutAsync(window);await Task.Delay(500);await CaptureT10ClientAsync(window,Path.Combine(folder,"Z15完整精度同源轨迹.png"));}
            }
            var dem=DemCatalog.Databases.FirstOrDefault(d=>d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase))??DemCatalog.Databases.FirstOrDefault();
            if(dem is null)throw new InvalidOperationException("缺少3D真实DEM验证数据");
            GlobalSettingsService.Current.TerrainCamera=new(center.Latitude,center.Longitude,16,46,55);
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id,Exaggeration=1.5};await main.Map.ApplyGlobalMapLayersAsync(true);
            foreach(var z in new[]{15d,16d,17d})
            {
                await JS($"window.hikingGuideTrackSceneTest.camera({z},46,55,{centerJson})");
                await Wait(async()=>{var v=await JS("window.hikingGuideTrackSceneTest.inspect()");var t=await JS("window.hikingGuide3DTest.inspect()");return v.GetProperty("terrain").GetBoolean()&&t.GetProperty("effective").GetBoolean()&&t.GetProperty("elevation").ValueKind==JsonValueKind.Number&&v.GetProperty("visibleChunks").GetInt32()>0&&v.GetProperty("vertices").GetInt32()>=points.Length;},"3D真实地形及同源精度未就绪");
                samples.Add(await JS("window.hikingGuideTrackSceneTest.inspect()"));checks[$"3D_Z{z}统一精度且保持3D"]=true;
            }
            await Wait(async()=>(await JS("window.hikingGuideRasterTest.inspect().slots[0].loaded")).GetBoolean(),"3D离线底图未加载");
            samples.Add(await JS("window.hikingGuide3DTest.inspect()"));checks["离线示意底图已读取"]=OfflineMaps.ReadHits>0;
            await Task.Delay(2000);await CaptureT10ClientAsync(window,Path.Combine(folder,"3D同源轨迹与内嵌方向箭头.png"));
            var after=await File.ReadAllBytesAsync(original);checks["原始项目未改变"]=hash.SequenceEqual(SHA256.HashData(after));
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Samples=samples,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
