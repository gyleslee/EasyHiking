using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunKilometerSmokeAsync(MainWindow owner,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var diagnostics=new List<object>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        async Task Wait(Func<Task<bool>> condition,string reason){for(var i=0;i<600;i++){if(await condition())return;await Task.Delay(100);}throw new TimeoutException(reason);}
        try
        {
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");
            var web=FavoritesPanel.Descendants<WebView2>(owner).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            using var bitmap=new SkiaSharp.SKBitmap(256,256);using(var c=new SkiaSharp.SKCanvas(bitmap))c.Clear(new SkiaSharp.SKColor(222,232,211));using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var png=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();
            FavoritesPanel.Descendants<MapView>(owner).First().BridgeForAutomation!.TileFetchForTest=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
            GlobalSettingsService.Current.MapSources=[new(){Name="隔离测试底图",UrlTemplate="https://kilometer.invalid/{z}/{x}/{y}.png"}];GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=GlobalSettingsService.Current.MapSources[0].Id};GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=false,HillshadeVisible=false};GlobalSettingsService.Current.OsmRoads.Enabled=false;GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            await main.OpenForAutomationAsync(Environment.GetEnvironmentVariable("HIKINGGUIDE_TRACK_SCENE_PROJECT")!);await main.WaitForMapRefreshForAutomationAsync();await main.Map.ApplyGlobalMapLayersAsync(true);await main.Map.UpdateFavoritesAsync([],false);
            var project=main.MapContextProject!;var service=new TrackKilometerService();var shortTrack=project.Tracks.Single(t=>t.Name=="一整条连接轨迹");var merged=project.Tracks.Single(t=>t.Name=="合并轨迹");var collection=project.Tracks.Single(t=>t.Name=="新合并轨迹");collection.Metadata[TrackKilometerService.RouteKindKey]="collection";
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_KILOMETER_FIXTURE_RESET")=="1"){foreach(var t in project.Tracks){TrackKilometerService.SetEnabled(t,false);MapLayerVisibility.SetTrackVisible(project.Settings.Map,t.Id,true);}await main.Map.RefreshVisibilityAsync();}
            string Baseline()=>JsonSerializer.Serialize(new{Geometry=project.Tracks.Select(t=>new{t.Id,t.Segments,t.GeometryRevision}),project.Waypoints,project.GuideNodes,project.GuideSegments,project.MediaAttachments,project.ExternalMediaReferences});var baseline=Baseline();
            checks["真实样本三类判定"]=service.Assess(shortTrack).Eligible&&service.Assess(merged).Eligible&&!service.Assess(collection).Eligible;
            checks[Environment.GetEnvironmentVariable("HIKINGGUIDE_KILOMETER_FIXTURE_RESET")=="1"?"验收副本公里标识已复位关闭":"所有样本默认关闭"]=project.Tracks.All(t=>!TrackKilometerService.IsEnabled(t));foreach(var t in project.Tracks.Where(t=>t!=collection))t.Metadata[TrackKilometerService.RouteKindKey]="route";
            checks["独立集合无树菜单入口"]=!new NavigationNode("集合",collection).CanKilometerMarks;
            main.ShowKilometersCommand.Execute(merged);await main.Map.RefreshVisibilityAsync();await JS("window.hikingGuide3DTest.at([113.42,23.24],14);true");
            await Wait(async()=>(await JS("window.hikingGuideKilometerTest.features().length")).GetInt32()==3,"公里点未显示");
            var state=await JS("window.hikingGuideKilometerTest.features()");diagnostics.Add(state);
            checks["一二公里及一位小数终点"]=state[0].GetProperty("properties").GetProperty("label").GetString()=="1"&&state[1].GetProperty("properties").GetProperty("label").GetString()=="2"&&state[2].GetProperty("properties").GetProperty("label").GetString()=="2.5";
            checks["小间隙计入显示总里程"]=Math.Abs(state[2].GetProperty("properties").GetProperty("distanceMeters").GetDouble()-merged.GetTotalLength()-6.165)<.02;
            main.ShowProjectKilometersCommand.Execute(project);await main.Map.RefreshVisibilityAsync();checks["项目批量跳过独立集合"]=TrackKilometerService.IsEnabled(shortTrack)&&TrackKilometerService.IsEnabled(merged)&&!TrackKilometerService.IsEnabled(collection);
            checks["不足一公里仅显示终点"]=service.Generate(shortTrack) is {Count:1} m&&m[0].IsEndpoint;
            main.HideProjectKilometersCommand.Execute(project);await main.Map.RefreshVisibilityAsync();checks["项目批量隐藏"]=project.Tracks.All(t=>!TrackKilometerService.IsEnabled(t));
            var nodes=main.NavigationNodes.SelectMany(Flatten).ToArray();main.UpdateProjectItemSelection(nodes.Single(n=>ReferenceEquals(n.Model,shortTrack)),ModifierKeys.None,focusOnMap:false);main.UpdateProjectItemSelection(nodes.Single(n=>ReferenceEquals(n.Model,merged)),ModifierKeys.Control,focusOnMap:false);main.ShowKilometersCommand.Execute(merged);await main.Map.RefreshVisibilityAsync();checks["多选轨迹批量显示"]=TrackKilometerService.IsEnabled(shortTrack)&&TrackKilometerService.IsEnabled(merged);
            var conflict=new Track{Name="起终方向相冲示例",Segments=[new(){Points=[new(){Latitude=23.2,Longitude=113.4},new(){Latitude=23.2,Longitude=113.41}]},new(){Points=[new(){Latitude=23.2,Longitude=113.42},new(){Latitude=23.2,Longitude=113.41001}]}]};
            var parsed=new HikingGuide.Application.TrackFiles.TrackImportResult();parsed.Tracks.Add(conflict);var importService=new HikingGuide.Application.Guides.ProjectImportService();var session=HikingGuide.Application.Guides.ProjectImportSession.Create(parsed,"公里测试.kml",new HikingProject());var import=new ImportWindow(session,new HikingProject(),importService){Owner=owner};_=owner.Dispatcher.BeginInvoke(()=>import.ShowDialog());await Wait(()=>Task.FromResult(import.IsVisible),"导入窗口未显示");var routeCombo=FavoritesPanel.Descendants<ComboBox>(import).First(c=>c.Items.Count==3&&c.Items[0]?.ToString()=="请选择…");checks["有间隙导入必须选择性质"]=routeCombo.IsEnabled&&routeCombo.SelectedIndex==0;routeCombo.SelectedItem="一条行进路线";await CaptureT10ClientAsync(import,Path.Combine(folder,"kilometer-import.png"));FavoritesPanel.Descendants<Button>(import).Single(b=>b.Content?.ToString()=="确认导入").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>Task.FromResult(!import.IsVisible),"导入选择未确认");checks["导入保存单线路线且默认关闭"]=conflict.Metadata.GetValueOrDefault(TrackKilometerService.RouteKindKey)=="route"&&!TrackKilometerService.IsEnabled(conflict);
            var mergeWindow=new TrackMergeWindow(project,false){Owner=owner};mergeWindow.Show();checks["合并默认记作合集"]=!mergeWindow.IsSingleJourney;mergeWindow.Close();
            var directionWindow=new KilometerDirectionWindow(conflict){Owner=owner};directionWindow.Show();await CaptureT10ClientAsync(directionWindow,Path.Combine(folder,"kilometer-directions.png"));var directionRows=FavoritesPanel.Descendants<DataGrid>(directionWindow).First();checks["方向相冲窗口列出各段起终点"]=directionRows.Items.Count==2&&directionRows.Columns.Count==4;directionWindow.Close();
            var editor=new ProjectItemEditorWindow(merged){Owner=owner};editor.Show();checks["属性有显示设置"]=FavoritesPanel.Descendants<CheckBox>(editor).Any(c=>c.Content?.ToString()=="显示轨迹公里数标识"&&c.Visibility==Visibility.Visible&&c.IsChecked==true);await CaptureT10ClientAsync(editor,Path.Combine(folder,"kilometer-properties.png"));editor.Close();
            var unsupported=new ProjectItemEditorWindow(collection);checks["集合属性不提供入口"]=!FavoritesPanel.Descendants<CheckBox>(unsupported).Any(c=>c.Content?.ToString()=="显示轨迹公里数标识"&&c.Visibility==Visibility.Visible);
            var settings=new KilometerSettingsWindow([new(project,3,2)],main.ApplyKilometers){Owner=owner};settings.Show();await CaptureT10ClientAsync(settings,Path.Combine(folder,"kilometer-settings.png"));settings.Close();
            await JS("window.hikingGuideKilometerTest.fit();true");await Task.Delay(1500);await Wait(async()=>(await JS("window.hikingGuideKilometerTest.rendered()")).GetInt32()>0,"公里标识未实际渲染");await main.Map.CapturePreviewAsync(Path.Combine(folder,"kilometers-2d.png"));checks["2D实际渲染"]=true;
            MapLayerVisibility.SetTrackVisible(project.Settings.Map,collection.Id,false);await main.Map.RefreshVisibilityAsync();await Task.Delay(500);var mapView=FavoritesPanel.Descendants<MapView>(owner).First();var location=await JS($"window.hikingGuideT10Test.location('{merged.Id:D}')");var x=location.GetProperty("x").GetDouble();var y=location.GetProperty("y").GetDouble();foreach(var type in new[]{"mousePressed","mouseReleased"})await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button="right",buttons=type=="mousePressed"?2:0,clickCount=1}));await Wait(()=>Task.FromResult(mapView.ActiveTrackMenuForAutomation?.IsOpen==true),"地图轨迹右击菜单未打开");diagnostics.Add(new{MenuTrack=(mapView.ActiveTrackMenuForAutomation!.Tag as HikingGuide.Application.Maps.MapEventMessage)?.TrackId,Headers=mapView.ActiveTrackMenuForAutomation.Items.OfType<MenuItem>().Select(i=>i.Header?.ToString()).ToArray()});checks["地图右击有公里显示隐藏入口"]=mapView.ActiveTrackMenuForAutomation!.Items.OfType<MenuItem>().Count(i=>i.Header?.ToString()?.Contains("公里数标识")==true)==2;mapView.ActiveTrackMenuForAutomation.IsOpen=false;
            var path=Path.Combine(folder,"saved.hikeproj");await new ProjectFileService().SaveAsync(project,path,false);var restored=await new ProjectFileService().LoadAsync(path);checks["项目保存重开保留显示选项"]=restored.Tracks.Count(TrackKilometerService.IsEnabled)==2;checks["轨迹节点人工内容不变"]=baseline==Baseline();
            var dem=DemCatalog.Databases.First(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id,Exaggeration=1.5,DefaultPitch=55};await main.Map.ApplyGlobalMapLayersAsync(true);await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean(),"3D未就绪");await JS("window.hikingGuide3DTest.camera(55,120);true");await Task.Delay(1500);checks["3D公里标识实际渲染"]=(await JS("window.hikingGuideKilometerTest.rendered()")).GetInt32()>0;await main.Map.CapturePreviewAsync(Path.Combine(folder,"kilometers-3d.png"));
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
        static IEnumerable<NavigationNode> Flatten(NavigationNode node){yield return node;foreach(var child in node.Children)foreach(var value in Flatten(child))yield return value;}
    }
}
