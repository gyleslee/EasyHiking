using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunProjectTreeReentrancySmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var diagnostics=new List<object>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        async Task Flush(){window.ProjectTree.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);window.ProjectTree.UpdateLayout();await Task.Delay(30);}
        try
        {
            for(var i=0;i<400&&!main.Map.IsReady;i++)await Task.Delay(100);
            using var bitmap=new SkiaSharp.SKBitmap(256,256);using(var c=new SkiaSharp.SKCanvas(bitmap))c.Clear(SkiaSharp.SKColors.Beige);using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var png=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();FavoritesPanel.Descendants<MapView>(window).First().BridgeForAutomation!.TileFetchForTest=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
            GlobalSettingsService.Current.MapSources=[new(){Name="项目树隔离底图",UrlTemplate="https://tree.invalid/{z}/{x}/{y}.png"}];GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=GlobalSettingsService.Current.MapSources[0].Id};GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=false,HillshadeVisible=false};GlobalSettingsService.Current.OsmRoads.Enabled=false;GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            var projects=new List<HikingProject>();
            await main.OpenForAutomationAsync(Environment.GetEnvironmentVariable("HIKINGGUIDE_TRACK_SCENE_PROJECT")!);projects.Add(main.MapContextProject!);
            for(var p=0;p<2;p++)
            {
                var project=new HikingProject{Name=$"多项目切换验收{p+1}"};
                for(var t=0;t<160;t++)project.AddTrack(new Track{Name=$"长名称轨迹{t+1}—虚拟化回收与样式选择验证",Segments=[new(){Points=[new(){Latitude=23+p*.01,Longitude=113+t*.00001},new(){Latitude=23.002+p*.01,Longitude=113.002+t*.00001}]}]},false);
                foreach(var track in project.Tracks)track.RecalculateDistances();
                var path=Path.Combine(folder,$"fixture-{p}.hikeproj");await new ProjectFileService().SaveAsync(project,path,false);await main.OpenForAutomationAsync(path);projects.Add(main.MapContextProject!);
            }
            await main.WaitForMapRefreshForAutomationAsync();await main.Map.ApplyGlobalMapLayersAsync(true);await main.Map.UpdateFavoritesAsync([],false);await Flush();
            string Baseline()=>JsonSerializer.Serialize(projects.Select(p=>new{p.Id,Tracks=p.Tracks.Select(t=>new{t.Id,t.Segments,t.Metadata}),p.Waypoints,p.GuideNodes,p.GuideSegments}));var baseline=Baseline();
            NavigationNode Root(HikingProject p)=>main.NavigationNodes.Single(n=>ReferenceEquals(n.Model,p));
            var syncRebuild=0;var maxRefreshDepth=0;var resets=0;
            NotifyCollectionChangedEventHandler observe=(_,e)=>{if(e.Action!=NotifyCollectionChangedAction.Reset)return;resets++;var frames=new StackTrace().GetFrames();if(frames.Any(f=>f.GetMethod()?.Name=="OnNavigationSelectionChanged"))syncRebuild++;maxRefreshDepth=Math.Max(maxRefreshDepth,frames.Count(f=>f.GetMethod()?.Name=="RefreshProjectView"));};main.NavigationNodes.CollectionChanged+=observe;
            var switches=true;var trackSelections=true;
            for(var i=0;i<75;i++)
            {
                foreach(var item in main.NavigationNodes)item.IsExpanded=false;await Flush();
                var p=projects[i%projects.Count];var root=Root(p);root.IsSelected=true;await Flush();var rootOk=ReferenceEquals(main.MapContextProject,p)&&window.ProjectTree.SelectedItem is NavigationNode selectedRoot&&ReferenceEquals(selectedRoot.Model,p);switches&=rootOk;
                Root(p).IsExpanded=true;if(window.ProjectTree.ItemContainerGenerator.ContainerFromItem(Root(p)) is TreeViewItem rootContainer)rootContainer.BringIntoView();await Flush();
                var track=Root(p).Children.First(n=>n.Model is Track);track.IsSelected=true;await Flush();var trackOk=main.PrimarySelectedTrack?.Id==((Track)track.Model!).Id;trackSelections&=trackOk;
                if((!rootOk||!trackOk)&&diagnostics.Count<10)diagnostics.Add(new{i,rootOk,trackOk,expected=p.Name,current=main.MapContextProject?.Name,selected=(window.ProjectTree.SelectedItem as NavigationNode)?.DisplayName});
                if(i%5==0){Root(p).IsExpanded=false;await Flush();Root(p).IsExpanded=true;await Flush();}
                if(i%10==0)await File.WriteAllTextAsync(Path.Combine(folder,"progress.json"),JsonSerializer.Serialize(new{i,resets,syncRebuild,maxRefreshDepth}));
            }
            await main.WaitForMapRefreshForAutomationAsync();await Flush();checks["异步地图刷新不覆盖较新的树选择"]=main.PrimarySelectedTrack?.Id==((Track)Root(projects[74%3]).Children.First(n=>n.Model is Track).Model!).Id&&window.ProjectTree.SelectedItem is NavigationNode finalSelection&&finalSelection.Model is Track;
            checks["真实WPF选中回调完成75轮项目切换"]=switches;checks["切换后轨迹选择正确"]=trackSelections;checks["选中事件栈内不再清空项目树"]=syncRebuild==0&&resets>=60;checks["重建无递归进入"]=maxRefreshDepth==1;
            Root(projects[0]).IsSelected=true;Root(projects[1]).IsSelected=true;await Flush();checks["快速连续选择只处理最终节点"]=ReferenceEquals(main.MapContextProject,projects[1]);
            var current=main.MapContextProject!;var foreign=Root(projects[2]);NotifyCollectionChangedEventHandler reenter=(_,e)=>{if(e.Action==NotifyCollectionChangedAction.Reset)main.SelectNavigationNode(foreign,false);};main.NavigationNodes.CollectionChanged+=reenter;main.RefreshProjectTreeForAutomation();main.NavigationNodes.CollectionChanged-=reenter;await Flush();checks["重建期间非空旧节点不能重入切项目"]=ReferenceEquals(main.MapContextProject,current);
            var threw=false;NotifyCollectionChangedEventHandler fail=(_,e)=>{if(e.Action==NotifyCollectionChangedAction.Reset)throw new InvalidOperationException("隔离测试：模拟重建异常");};main.NavigationNodes.CollectionChanged+=fail;try{main.RefreshProjectTreeForAutomation();}catch(InvalidOperationException){threw=true;}finally{main.NavigationNodes.CollectionChanged-=fail;}checks["异常后重建保护解除"]=threw&&!main.IsProjectTreeRefreshActive;main.RefreshProjectTreeForAutomation();await Flush();checks["异常恢复后可再次切项目"]=main.NavigationNodes.Count==3;
            var rootNow=Root(current);rootNow.IsExpanded=true;await Flush();var tracks=rootNow.Children.Where(n=>n.Model is Track).Take(2).ToArray();main.UpdateProjectItemSelection(tracks[0],ModifierKeys.None,focusOnMap:false);main.UpdateProjectItemSelection(tracks[1],ModifierKeys.Control,focusOnMap:false);checks["Ctrl多选继续有效"]=main.SelectedProjectItemCount==2;main.RefreshProjectTreeForAutomation();await Flush();checks["重建保留多选和展开"]=main.SelectedProjectItemCount==2&&Root(current).IsExpanded;
            var style=window.ProjectTree.ItemContainerStyle;checks["项目树使用固定作用域样式"]=style is not null&&window.Resources[typeof(TreeViewItem)] is null;checks["保留虚拟化和回收模式"]=VirtualizingPanel.GetIsVirtualizing(window.ProjectTree)&&VirtualizingPanel.GetVirtualizationMode(window.ProjectTree)==VirtualizationMode.Recycling;
            checks["轨迹节点人工内容不变"]=baseline==Baseline();diagnostics.Add(new{resets,syncRebuild,maxRefreshDepth,projects=projects.Count,tracks=projects.Sum(p=>p.Tracks.Count)});main.NavigationNodes.CollectionChanged-=observe;
            await CaptureT10ClientAsync(window,Path.Combine(folder,"project-tree.png"));var about=new AboutWindow{Owner=window};about.Show();await Task.Delay(100);await CaptureT10ClientAsync(about,Path.Combine(folder,"29-about.png"));about.Close();
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
