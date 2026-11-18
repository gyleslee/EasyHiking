using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using Microsoft.Web.WebView2.Wpf;
using Track=HikingGuide.Core.Models.Track;
namespace HikingGuide.App;
public partial class App
{
    private async Task VerifyTrackContextActionsAsync(MainWindow window,MainViewModel main,string folder,Dictionary<string,bool> checks)
    {
        var project=main.MapContextProject!;var track=project.Tracks[0];var other=project.Tracks[1];
        var mapView=FavoritesPanel.Descendants<MapView>(window).First();
        var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
        async Task<JsonElement> Script(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
        async Task Wait(Func<bool> predicate,string message){for(var i=0;i<180;i++){if(predicate()&&!main.IsBusy)return;await Task.Delay(100);}throw new TimeoutException(message+"；"+main.StatusText);}
        MapDisplayData Display()=>T10ManualField<MapDisplayData>(main.Map,"_lastDisplayData");
        bool ShowsPart(Guid id)=>Display().Tracks.SelectMany(t=>t.RenderPieces??[]).Any(p=>p.SplitSectionId==id);
        async Task Click(double lon,double lat,bool right=false,bool alt=false)
        {
            var position=await Script(FormattableString.Invariant($"window.hikingGuide3DTest.surface({lon:R},{lat:R})"));var x=position.GetProperty("x").GetDouble();var y=position.GetProperty("y").GetDouble();
            foreach(var type in new[]{"mouseMoved","mousePressed","mouseReleased"})
                await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button=type=="mouseMoved"?"none":right?"right":"left",buttons=type=="mousePressed"?(right?2:1):0,modifiers=alt?1:0,clickCount=type=="mouseMoved"?0:1}));
            await Task.Delay(350);
        }
        async Task<ContextMenu> Open(bool whole=false)
        {await Click(113.002,23,true,whole);await Wait(()=>mapView.ActiveTrackMenuForAutomation?.IsOpen==true,"轨迹右键菜单未打开");return mapView.ActiveTrackMenuForAutomation!;}
        MenuItem Item(ContextMenu menu,string title)=>menu.Items.OfType<MenuItem>().Single(i=>i.Header?.ToString()==title);
        void Invoke(ContextMenu menu,string title){var item=Item(menu,title);if(!item.Command.CanExecute(item.CommandParameter))throw new InvalidOperationException(title+"未启用");menu.IsOpen=false;item.Command.Execute(item.CommandParameter);}
        async Task Undo(){main.UndoCommand.Execute(null);await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(350);}
        if(main.IsTrackEditActive){main.ExitTrackEditCommand.Execute(null);await Task.Delay(300);}
        var menu=await Open();var names=menu.Items.OfType<MenuItem>().Select(m=>m.Header?.ToString()).ToArray();
        checks["标注入口紧接添加收藏点"]=Array.IndexOf(names,"在此添加普通标注")==Array.IndexOf(names,"添加收藏点")+1&&Array.IndexOf(names,"在此添加攻略节点")==Array.IndexOf(names,"添加收藏点")+2;
        checks["普通轨迹攻略节点遵守主攻略规则"]=!Item(menu,"在此添加攻略节点").Command.CanExecute(Item(menu,"在此添加攻略节点").CommandParameter);
        var oldMarkers=project.Waypoints.Select(p=>p.Id).ToHashSet();Invoke(menu,"在此添加普通标注");await Wait(()=>project.Waypoints.Count==oldMarkers.Count+1,"右键添加普通标注失败");
        var added=project.Waypoints.Single(p=>!oldMarkers.Contains(p.Id));checks["普通模式标注吸附到所点轨迹GPS点"]=added.AssociatedTrackId==track.Id&&added.Coordinate==track.Segments[0].Points[2].Coordinate&&added.PreferredTrackPosition?.TrackSegmentId==track.Segments[0].Id;
        await Undo();
        var section=track.GetSplitSections()[0].Id;menu=await Open();checks["正常模式可隐藏分段"]=Item(menu,"隐藏轨迹").Command.CanExecute(Item(menu,"隐藏轨迹").CommandParameter);Invoke(menu,"隐藏轨迹");await Wait(()=>!ShowsPart(section),"分段未隐藏");
        checks["隐藏分段不隐藏其他分段或修改原轨迹"]=track.Segments.Count==3&&track.GetPointCount()==18&&Display().Tracks.Single(t=>t.TrackId==track.Id).RenderPieces!.Any()&&MapLayerVisibility.IsTrackVisible(project.Settings.Map,track.Id);
        var node=T10ManualNodes(main.NavigationNodes).Single(n=>n.Model is SplitSection s&&s.Id==section);checks["项目树分段显隐开关可恢复"]=node.CanToggleMapVisibility&&!node.IsMapVisible;node.IsMapVisible=true;await Wait(()=>ShowsPart(section),"项目树重新显示分段失败");
        menu=await Open(true);Invoke(menu,"隐藏轨迹");await Wait(()=>Display().Tracks.All(t=>t.TrackId!=track.Id),"普通模式整轨隐藏失败");checks["整轨隐藏保留其他轨迹并隐藏所属标注"]=Display().Tracks.Any(t=>t.TrackId==other.Id)&&Display().Waypoints.All(w=>project.Waypoints.First(p=>p.Id==w.WaypointId).AssociatedTrackId!=track.Id);await Undo();
        await main.EnterProjectEditForAutomationAsync(track.Id);await Task.Delay(350);
        menu=await Open();Invoke(menu,"隐藏轨迹");await Wait(()=>!ShowsPart(section),"编辑模式分段隐藏失败");checks["编辑模式隐藏分段仍保持编辑会话"]=main.IsTrackEditActive;await Undo();
        menu=await Open(true);Invoke(menu,"隐藏轨迹");await Wait(()=>Display().Tracks.All(t=>t.TrackId!=track.Id),"编辑临时显示覆盖整轨隐藏");checks["编辑模式整轨隐藏不会被临时显示复活"]=main.IsTrackEditActive;await Undo();
        await Click(113.0015,23,true);await Wait(()=>mapView.ActiveTrackMenuForAutomation?.IsOpen==true,"中间位置菜单未打开");menu=mapView.ActiveTrackMenuForAutomation!;Invoke(menu,"区间裁剪");
        await Wait(()=>main.GetType().GetField("_trackRangeStart",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(main) is not null,"右击起点未登记");
        var start=T10ManualField<TrackPosition>(main,"_trackRangeStart");checks["右键裁剪直接记录右击位置为第一边界"]=Math.Abs(start.SnappedCoordinate.Longitude-113.0015)<.00001;
        var revision=track.GeometryRevision;await Click(113.004,23);await Wait(()=>track.GeometryRevision>revision,"右键裁剪仍需多点一次");checks["右键裁剪只需再选一个点"]=track.Segments[0].Points.Count==5&&track.Segments[1].Points.Count==6;await Undo();
        var firstSection=track.GetSplitSections()[0];main.UpdateProjectItemSelection(T10ManualNodes(main.NavigationNodes).Single(n=>n.Model is SplitSection s&&s.Id==firstSection.Id),System.Windows.Input.ModifierKeys.None,focusOnMap:false);
        main.CutJoinRangeCommand.Execute(null);var field=main.GetType().GetField("_trackRangeStart",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;checks["顶部菜单裁剪仍等待用户选择两个边界"]=field.GetValue(main) is null;await main.CancelPendingTrackEditAsync();
        await main.ChangeMainTrackForAutomationAsync(track.Id);await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(350);
        // 选开已有标注的位置，确保命中轨迹菜单而不是标注菜单。
        await Click(113.0015,23,true);await Wait(()=>mapView.ActiveTrackMenuForAutomation?.IsOpen==true,"主攻略轨迹菜单未打开");menu=mapView.ActiveTrackMenuForAutomation!;
        checks["主攻略轨迹可添加攻略节点"]=Item(menu,"在此添加攻略节点").Command.CanExecute(Item(menu,"在此添加攻略节点").CommandParameter);
        await CaptureT10ElementAsync(menu,Brushes.White,Path.Combine(folder,"map-edit-context.png"));
        var oldNodes=project.GuideNodes.Select(n=>n.Id).ToHashSet();Invoke(menu,"在此添加攻略节点");await Wait(()=>project.GuideNodes.Count==oldNodes.Count+1,"右键添加攻略节点失败");
        var guide=project.GuideNodes.Single(n=>!oldNodes.Contains(n.Id));checks["攻略节点归属主轨迹且保留精确GPS身份"]=guide.TrackId==track.Id&&!guide.IsOrphaned&&guide.ExactPosition?.TrackSegmentId==track.Segments[0].Id&&track.Segments[0].Points.Any(p=>p.Coordinate==guide.SnappedCoordinate);
        await Undo();
        main.ExitTrackEditCommand.Execute(null);await Task.Delay(300);
    }
}
