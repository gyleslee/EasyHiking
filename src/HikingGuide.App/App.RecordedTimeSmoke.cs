using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Core.Elevation;
using HikingGuide.Infrastructure.TrackFiles;
using HikingGuide.Infrastructure.Projects;
namespace HikingGuide.App;
public partial class App
{
 private async Task<bool> RunRecordedTimeSmokeAsync(MainWindow window,MainViewModel main,string output)
 {
  var checks=new Dictionary<string,bool>();string? error=null;var folder=Path.GetDirectoryName(output)!;
  async Task Wait(Func<bool> test){for(var i=0;i<1800;i++){if(test())return;await Task.Delay(100);}throw new TimeoutException("属性后台分析未完成");}
  try{
   await Wait(()=>main.Map.IsReady);
   var bridge=FavoritesPanel.Descendants<MapView>(window).First().BridgeForAutomation!;
   using var bitmap=new SkiaSharp.SKBitmap(256,256);using(var canvas=new SkiaSharp.SKCanvas(bitmap))canvas.Clear(new SkiaSharp.SKColor(231,238,223));using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var png=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();
   bridge.TileFetchForTest=(_,_)=>Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(bytes)});
   var source=new MapSourceDefinition{Name="时间与高程操作演示底图",UrlTemplate="https://recorded-time.invalid/{z}/{x}/{y}.png"};GlobalSettingsService.Current.MapSources.Add(source);GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=source.Id};

   var input=Environment.GetEnvironmentVariable("HIKINGGUIDE_TIME_SAMPLE")??throw new InvalidOperationException("缺少测试轨迹");var original=File.ReadAllBytes(input);
   var imported=await new KmlTrackImporter().ImportAsync(input);var p=new HikingProject{Name="记录时间与高程示例"};var t=imported.Tracks.Single();t.Name=Path.GetFileNameWithoutExtension(input);t.RecalculateDistances();p.AddTrack(t,false);foreach(var importedMarker in imported.Waypoints){importedMarker.AssociatedTrackId=t.Id;p.Waypoints.Add(importedMarker);}
   var dem=DemCatalog.Databases.First(d=>d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));GlobalSettingsService.Current.Terrain3D=new(){Enabled=false,TerrainDemDatabaseId=dem.Id};
   GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=false,HillshadeVisible=false};GlobalSettingsService.Current.OsmRoads.Enabled=false;
   var file=Path.Combine(folder,"记录时间示例.hikeproj");await new ProjectFileService().SaveAsync(p,file,false);await main.OpenForAutomationAsync(file);p=main.MapContextProject!;t=p.Tracks.Single();
   main.UpdateProjectItemSelection(T10ManualNodes(main.NavigationNodes).Single(n=>ReferenceEquals(n.Model,t)),ModifierKeys.None,focusOnMap:false);
   await Wait(()=>main.SelectedProperties.All(r=>r.Value!="正在计算…"));
   var expected=new[]{"名称","主攻略轨迹","显示颜色","总距离","运动用时","运动均速","全程用时","全程均速","休息时间","累计爬升\n(GPS)","累计爬升\n(DEM)","累计下降\n(GPS)","累计下降\n(DEM)","最高海拔","最低海拔","标注点数","轨迹段数","轨迹点数","说明","原始来源","轨迹ID"};
   checks["属性字段顺序与改名准确且移除当前选中轨迹"]=main.SelectedProperties.Select(r=>r.Name).SequenceEqual(expected);
   checks["真实样本运动全程休息时间精确匹配"]=main.SelectedProperties.Single(r=>r.Name=="运动用时").Value=="12:43:23"&&main.SelectedProperties.Single(r=>r.Name=="全程用时").Value=="34:41:04"&&main.SelectedProperties.Single(r=>r.Name=="休息时间").Value=="21:57:41";
   checks["颜色行提供可点击色块和当前轨迹参数"]=main.SelectedProperties.Single(r=>r.Name=="显示颜色") is{Color:not null,Track:not null};
   checks["地形DEM统计已生成且没有改用GPS"]=main.SelectedProperties.Where(r=>r.Name.Contains("(DEM)")||r.Name is "最高海拔" or "最低海拔").All(r=>r.Value.Contains(" m"));
   await File.WriteAllTextAsync(Path.Combine(folder,"properties.json"),JsonSerializer.Serialize(main.SelectedProperties.Select(r=>new{r.Name,r.Value,r.Color}),new JsonSerializerOptions{WriteIndented=true}));
   await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(700);window.PropertiesPanelColumn.Width=new GridLength(270);await T10ManualLayoutAsync(window);await CaptureT10ClientAsync(window,Path.Combine(folder,"recorded-track-properties.png"));
   checks["GPS及DEM采样均未写回原始轨迹点"]=t.Segments.SelectMany(s=>s.Points).All(x=>x.DemElevation is null);
   checks["项目持久化保留全部轨迹点时间"]=t.Segments.SelectMany(s=>s.Points).All(x=>x.Timestamp.HasValue);
   var cachedAnalysisIds=p.ElevationAnalyses.Select(r=>r.Id).ToArray();
   main.RefreshSelectedTrackProperties();
   await Wait(()=>main.SelectedProperties.All(r=>r.Value!="正在计算…"));
   checks["重复选中复用项目分析结果且不新增统计"]=cachedAnalysisIds.SequenceEqual(p.ElevationAnalyses.Select(r=>r.Id));
   checks["属性统计已保存在项目分析缓存中"]=p.ElevationAnalyses.Any(r=>r.Source.SourceType==ElevationSourceType.Gps)&&p.ElevationAnalyses.Any(r=>r.Source.SourceType==ElevationSourceType.Dem);
   var w=p.Waypoints.First(x=>x.Timestamp.HasValue);main.UpdateProjectItemSelection(T10ManualNodes(main.NavigationNodes).Single(n=>ReferenceEquals(n.Model,w)),ModifierKeys.None,focusOnMap:false);
   checks["标注点时间显示为年月日时分"]=main.SelectedProperties.Single(r=>r.Name=="时间").Value==w.Timestamp!.Value.ToLocalTime().ToString("yyyy/M/d H:mm",System.Globalization.CultureInfo.InvariantCulture);
   window.BottomPanelRow.Height=new GridLength(380);var analysis=window.ElevationAnalysis;window.AnalysisTab.IsSelected=true;await T10ManualLayoutAsync(window);await analysis.RunAutomationGpsAsync(p,t);
   var result=T10ManualField<ElevationAnalysisResult>(analysis,"_leftResult");var point=result.Series[result.Series.Count/2];var text=analysis.BuildRecordedHoverText(result,point,point.DistanceMeters);
   checks["分析悬浮牌显示起终距离与原始记录时间"]=text.Contains("离起点")&&text.Contains("离终点")&&text.Contains("时间 ")&&!text.Contains("无记录");
   await File.WriteAllTextAsync(Path.Combine(folder,"hover.txt"),text);
   typeof(ElevationAnalysisControl).GetMethod("DrawHoverBubble",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(analysis,new object[]{T10ManualField<ScottPlot.WPF.WpfPlot>(analysis,"LeftPlot"),result,T10ManualField<Canvas>(analysis,"LeftHoverOverlay"),point.DistanceMeters});
   await CaptureT10ClientAsync(window,Path.Combine(folder,"recorded-analysis-hover.png"));
   checks["原始导入文件字节未变"]=File.ReadAllBytes(input).SequenceEqual(original);
  }catch(Exception ex){error=ex.ToString();try{await CaptureT10ClientAsync(window,Path.Combine(folder,"failure.png"));}catch{}}
  var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
 }
}


