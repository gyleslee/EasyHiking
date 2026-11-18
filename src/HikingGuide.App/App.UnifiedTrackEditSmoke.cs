using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
using Track = HikingGuide.Core.Models.Track;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunUnifiedTrackEditSmokeAsync(MainWindow window, MainViewModel main, string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            async Task Wait(Func<bool> condition,string message)
            { for(var i=0;i<160;i++){if(condition()&&!main.IsBusy)return;await Task.Delay(100);}throw new TimeoutException(message+"；"+main.StatusText); }
            await Wait(()=>main.Map.IsReady,"地图未就绪");
            var mapView=FavoritesPanel.Descendants<MapView>(window).First();
            using var bitmap=new SkiaSharp.SKBitmap(256,256);using(var canvas=new SkiaSharp.SKCanvas(bitmap))canvas.Clear(SkiaSharp.SKColors.Beige);
            using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var png=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();
            mapView.BridgeForAutomation!.TileFetchForTest=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
            GlobalSettingsService.Current.MapSources=[new(){Name="编辑操作演示底图",UrlTemplate="https://edit.invalid/{z}/{x}/{y}.png"}];
            GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=GlobalSettingsService.Current.MapSources[0].Id};
            GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=false,HillshadeVisible=false};
            GlobalSettingsService.Current.OsmRoads.Enabled=false;GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            Track Make(string name,double lat)=>new(){Name=name,Segments=Enumerable.Range(0,3).Select(s=>new TrackSegment
            {SplitSectionId=Guid.NewGuid(),SplitSectionName=$"{name}（{s+1}）",Points=Enumerable.Range(0,6).Select(i=>new TrackPoint{Latitude=lat,Longitude=113+s*.007+i*.001,SourceElevation=100+i*5}).ToList()}).ToList()};
            var project=new HikingProject{Name="轨迹编辑操作示例"};var a=Make("山脊线路",23);var b=Make("备选线路",23.002);a.RecalculateDistances();b.RecalculateDistances();project.AddTrack(a,false);project.AddTrack(b,false);
            var marker=new Waypoint{Name="示例标注",AssociatedTrackId=a.Id,Latitude=23,Longitude=113.003};project.Waypoints.Add(marker);
            project.Settings.Map.Center=new(23.0005,113.0025);project.Settings.Map.Zoom=17;
            var path=Path.Combine(folder,"编辑操作示例.hikeproj");await new ProjectFileService().SaveAsync(project,path,false);await main.OpenForAutomationAsync(path);
            project=main.MapContextProject!;a=project.Tracks[0];b=project.Tracks[1];marker=project.Waypoints.Single();
            await main.WaitForMapRefreshForAutomationAsync();await main.Map.ApplyGlobalMapLayersAsync(true);await main.Map.UpdateFavoritesAsync([],false);await Task.Delay(500);
            main.AutoConfirmTrackEditForAutomation=true;
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> Script(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            Task Input(string type,double x,double y,int mods=0,string button="left",int buttons=0)=>web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button,buttons,modifiers=mods,clickCount=type=="mouseMoved"?0:1}));
            async Task Click(TrackPoint point,bool ctrl=false,bool right=false)
            {
                var loc=await Script(FormattableString.Invariant($"window.hikingGuide3DTest.surface({point.Longitude:R},{point.Latitude:R})"));
                var x=loc.GetProperty("x").GetDouble();var y=loc.GetProperty("y").GetDouble();
                await Input("mouseMoved",x,y,ctrl?2:0,button:"none");await Input("mousePressed",x,y,ctrl?2:0,right?"right":"left",right?2:1);await Input("mouseReleased",x,y,ctrl?2:0,right?"right":"left");await Task.Delay(350);
            }
            NavigationNode Node(object model)=>T10ManualNodes(main.NavigationNodes).Single(n=>ReferenceEquals(n.Model,model)||model is SplitSection s&&n.Model is SplitSection candidate&&candidate.Id==s.Id);
            async Task Select(object model,bool ctrl=false)
            {main.UpdateProjectItemSelection(Node(model),ctrl?ModifierKeys.Control:ModifierKeys.None,focusOnMap:false);await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);await main.WaitForTrackEditTargetForAutomationAsync();}
            async Task Undo(){main.UndoCommand.Execute(null);await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(300);}
            var editMenu=FavoritesPanel.Descendants<Menu>(window).Single().Items.OfType<MenuItem>().Single(m=>m.Header?.ToString()=="轨迹编辑");
            var headers=editMenu.Items.OfType<MenuItem>().Select(m=>m.Header?.ToString()).ToArray();
            checks["菜单栏统一编辑入口"]=new[]{"拆分轨迹","删除轨迹","反转轨迹","区间裁剪","删除此点前轨迹","删除此点后轨迹"}.All(headers.Contains)&&!headers.Any(h=>h is "删除轨迹段" or "反转轨迹段" or "反转整条轨迹");
            await Select(a);await Select(b,true);var aStart=a.Segments[0].Points[0].Id;var bStart=b.Segments[0].Points[0].Id;
            main.ReverseGeometryCommand.Execute(null);await Wait(()=>a.GeometryRevision==1&&b.GeometryRevision==1,"批量反转失败");
            checks["整轨批量反转及一次撤销"]=a.Segments[^1].Points[^1].Id==aStart&&b.Segments[^1].Points[^1].Id==bStart;
            await Undo();checks["批量反转撤销恢复两条轨迹"]=a.Segments[0].Points[0].Id==aStart&&b.Segments[0].Points[0].Id==bStart;
            await Select(a);main.EnterTrackEditCommand.Execute(null);await Wait(()=>main.IsTrackEditActive,"编辑模式未进入");await Task.Delay(500);
            await Click(a.Segments[0].Points[1]);await Wait(()=>main.SelectedTrackEditPointId==a.Segments[0].Points[1].Id,"GPS点未选中");
            await Click(a.Segments[0].Points[2],true);
            var selected=T10ManualField<Dictionary<Guid,TrackEditHandle>>(main,"_selectedEditPoints");await Wait(()=>selected.Count==2,"Ctrl圆点多选失败");checks["真实鼠标Ctrl多选GPS点"]=selected.Count==2;
            await Click(a.Segments[0].Points[2],true);await Wait(()=>selected.Count==1,"Ctrl取消选点失败");
            checks["Ctrl取消圆点后主选仍属于已选点"]=main.SelectedTrackEditPointId==a.Segments[0].Points[1].Id;
            await Click(a.Segments[0].Points[2],true);await Wait(()=>selected.Count==2,"重新加选GPS点失败");
            var camera=(await Script("window.hikingGuideT10Test.inspect().camera")).GetRawText();
            await CaptureT10ClientAsync(window,Path.Combine(folder,"gps-point-multiselect.png"));
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyDown\",\"key\":\"Delete\",\"code\":\"Delete\",\"windowsVirtualKeyCode\":46}");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyUp\",\"key\":\"Delete\",\"code\":\"Delete\",\"windowsVirtualKeyCode\":46}");
            await Wait(()=>a.Segments[0].Points.Count==4,"Delete批量删除GPS点失败");
            checks["地图Delete删除选中GPS点并保持视野"]=camera==(await Script("window.hikingGuideT10Test.inspect().camera")).GetRawText();await Undo();
            await Select(a.GetSplitSections()[0]);main.CutJoinRangeCommand.Execute(null);await Click(a.Segments[0].Points[1]);await Click(a.Segments[0].Points[4]);
            await Wait(()=>a.Segments[0].Points.Count==4,"两点区间裁剪未执行");checks["实际两点裁剪直连并保留其他分段"]=a.Segments.Count==3&&a.Segments[1].Points.Count==6;await Undo();
            await Click(a.Segments[0].Points[2]);await Wait(()=>main.SelectedTrackEditPointId==a.Segments[0].Points[2].Id,"裁剪基准GPS点未选中");
            main.TrimBeforeCommand.Execute(null);await Wait(()=>a.Segments[0].Points.Count==4,"删除此点前失败");checks["删除此点前仅处理当前分段"]=a.Segments.Count==3&&a.Segments[1].Points.Count==6;await Undo();
            await Click(a.Segments[0].Points[2]);await Wait(()=>main.SelectedTrackEditPointId==a.Segments[0].Points[2].Id,"后裁剪基准未选中");
            main.TrimAfterCommand.Execute(null);await Wait(()=>a.Segments[0].Points.Count==3,"删除此点后失败");checks["删除此点后仅处理当前分段"]=a.Segments.Count==3&&a.Segments[1].Points.Count==6;await Undo();
            await Select(a.GetSplitSections()[1]);await Select(b.GetSplitSections()[1],true);main.DeleteGeometryCommand.Execute(null);await Wait(()=>a.Segments.Count==2&&b.Segments.Count==2,"跨轨迹分段批量删除失败");checks["不同轨迹分段可批量删除"]=true;await Undo();
            await Select(a);main.DeleteRelatedMarkersForAutomation=false;main.DeleteGeometryCommand.Execute(null);await Wait(()=>!project.Tracks.Contains(a),"整轨删除失败");checks["保留标注选择不删除标注"]=project.Waypoints.Contains(marker)&&marker.AssociatedTrackId is null;await Undo();
            await Select(a);main.DeleteRelatedMarkersForAutomation=true;main.DeleteGeometryCommand.Execute(null);await Wait(()=>!project.Tracks.Contains(a),"同时删除标注失败");checks["删除相关标注选择生效"]=!project.Waypoints.Contains(marker);await Undo();checks["一次撤销恢复轨迹标注和归属"]=project.Tracks.Contains(a)&&project.Waypoints.Contains(marker)&&marker.AssociatedTrackId==a.Id;main.DeleteRelatedMarkersForAutomation=false;
            await Select(a);await Click(a.Segments[0].Points[2],right:true);await Wait(()=>mapView.ActiveTrackMenuForAutomation?.IsOpen==true,"地图菜单未打开");
            var menu=mapView.ActiveTrackMenuForAutomation!;var mapHeaders=menu.Items.OfType<MenuItem>().Select(m=>m.Header?.ToString()).ToArray();
            checks["地图右键统一编辑菜单"]=new[]{"拆分轨迹","删除轨迹","反转轨迹","区间裁剪","删除此点前轨迹","删除此点后轨迹"}.All(mapHeaders.Contains)&&!mapHeaders.Any(h=>h is "删除轨迹段" or "反转轨迹段" or "反转整条轨迹");
            await CaptureT10ElementAsync(menu,Brushes.White,Path.Combine(folder,"map-edit-context.png"));menu.IsOpen=false;
            Node(a).IsExpanded=true;main.NavigationNodes[0].IsExpanded=true;await T10ManualLayoutAsync(window);
            var text=FavoritesPanel.Descendants<TextBlock>(window.ProjectTree).First(t=>t.DataContext is NavigationNode n&&ReferenceEquals(n.Model,a)&&t.ContextMenu is not null);
            var treeMenu=text.ContextMenu!;treeMenu.PlacementTarget=text;treeMenu.Placement=PlacementMode.Right;treeMenu.IsOpen=true;await T10ManualLayoutAsync(window);
            var treeHeaders=treeMenu.Items.OfType<MenuItem>().Select(m=>m.Header?.ToString()).ToArray();
            checks["项目树保留统一删除反转并移除点及裁剪工具"]=new[]{"删除轨迹","反转轨迹"}.All(treeHeaders.Contains)&&!treeHeaders.Any(h=>h is "拆分轨迹段" or "拆分轨迹" or "添加轨迹点" or "删除轨迹点" or "区间裁剪" or "删除此点前轨迹" or "删除此点后轨迹");
            checks["项目树删除紧邻反转上方"]=Array.IndexOf(treeHeaders,"反转轨迹")==Array.IndexOf(treeHeaders,"删除轨迹")+1;
            await CaptureT10ElementAsync(treeMenu,Brushes.White,Path.Combine(folder,"24-context-track.png"));treeMenu.IsOpen=false;
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_CONTEXT_ACTION_TEST")=="1")await VerifyTrackContextActionsAsync(window,main,folder,checks);
            var about=new AboutWindow{Owner=window};about.Show();await T10ManualLayoutAsync(about);await CaptureT10ClientAsync(about,Path.Combine(folder,"29-about.png"));about.Close();
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(x=>x);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
