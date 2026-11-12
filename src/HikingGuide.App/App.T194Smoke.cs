using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Favorites;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Application.Routing;
using HikingGuide.Infrastructure.Favorites;
using HikingGuide.Infrastructure.Media;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.App.Services;
using Microsoft.Web.WebView2.Wpf;
namespace HikingGuide.App;
public partial class App
{
    private sealed class T194Http(byte[] bytes):HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Count++;var content=new ByteArrayContent(bytes);content.Headers.ContentType=new("image/png");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});}
    }
    private sealed class T194Router:IRoutingProvider
    {
        public int Delay{get;set;}
        public async Task<RoutingResult> RouteFootShortestAsync(GeoCoordinate start,GeoCoordinate end,CancellationToken cancellationToken=default){if(Delay>0)await Task.Delay(Delay,cancellationToken);return new([start,end],"foot-shortest","隔离测试");}
    }
    private async Task<bool> RunT194SmokeAsync(MainWindow owner,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;var folder=Path.GetDirectoryName(output)!;var opened=new List<Window>();var oldDownloader=_externalMediaDownloadService;
        try
        {
            async Task Wait(Func<bool> condition,string message){for(int i=0;i<400;i++){if(condition())return;await Task.Delay(50);}throw new InvalidOperationException(message);}
            await Wait(()=>main.Map.IsReady,"地图未就绪");
            var coordinate=new GeoCoordinate(24.43783414734684,113.2284787648979);var point=new FavoritePoint{Name="收藏点功能验证",Latitude=coordinate.Latitude,Longitude=coordinate.Longitude,SourceReference="保留原来源"};
            var collections=await Favorites.CollectionsAsync();var editor=new FavoriteEditorWindow(Favorites,collections,point){Owner=owner};opened.Add(editor);editor.Show();await Task.Delay(100);await editor.DemTaskForAutomation;
            var first=DemCatalog.Databases.First();checks["新建默认首套DEM且自动填海拔"]=ReferenceEquals(editor.DemForAutomation.SelectedItem,first)&&double.TryParse(editor.ElevationForAutomation.Text,out var initial)&&double.IsFinite(initial);
            checks["窗口宽度为原来四分之三"]=Math.Abs(editor.Width-450)<1;
            var buttons=FavoritesPanel.Descendants<Button>(editor).Select(b=>b.Content?.ToString()??"").ToArray();
            checks["移除DEM按钮及旧图片链接编辑按钮"]=!buttons.Contains("从DEM读取…")&&!buttons.Contains("修改所选图片链接…");
            checks["五项图片按钮按指定顺序排列"]=new[]{"添加本地图片","添加网络图片链接并下载","查看图片","移除所选图片","修改图片说明"}.Select(label=>Array.IndexOf(buttons,label)).SequenceEqual(new[]{"添加本地图片","添加网络图片链接并下载","查看图片","移除所选图片","修改图片说明"}.Select(label=>Array.IndexOf(buttons,label)).OrderBy(i=>i))&&buttons.Contains("添加网络图片链接并下载");
            if(DemCatalog.Databases.Count>1){editor.DemForAutomation.SelectedIndex=1;editor.ElevationForAutomation.Text="777.77";await editor.DemTaskForAutomation;checks["手工海拔不被过期DEM结果覆盖"]=editor.ElevationForAutomation.Text=="777.77";editor.DemForAutomation.SelectedIndex=0;await editor.DemTaskForAutomation;checks["切换DEM自动重新读取"]=editor.ElevationForAutomation.Text!="777.77";}
            await CaptureT10ClientAsync(editor,Path.Combine(folder,"66-favorite-editor.png"));
            byte[] image;using(var resource=GetResourceStream(new Uri("pack://application:,,,/Assets/AppLogo.png"))!.Stream){using var memory=new MemoryStream();await resource.CopyToAsync(memory);image=memory.ToArray();}
            var handler=new T194Http(image);using var downloader=new HttpExternalMediaDownloadService(new ApplicationDataPaths(),testHandler:handler,resolver:(_,_)=>Task.FromResult(new[]{IPAddress.Parse("93.184.216.34")}),imageValidator:new WpfImageDecodeValidator());_externalMediaDownloadService=downloader;
            await editor.AddNetworkImageAsync("https://favorite-test.example/photo.png");await editor.RunAutomationSaveAsync();var detail=(await Favorites.GetAsync(point.Id))!;
            var downloaded=await Favorites.ReadOriginalAsync(detail.Blobs.Single());
            checks["网络链接确认后下载并随收藏点保存本地原图"]=handler.Count==1&&detail.Attachments.Single().MediaBlobId.HasValue&&!detail.Attachments.Single().OnlineOnly&&image.SequenceEqual(downloaded);
            checks["隐藏来源字段但保留既有来源元数据"]=detail.Point.SourceReference=="保留原来源";
            async Task DoubleClick(FavoriteEditorWindow target,string label){var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};int ticks=0;timer.Tick+=(_,_)=>{var openedViewer=Windows.OfType<FavoriteImageViewerWindow>().FirstOrDefault(w=>w.Owner==target);if(openedViewer?.PhotoReadyForAutomation==true||++ticks>100){timer.Stop();completion.TrySetResult(openedViewer?.PhotoReadyForAutomation==true);openedViewer?.Close();}};timer.Start();target.DoubleClickImageForAutomation();checks[label]=await completion.Task;}
            var edit=new FavoriteEditorWindow(Favorites,collections,detail.Point with{ElevationMeters=888.88,ElevationSourceKind=FavoriteElevationSourceKind.Manual,ElevationSourceReference=null},detail){Owner=owner};opened.Add(edit);edit.Show();await Task.Delay(150);checks["编辑已有收藏不自动覆盖固定海拔"]=edit.ElevationForAutomation.Text.StartsWith("888.88");await DoubleClick(edit,"双击本地图片打开统一查看器");edit.Close();
            var networkPoint=point with{Id=Guid.NewGuid(),Name="网络图片查看验证"};var networkEditor=new FavoriteEditorWindow(Favorites,collections,networkPoint,onlineReferences:[new(Guid.NewGuid(),networkPoint.Id,null,"网络示例",null,0,DateTimeOffset.UtcNow,"https://favorite-test.example/online.png",true)]){Owner=owner};opened.Add(networkEditor);networkEditor.Show();await Task.Delay(150);await DoubleClick(networkEditor,"双击网络引用自动下载并查看");networkEditor.Close();
            var viewer=new FavoriteImageViewerWindow(FavoriteImageEntries.Create(Favorites,detail,detail.Attachments)){Owner=owner};opened.Add(viewer);viewer.Show();await Wait(()=>viewer.PhotoReadyForAutomation,"图片未显示");var zoom=viewer.ZoomForAutomation;viewer.ZoomForTest(2);checks["收藏点查看器支持放大"]=viewer.ZoomForAutomation>zoom;viewer.ZoomForTest(.5);checks["收藏点查看器支持缩小"]=Math.Abs(viewer.ZoomForAutomation-zoom)<.001;viewer.RotateForTest(90);checks["收藏点查看器支持旋转"]=viewer.RotationForAutomation==90;await CaptureT10ClientAsync(viewer,Path.Combine(folder,"67-favorite-image-viewer.png"));viewer.Close();
            var now=DateTimeOffset.UtcNow;var collection=await Favorites.SaveCollectionAsync(new(Guid.NewGuid(),"测试收藏夹",1,now,now));var second=new FavoritePoint{Name="第二个收藏点",Latitude=24.44,Longitude=113.23,CollectionId=collection.Id};await Favorites.SaveAsync(second);collections=await Favorites.CollectionsAsync();var points=await Favorites.SearchAsync();
            var export=new FavoriteExportWindow(collections,points,[],null){Owner=owner};opened.Add(export);export.Show();export.SelectForAutomation(FavoriteExportScope.Collections,FavoriteDefaults.UnclassifiedId,collection.Id);await Task.Delay(200);checks["导出面板支持多选收藏夹"]=export.Selection!.CollectionIds.Count==2;await CaptureT10ClientAsync(export,Path.Combine(folder,"68-favorite-export.png"));export.SelectForAutomation(FavoriteExportScope.Points,point.Id,second.Id);checks["导出面板支持跨收藏夹多选收藏点"]=export.Selection!.PointIds.Count==2;export.Close();
            var project=new HikingProject{Name="地图交互隔离验证"};var projectPath=Path.Combine(folder,"interactions.hikeproj");await new ProjectFileService().SaveAsync(project,projectPath,false);await main.OpenForAutomationAsync(projectPath);var router=new T194Router();main.ConfigureRouting(router);
            var web=FavoritesPanel.Descendants<WebView2>(owner).First(w=>w.Source?.Host=="hikingguide.local");async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            await JS("window.hikingGuideTrackSceneTest.camera(16,0,0,[113.23,24.44]);document.querySelector('canvas.maplibregl-canvas').focus();true");await Task.Delay(100);
            var before=await JS("window.hikingGuideT10Test.inspect().camera");await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyDown\",\"key\":\"w\",\"code\":\"KeyW\",\"windowsVirtualKeyCode\":87}");await Task.Delay(200);var after=await JS("window.hikingGuideT10Test.inspect().camera");checks["W键上移地图保持缩放与方向"]=after.GetProperty("latitude").GetDouble()>before.GetProperty("latitude").GetDouble()&&after.GetProperty("zoom").GetDouble()==before.GetProperty("zoom").GetDouble();
            await JS("window.favoriteTestInput=document.createElement('input');document.body.appendChild(window.favoriteTestInput);window.favoriteTestInput.focus();true");var at=await JS("window.hikingGuideT10Test.inspect().camera");await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyDown\",\"key\":\"w\",\"code\":\"KeyW\",\"windowsVirtualKeyCode\":87}");await Task.Delay(100);checks["文本输入不误触WASD移动"]=(await JS("window.hikingGuideT10Test.inspect().camera")).GetRawText()==at.GetRawText();await JS("window.favoriteTestInput.remove();true");
            async Task RightClick(){await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent","{\"type\":\"mousePressed\",\"x\":160,\"y\":140,\"button\":\"right\",\"buttons\":2,\"clickCount\":1}");await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent","{\"type\":\"mouseReleased\",\"x\":160,\"y\":140,\"button\":\"right\",\"buttons\":0,\"clickCount\":1}");}
            foreach(var (label,command) in new[]{("BRouter",main.StartBRouterPlanCommand),("手绘",main.StartManualPlanCommand),("混合",main.StartMixedPlanCommand)}){
                command.Execute(null);await Wait(()=>main.IsRoutePlanningActive,"规划未开始");await main.Map.RunP7PlanningClickSmokeAsync(24.44,113.23);await Wait(()=>main.RoutePlanningPoints.Count==1&&main.UndoPlanningPointCommand.CanExecute(null),"起点未加入");await main.Map.RunP7PlanningClickSmokeAsync(24.441,113.231);await Wait(()=>main.RoutePlanningPoints.Count==2&&main.UndoPlanningPointCommand.CanExecute(null),"终点未加入");await RightClick();await Wait(()=>main.RoutePlanningPoints.Count==1,"右键未撤销终点");await RightClick();await Wait(()=>main.RoutePlanningPoints.Count==0,"右键未撤销起点");checks[label+"规划右键逐点撤销且不弹标注菜单"]=true;main.CancelPlanningCommand.Execute(null);await Wait(()=>!main.IsRoutePlanningActive,"规划未结束");
            }
            router.Delay=3000;main.StartBRouterPlanCommand.Execute(null);await Wait(()=>main.IsRoutePlanningActive,"BRouter规划未开始");await main.Map.RunP7PlanningClickSmokeAsync(24.44,113.23);await Wait(()=>main.RoutePlanningPoints.Count==1&&main.UndoPlanningPointCommand.CanExecute(null),"起点未加入");await main.Map.RunP7PlanningClickSmokeAsync(24.441,113.231);await Wait(()=>!main.UndoPlanningPointCommand.CanExecute(null),"未进入后台计算");await RightClick();await Wait(()=>main.UndoPlanningPointCommand.CanExecute(null)&&main.RoutePlanningStatus.Contains("取消"),"右键未取消后台规划点");checks["后台计算中右键只撤销待加入点不误删前一点"]=main.RoutePlanningPoints.Count==1;await RightClick();await Wait(()=>main.RoutePlanningPoints.Count==0,"起点未撤销");main.CancelPlanningCommand.Execute(null);
        }catch(Exception e){error=e.ToString();}
        finally{foreach(var w in opened.Where(w=>w.IsVisible))w.Close();_externalMediaDownloadService=oldDownloader;}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
