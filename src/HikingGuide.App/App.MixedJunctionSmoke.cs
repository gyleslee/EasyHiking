using System.IO;
using System.Text.Json;
using System.Windows;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Routing;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.Routing;
using HikingGuide.Infrastructure.TrackFiles;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunMixedJunctionSmokeAsync(MainWindow window, MainViewModel main, string output)
    {
        var checks=new Dictionary<string,bool>(); var diagnostics=new List<object>(); string? error=null;
        var folder=Path.GetDirectoryName(output)!;
        var runtime=Environment.GetEnvironmentVariable("HIKINGGUIDE_BROUTER_RUNTIME_DIR")!;
        await using var process=new BRouterProcessService(Path.Combine(runtime,"java","bin","java.exe"),
            Path.Combine(runtime,"brouter-1.7.10-all.jar"),Environment.GetEnvironmentVariable("HIKINGGUIDE_OSM_ROAD_DATA_DIR")!,Path.Combine(runtime,"profiles2"));
        async Task Wait(Func<bool> condition,string reason){for(var i=0;i<1500;i++){if(condition())return;await Task.Delay(100);}throw new TimeoutException(reason);}
        try
        {
            var file=Environment.GetEnvironmentVariable("HIKINGGUIDE_TRACK_SCENE_PROJECT")!;
            await main.OpenForAutomationAsync(file);await Wait(()=>main.Map.IsReady,"地图未就绪");await main.WaitForMapRefreshForAutomationAsync();
            main.ConfigureRouting(new BRouterProvider(process,()=>"current-rd5-validation"));
            var project=main.MapContextProject!;
            checks["验证项目无人工节点可安全生成独立副本"]=project.GuideNodes.Count==0&&project.Waypoints.Count==0;
            if(!checks.Values.All(x=>x))throw new InvalidOperationException("样例关系已改变，停止副本生成。");
            window.Width=1400;window.Height=900;
            foreach(var track in project.Tracks)
            {
                var oldParts=track.Segments.Count;
                var legs=track.Segments[0].RouteProvenance!.Legs;
                var originalEnd=track.EndPoint!.Coordinate;
                main.StartMixedPlanCommand.Execute(null);await Wait(()=>main.IsRoutePlanningActive,"混合规划未进入");
                async Task Pick(GeoCoordinate p,int count){await main.Map.RunP7PlanningClickSmokeAsync(p.Latitude,p.Longitude);await Wait(()=>main.RoutePlanningPoints.Count==count&&main.UndoPlanningPointCommand.CanExecute(null),"规划点未完成，"+main.RoutePlanningStatus);}
                await Pick(legs[0].BRouterRequestMetadata!.RequestedStart,1);
                for(var i=0;i<legs.Count;i++)
                {
                    if(legs[i].Mode==RouteLegMode.Manual)main.NextManualLegCommand.Execute(null);else main.NextBRouterLegCommand.Execute(null);
                    var end=legs[i].Mode==RouteLegMode.BRouter?legs[i].BRouterRequestMetadata!.RequestedEnd:
                        i+1<legs.Count?legs[i+1].BRouterRequestMetadata!.RequestedStart:originalEnd;
                    await Pick(end,i+2);
                }
                var service=T10ManualField<RoutePlanService>(main,"_routePlan");
                var result=service.BuildTrack(track.Name!,track.Description);
                checks[track.Id+"实际路由切换后物理段为1"]=result.Segments.Count==1;
                checks[track.Id+"吸附后的实际几何里程正确"]=Math.Abs(result.GetTotalLength()-service.Plan.Legs.Sum(l=>l.DistanceMeters))<.000001;
                checks[track.Id+"没有增加额外接续点"]=result.GetPointCount()==service.Plan.Legs.Sum(l=>l.Geometry.Count)-service.Plan.Legs.Count+1;
                checks[track.Id+"手绘终点直接等于路由起点"]=service.Plan.Legs.Select((l,i)=>(l,i)).Where(x=>x.i>0&&service.Plan.Legs[x.i-1].Mode==RouteLegMode.Manual&&x.l.Mode==RouteLegMode.BRouter).All(x=>service.Plan.Legs[x.i-1].Geometry[^1]==x.l.Geometry[0]);
                var before=service.Plan.Legs[^1];var raw=before.Geometry.ToArray();
                main.UndoPlanningPointCommand.Execute(null);main.RedoPlanningPointCommand.Execute(null);
                checks[track.Id+"撤销重做不改变原路由点"]=ReferenceEquals(before,service.Plan.Legs[^1])&&raw.SequenceEqual(before.Geometry);
                await main.Map.SetRoutePreviewAsync(service.Plan);await Task.Delay(500);
                var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
                var junction=service.Plan.Legs.Last(l=>l.Mode==RouteLegMode.BRouter).Geometry[0];
                await web.CoreWebView2.ExecuteScriptAsync($"window.hikingGuide3DTest.at([{junction.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{junction.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}],22);true");
                await Task.Delay(1200);await main.Map.CapturePreviewAsync(Path.Combine(folder,$"junction-{track.Id}.png"));
                var request=new TrackExportRequest{Tracks=[result],DocumentName=result.Name};
                var gpx=Path.Combine(folder,$"{track.Id}.gpx");var kml=Path.Combine(folder,$"{track.Id}.kml");
                await new GpxTrackExporter().ExportAsync(request,gpx);await new KmlTrackExporter().ExportAsync(request,kml);
                checks[track.Id+"GPX导出单段"]=System.Xml.Linq.XDocument.Load(gpx).Descendants().Count(x=>x.Name.LocalName=="trkseg")==1;
                checks[track.Id+"KML导出单线"]=System.Xml.Linq.XDocument.Load(kml).Descendants().Count(x=>x.Name.LocalName=="LineString")==1;
                diagnostics.Add(new{track.Id,OldParts=oldParts,NewParts=result.Segments.Count,ExtraConnectionPoints=0,Distance=result.GetTotalLength()});
                track.Segments=result.Segments;track.RecalculateDistances();
                T10ManualInvoke(main,"CancelRoutePlan",false);
            }
            project.Id=Guid.NewGuid();project.Name="凤凰山-混合规划修复副本";
            var repaired=Path.Combine(folder,"凤凰山-混合规划修复副本.hikeproj");await new ProjectFileService().SaveAsync(project,repaired,false);
            var reopened=await new ProjectFileService().LoadAsync(repaired);checks["保存重开仍为连续轨迹"]=reopened.Tracks.All(t=>t.Segments.Count==1);
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(x=>x);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
