using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Routing;
using HikingGuide.Core.Models;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
using Track=HikingGuide.Core.Models.Track;
namespace HikingGuide.App;
public partial class App
{
    private sealed class ContinuationSmokeProvider:IRoutingProvider
    {
        public Task<RoutingResult> RouteFootShortestAsync(GeoCoordinate start,GeoCoordinate end,CancellationToken token=default)=>
            Task.FromResult(new RoutingResult([new(start.Latitude+.0001,start.Longitude),end],"测试步行","隔离数据"));
    }
    private async Task<bool> RunIndependentTrackSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            async Task Wait(Func<bool> condition,string message){for(int i=0;i<220;i++){if(condition()&&!main.IsBusy)return;await Task.Delay(100);}throw new TimeoutException(message+"；"+main.StatusText);}
            await Wait(()=>main.Map.IsReady,"地图未就绪");
            window.ProjectPanelColumn.Width=new GridLength(270);window.PropertiesPanelColumn.Width=new GridLength(190);
            var view=FavoritesPanel.Descendants<MapView>(window).First();
            using var bitmap=new SkiaSharp.SKBitmap(256,256);using(var canvas=new SkiaSharp.SKCanvas(bitmap))canvas.Clear(SkiaSharp.SKColors.Beige);
            using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var png=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();
            view.BridgeForAutomation!.TileFetchForTest=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
            GlobalSettingsService.Current.MapSources=[new(){Name="操作演示底图",UrlTemplate="https://edit.invalid/{z}/{x}/{y}.png"}];
            GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=GlobalSettingsService.Current.MapSources[0].Id};
            GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=false,HillshadeVisible=false};
            GlobalSettingsService.Current.OsmRoads.Enabled=false;GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            Track Make(string name,int parts,double lat)=>new(){Name=name,DisplayColor="#4477AA",SourceFormat=TrackSourceFormat.Gpx,
                Segments=Enumerable.Range(0,parts).Select(s=>new TrackSegment{SplitSectionId=Guid.NewGuid(),
                SplitSectionName=$"{name}（{s+1}）",Points=Enumerable.Range(0,12).Select(i=>new TrackPoint{Latitude=lat,Longitude=113+s*.02+i*.001}).ToList()}).ToList()};
            var project=new HikingProject{Name="独立轨迹与继续规划示例"};var a=Make("山脊线路",2,23);var b=Make("备选线路",1,23.0012);
            a.RecalculateDistances();b.RecalculateDistances();project.AddTrack(a,false);project.AddTrack(b,false);
            project.Waypoints.Add(new(){Name="示例图片标注",AssociatedTrackId=a.Id,Latitude=23,Longitude=113.009});
            project.Settings.Map.Center=new(23.0005,113.005);project.Settings.Map.Zoom=16;
            var path=Path.Combine(folder,"示例项目.hikeproj");await new ProjectFileService().SaveAsync(project,path,false);var original=File.ReadAllBytes(path);
            await main.OpenForAutomationAsync(path);project=main.MapContextProject!;a=project.Tracks[0];b=project.Tracks.Last();
            await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(900);main.AutoConfirmTrackEditForAutomation=true;main.ConfigureRouting(new ContinuationSmokeProvider());
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> Script(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            async Task Click(double lon,double lat,bool right=false,bool ctrl=false){var pt=await Script(FormattableString.Invariant($"window.hikingGuide3DTest.surface({lon:R},{lat:R})"));var x=pt.GetProperty("x").GetDouble();var y=pt.GetProperty("y").GetDouble();foreach(var type in new[]{"mouseMoved","mousePressed","mouseReleased"})await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button=type=="mouseMoved"?"none":right?"right":"left",buttons=type=="mousePressed"?(right?2:1):0,modifiers=ctrl?2:0,clickCount=type=="mouseMoved"?0:1}));await Task.Delay(400);}
            async Task Undo(){main.UndoCommand.Execute(null);await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(350);}
            checks["旧项目分段转换为独立轨迹且原文件未改写"]=project.Tracks.Count==3&&project.IsDirty&&File.ReadAllBytes(path).SequenceEqual(original);
            checks["项目树不再显示拆分分段组"]=!T10ManualNodes(main.NavigationNodes).Any(n=>n.DisplayName.StartsWith("拆分分段"));
            await Click(113.003,23);await Wait(()=>main.SelectedProjectItemCount==1,"地图单选失败");
            await Click(113.003,23.0012,ctrl:true);await Wait(()=>main.SelectedProjectItemCount==2,"地图多选失败");
            await T10ManualLayoutAsync(window);await CaptureT10ClientAsync(window,Path.Combine(folder,"30-map-multiselect.png"));
            var camera=(await Script("window.hikingGuideT10Test.inspect()" )).GetProperty("camera").GetRawText();
            await Click(113.003,23.0006);await Wait(()=>main.SelectedProjectItemCount==0,"空白处没有解除多选");
            checks["空白处解除全部轨迹选择并保持地图视角"]=project.ActiveTrackId is null&&camera==(await Script("window.hikingGuideT10Test.inspect()" )).GetProperty("camera").GetRawText();
            await main.EnterProjectEditForAutomationAsync(a.Id);await Task.Delay(450);await Click(113.004,23,right:true);await Wait(()=>view.ActiveTrackMenuForAutomation?.IsOpen==true,"右键菜单未打开");
            var menu=view.ActiveTrackMenuForAutomation!;var titles=menu.Items.OfType<MenuItem>().Select(m=>m.Header?.ToString()).ToArray();
            checks["继续规划与隐藏轨迹顺序正确"]=Array.IndexOf(titles,"继续规划路线")==Array.IndexOf(titles,"在此添加攻略节点")+1&&Array.IndexOf(titles,"隐藏轨迹")==Array.IndexOf(titles,"继续规划路线")+1;
            await CaptureT10ElementAsync(menu,Brushes.White,Path.Combine(folder,"map-edit-context.png"));
            var split=menu.Items.OfType<MenuItem>().Single(m=>m.Header?.ToString()=="拆分轨迹");menu.IsOpen=false;split.Command.Execute(split.CommandParameter);
            await Wait(()=>project.Tracks.Count==4,"拆分未生成独立轨迹");await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(500);
            checks["拆分得到两条独立轨迹且编号继续递增"]=project.Tracks.Count==4&&project.Tracks.Any(t=>t.Name=="山脊线路（3）")&&project.Tracks.All(t=>t.GetSplitSections().Count==1);
            checks["标注随拆分后的轨迹转移"]=project.Waypoints.Single().AssociatedTrackId==project.Tracks.Single(t=>t.Name=="山脊线路（3）").Id;
            main.NavigationNodes[0].IsExpanded=true;foreach(var node in T10ManualNodes(main.NavigationNodes).Where(n=>n.Model is Track))node.IsExpanded=true;
            await T10ManualLayoutAsync(window);await CaptureT10ClientAsync(window,Path.Combine(folder,"independent-tracks.png"));
            foreach(var connect in new[]{false,true})
            {
                var dialog=new TrackMergeWindow(project,connect,geometrySelection:new[]{new HikingGuide.Application.TrackEditing.TrackMergeSource(a.Id,false),new HikingGuide.Application.TrackEditing.TrackMergeSource(b.Id,false)}){Owner=window};
                dialog.Show();await T10ManualLayoutAsync(dialog);await CaptureT10ClientAsync(dialog,Path.Combine(folder,connect?"31-track-connect.png":"10-track-merge.png"));dialog.Close();
            }
            await Undo();checks["一次撤销恢复拆分前轨迹及标注归属"]=project.Tracks.Count==3&&project.Waypoints.Single().AssociatedTrackId==a.Id;
            main.RedoCommand.Execute(null);await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(400);checks["重做恢复独立轨迹"]=project.Tracks.Count==4;await Undo();
            await Click(113.003,23);await Wait(()=>main.SelectedTrackEditPointId is not null,"编辑点未选中");
            await Click(113.003,23.0006);checks["编辑模式空白点击取消轨迹选中但保留编辑模式"]=main.IsTrackEditActive&&main.SelectedProjectItemCount==0;
            checks["空白取消选择同时清除GPS手柄与区间预览"]=(await Script("window.hikingGuideT10Test.inspect().handleIds.length")).GetInt32()==0;
            var count=a.GetPointCount();var oldEnd=a.EndPoint!.Coordinate;var oldName=a.Name;var oldColor=a.DisplayColor;var oldOwner=project.Waypoints.Single().AssociatedTrackId;
            main.ContinueRoutePlanCommand.Execute(a);await Wait(()=>main.RoutePlanForAutomation?.ControlPoints.Count==1,"继续规划起点未设置");
            checks["继续规划以原轨迹终点为固定起点"]=main.RoutePlanForAutomation!.ControlPoints[0].Coordinate==oldEnd&&!main.IsTrackEditActive;
            await Click(113.006,23.0008);await Wait(()=>main.RoutePlanForAutomation?.Legs.Count==1,"继续规划未生成路由");
            await T10ManualLayoutAsync(window);await CaptureT10ClientAsync(window,Path.Combine(folder,"continue-routing.png"));
            main.CompletePlanningCommand.Execute(null);await Wait(()=>!main.IsRoutePlanningActive,"继续规划未提交");await main.WaitForMapRefreshForAutomationAsync();
            checks["继续规划追加原轨迹并保留名称颜色与标注"]=project.Tracks.Count==3&&a.GetPointCount()>count&&a.Segments[0].Points[count-1].Coordinate==oldEnd&&a.Name==oldName&&a.DisplayColor==oldColor&&project.Waypoints.Single().AssociatedTrackId==oldOwner;
            await Undo();checks["继续规划可一次撤销"]=a.GetPointCount()==count&&a.EndPoint!.Coordinate==oldEnd;
            main.StartManualPlanCommand.Execute(null);await Wait(()=>main.IsRoutePlanningActive,"临时规划未开始");await Click(113.002,23.0006);await Wait(()=>main.RoutePlanForAutomation?.ControlPoints.Count==1,"临时规划点未加入");
            main.ContinueRoutePlanCommand.Execute(a);await Wait(()=>main.RoutePlanForAutomation?.ControlPoints.Count==1&&main.RoutePlanForAutomation?.ControlPoints[0].Coordinate==a.EndPoint!.Coordinate,"继续规划未替换旧临时规划");
            main.GetType().GetMethod("CancelRoutePlan",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(main,[false]);
            checks["继续规划替换临时草稿并可取消且原轨迹不变"]=!main.IsRoutePlanningActive&&a.GetPointCount()==count;
            main.StartManualPlanCommand.Execute(null);await Wait(()=>main.IsRoutePlanningActive,"手绘未开始");await Click(113.001,23.001);await Click(113.004,23.0015);await Wait(()=>main.RoutePlanForAutomation?.Legs.Count==1,"手绘未完成");
            main.CompletePlanningCommand.Execute(null);await Wait(()=>!main.IsRoutePlanningActive,"手绘未提交");
            var first=project.Tracks.Last();main.StartManualPlanCommand.Execute(null);await Wait(()=>main.IsRoutePlanningActive,"第二次手绘未开始");await Click(113.002,23.0005);await Click(113.005,23.0012);await Wait(()=>main.RoutePlanForAutomation?.Legs.Count==1,"第二次手绘未完成");main.CompletePlanningCommand.Execute(null);await Wait(()=>!main.IsRoutePlanningActive,"第二次手绘未提交");
            checks["新规划轨迹随机颜色不与已有轨迹重复"]=first.DisplayColor!=project.Tracks.Last().DisplayColor&&first.DisplayColor!=a.DisplayColor;
            var planningMenu=FavoritesPanel.Descendants<Menu>(window).Single().Items.OfType<MenuItem>().Single(m=>m.Header?.ToString()=="路线规划");checks["总菜单提供继续规划入口"]=planningMenu.Items.OfType<MenuItem>().Any(m=>m.Header?.ToString()=="继续规划路线");
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_TRACK_SCENE_PROJECT") is {} scene && File.Exists(scene))
            {
                var storage=new ProjectFileService();var originalProject=await storage.LoadAsync(scene);
                var countBefore=originalProject.Tracks.Sum(t=>t.GetPointCount());
                var pointIds=originalProject.Tracks.SelectMany(t=>t.Segments).SelectMany(p=>p.Points).Select(p=>p.Id).ToHashSet();
                var markerIds=originalProject.Waypoints.Select(p=>p.Id).ToHashSet();
                var manual=JsonSerializer.Serialize(originalProject.ManualData);
                await main.OpenForAutomationAsync(scene);var migrated=main.MapContextProject!;await main.WaitForMapRefreshForAutomationAsync();
                var snapshot=Path.Combine(folder,"凤凰山-独立轨迹转换验证.hikeproj");await storage.SaveAsync(migrated,snapshot,false);
                var reloaded=await storage.LoadAsync(snapshot);
                checks["凤凰山真实副本转换后原始点标注及人工内容完整"]=reloaded.Tracks.Sum(t=>t.GetPointCount())==countBefore&&pointIds.SetEquals(reloaded.Tracks.SelectMany(t=>t.Segments).SelectMany(p=>p.Points).Select(p=>p.Id))&&markerIds.SetEquals(reloaded.Waypoints.Select(p=>p.Id))&&manual==JsonSerializer.Serialize(reloaded.ManualData);
                checks["凤凰山转换可通过完整性检查保存重开"]=reloaded.Tracks.Count>originalProject.Tracks.Count&&reloaded.Tracks.Where(t=>t.Id!=reloaded.MainGuideTrackId).All(t=>t.GetSplitSections().Count==1);
            }
        }
        catch(Exception e){error=e.ToString();try{await CaptureT10ClientAsync(window,Path.Combine(folder,"failure.png"));}catch{}}
        var passed=error is null&&checks.Values.All(x=>x);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}