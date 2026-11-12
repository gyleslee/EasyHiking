using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using System.Security.Cryptography;
using HikingGuide.App.ViewModels;
using HikingGuide.App.Services;
using HikingGuide.Core.Models;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Media;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunMarkerDisplaySmokeAsync(MainWindow owner,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var diagnostics=new List<object>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        async Task Wait(Func<Task<bool>> condition,string reason){for(var i=0;i<700;i++){if(await condition())return;await Task.Delay(100);}throw new TimeoutException(reason);}
        try
        {
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");
            var web=FavoritesPanel.Descendants<WebView2>(owner).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            using var bitmap=new SkiaSharp.SKBitmap(320,220);using(var c=new SkiaSharp.SKCanvas(bitmap)){c.Clear(SkiaSharp.SKColors.ForestGreen);using var paint=new SkiaSharp.SKPaint{Color=SkiaSharp.SKColors.Gold};c.DrawCircle(160,110,65,paint);}using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var png=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();
            var handler=new T194Http(bytes);using var downloader=new HttpExternalMediaDownloadService(new ApplicationDataPaths(Path.Combine(folder,"ImageDownload")),testHandler:handler,resolver:(_,_)=>Task.FromResult(new[]{IPAddress.Parse("8.8.8.8")}));main.MarkerDownloaderForAutomation=downloader;
            using var baseBitmap=new SkiaSharp.SKBitmap(256,256);using(var baseCanvas=new SkiaSharp.SKCanvas(baseBitmap))baseCanvas.Clear(new SkiaSharp.SKColor(229,234,217));using var baseImage=SkiaSharp.SKImage.FromBitmap(baseBitmap);using var basePng=baseImage.Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var baseBytes=basePng.ToArray();
            var bridge=FavoritesPanel.Descendants<MapView>(owner).First().BridgeForAutomation!;bridge.TileFetchForTest=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(baseBytes)});
            GlobalSettingsService.Current.MapSources=[new(){Name="隔离测试底图",UrlTemplate="https://marker.invalid/{z}/{x}/{y}.png"}];GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=GlobalSettingsService.Current.MapSources[0].Id};GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=false,HillshadeVisible=false};GlobalSettingsService.Current.OsmRoads.Enabled=false;GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            var project=new HikingProject{Name="标注显示隔离测试"};var local=new Waypoint{Name="船底顶摄影点长名称",Latitude=24.4816,Longitude=113.2613};var remote=new Waypoint{Name="网络图片测试标注",Latitude=24.483,Longitude=113.264};var empty=new Waypoint{Name="",Latitude=24.480,Longitude=113.259};project.Waypoints.Add(local);project.Waypoints.Add(remote);project.Waypoints.Add(empty);
            var albumTrack=new Track{Name="图片浏览轨迹",Segments=[new(){Points=[new(){Latitude=24.480,Longitude=113.259},new(){Latitude=24.485,Longitude=113.267}]}]};albumTrack.RecalculateDistances();project.AddTrack(albumTrack,false);local.AssociatedTrackId=remote.AssociatedTrackId=empty.AssociatedTrackId=albumTrack.Id;
            var otherTrack=new Track{Name="其他轨迹",Segments=[new(){Points=[new(){Latitude=24.480,Longitude=113.258},new(){Latitude=24.486,Longitude=113.268}]}]};otherTrack.RecalculateDistances();project.AddTrack(otherTrack,false);
            var nextPhoto=new Waypoint{Name="后方摄影点",Latitude=24.484,Longitude=113.266,AssociatedTrackId=albumTrack.Id};var foreignPhoto=new Waypoint{Name="其他轨迹图片",Latitude=24.4825,Longitude=113.263,AssociatedTrackId=otherTrack.Id};project.Waypoints.Add(nextPhoto);project.Waypoints.Add(foreignPhoto);

            var blob=new MediaBlob{Sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),MimeType="image/png",FileExtension=".png",ByteLength=bytes.Length,Width=320,Height=220,PendingBytes=bytes};blob.StoredPath=$"media/originals/{blob.Id:D}.png";project.MediaBlobs.Add(blob);project.MediaAttachments.Add(new(){OwnerId=local.Id,OwnerType=MediaOwnerType.Waypoint,MediaBlobId=blob.Id,OriginalFileName="本地风景.png"});foreach(var ownerId in new[]{nextPhoto.Id,foreignPhoto.Id})project.MediaAttachments.Add(new(){OwnerId=ownerId,OwnerType=MediaOwnerType.Waypoint,MediaBlobId=blob.Id,OriginalFileName="图片.png"});project.ExternalMediaReferences.Add(new(){OwnerId=remote.Id,OwnerType=MediaOwnerType.Waypoint,Url="https://example.com/hiking-marker.png"});project.ExternalMediaReferences.Add(new(){OwnerId=remote.Id,OwnerType=MediaOwnerType.Waypoint,Url="https://example.com/hiking-marker-second.png",DisplayOrder=1});
            var markerBaseline=JsonSerializer.Serialize(new{project.Waypoints,project.GuideNodes,project.MediaAttachments,project.ExternalMediaReferences,project.MediaBlobs});
            var path=Path.Combine(folder,"markers.hikeproj");await new ProjectFileService().SaveAsync(project,path,false);await main.OpenForAutomationAsync(path);await main.WaitForMapRefreshForAutomationAsync();await main.Map.ApplyGlobalMapLayersAsync(true);await main.Map.UpdateFavoritesAsync([],false);
            await JS("window.hikingGuide3DTest.at([113.2613,24.4816],16);true");
            await Wait(async()=>{var state=await JS("window.hikingGuideMarkerDisplayTest.inspect()");await File.WriteAllTextAsync(Path.Combine(folder,"marker-progress.json"),state.GetRawText());return state.GetProperty("images").GetInt32()>=2;},"导入后缩略图没有自动显示");await Task.Delay(800);
            var s=await JS("window.hikingGuideMarkerDisplayTest.inspect()");diagnostics.Add(s);
            checks["网络缩略图自动下载"]=handler.Count>0;checks["预取不把原图加入项目"]=main.MapContextProject!.MediaAttachments.Count==3;
            var current=main.MapContextProject!;checks["预取保留标注及图片数据"]=markerBaseline==JsonSerializer.Serialize(new{current.Waypoints,current.GuideNodes,current.MediaAttachments,current.ExternalMediaReferences,current.MediaBlobs});
            checks["五字截断及无名称回退"]=s.GetProperty("features").EnumerateArray().Any(f=>f.GetProperty("properties").GetProperty("shortLabel").GetString()=="船底顶摄影…")&&s.GetProperty("features").EnumerateArray().Any(f=>f.GetProperty("properties").GetProperty("shortLabel").GetString()=="");
            checks["有缩略图可见"]=s.GetProperty("renderedThumbnails").GetInt32()>0;
            checks["工具栏显示模式入口可见"]=FavoritesPanel.Descendants<ComboBox>(owner).Any(c=>c.Name=="MarkerDisplayBox"&&c.IsVisible);
            await main.Map.CapturePreviewAsync(Path.Combine(folder,"markers-2d.png"));await CaptureT10ClientAsync(owner,Path.Combine(folder,"marker-display-toolbar.png"));

            var displayMenu=(MenuItem)owner.FindName("MarkerDisplayMenu");var markerMenu=(MenuItem)ItemsControl.ItemsControlFromItemContainer(displayMenu);markerMenu.IsSubmenuOpen=true;displayMenu.IsSubmenuOpen=true;await Task.Delay(300);
            checks["菜单四种方式及默认组合显示"]=displayMenu.Items.OfType<MenuItem>().Count()==4&&displayMenu.Items.OfType<MenuItem>().Single(i=>i.Tag.ToString()=="3").IsChecked;
            ((MenuItem)displayMenu.Items[0]).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));await Task.Delay(200);checks["菜单同步工具栏"]=main.Map.MarkerDisplayMode==0;((MenuItem)displayMenu.Items[3]).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            displayMenu.IsSubmenuOpen=true;await Task.Delay(200);
            if(displayMenu.Template.FindName("PART_Popup",displayMenu) is System.Windows.Controls.Primitives.Popup popup&&popup.Child is System.Windows.FrameworkElement content){var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(content);var shot=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(content.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,System.Windows.Media.PixelFormats.Pbgra32);shot.Render(content);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));using var file=File.Create(Path.Combine(folder,"marker-display-menu.png"));encoder.Save(file);}
            displayMenu.IsSubmenuOpen=false;markerMenu.IsSubmenuOpen=false;
            foreach(var mode in new[]{0,1,2,3}){main.Map.MarkerDisplayMode=mode;await Task.Delay(400);var state=await JS("window.hikingGuideMarkerDisplayTest.inspect()");checks[$"模式{mode}已同步"]=state.GetProperty("mode").GetInt32()==mode&&(mode!=0||state.GetProperty("renderedText").GetInt32()==0&&state.GetProperty("renderedThumbnails").GetInt32()==0);}
            await JS("window.hikingGuide3DTest.at([113.2613,24.4816],11);true");await Task.Delay(600);checks["Z11仅圆点"]=(await JS("window.hikingGuideMarkerDisplayTest.inspect().renderedText")).GetInt32()==0;
            await JS("window.hikingGuide3DTest.at([113.2613,24.4816],13);true");await Task.Delay(600);checks["Z13无缩略图"]=(await JS("window.hikingGuideMarkerDisplayTest.inspect().renderedThumbnails")).GetInt32()==0;
            await JS($"window.hikingGuide3DTest.at([113.2613,24.4816],16);window.hikingGuideMarkerDisplayTest.popup('{remote.Id:D}');true");await Task.Delay(900);checks["弹窗有大缩略图"]=(await JS("!!document.querySelector('.maplibregl-popup-content img')")).GetBoolean();checks["弹窗按钮无需滚动可见"]=(await JS("(()=>{const root=document.querySelector('.maplibregl-popup-content'),b=root.querySelector('button:not(.maplibregl-popup-close-button)');const r=root.getBoundingClientRect(),p=b.getBoundingClientRect();return p.top>=r.top&&p.bottom<=r.bottom&&root.scrollWidth<=root.clientWidth+1;})()")).GetBoolean();await main.Map.CapturePreviewAsync(Path.Combine(folder,"marker-popup.png"));
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent","{\"type\":\"mousePressed\",\"x\":80,\"y\":160,\"button\":\"left\",\"buttons\":1,\"clickCount\":1}");
            for(var step=1;step<=4;step++){await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type="mouseMoved",x=80+step*20,y=160+step*8,button="left",buttons=1}));await Task.Delay(40);}
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent","{\"type\":\"mouseReleased\",\"x\":160,\"y\":192,\"button\":\"left\",\"buttons\":0,\"clickCount\":1}");await Task.Delay(250);
            checks["拖动地图自动隐藏图片弹窗"]=(await JS("document.querySelectorAll('.maplibregl-popup-content').length")).GetInt32()==0;
            main.RunMapMarkerSelectionForAutomation(remote.Id);_=owner.Dispatcher.BeginInvoke(()=>main.OpenMarkerMediaForAutomation());
            await Wait(()=>Task.FromResult(Windows.OfType<FavoriteImageViewerWindow>().Any(w=>w.PhotoReadyForAutomation)),"网络图片未直接打开查看器");var viewer=Windows.OfType<FavoriteImageViewerWindow>().First();
            var pointImages=FavoritesPanel.Descendants<ComboBox>(viewer).First();checks["当前标注多图仍用下拉框"]=pointImages.Items.Count==2;pointImages.SelectedIndex=1;await Wait(()=>Task.FromResult(viewer.PhotoReadyForAutomation&&viewer.Title.Contains("2/2")),"同标注第二张图片未显示");checks["下拉切图不切换标注"]=viewer.GroupTitleForAutomation.StartsWith(remote.Name!);
            viewer.NavigateForAutomation(-1);await Wait(()=>Task.FromResult(viewer.PhotoReadyForAutomation&&viewer.GroupTitleForAutomation.StartsWith(local.Name!)),"未切换到上一有图标注");checks["上一个有图标注且跳过空点"]=!viewer.PreviousEnabledForAutomation;
            viewer.NavigateForAutomation(1);await Wait(()=>Task.FromResult(viewer.PhotoReadyForAutomation&&viewer.GroupTitleForAutomation.StartsWith(remote.Name!)),"未回到网络图片标注");viewer.NavigateForAutomation(1);await Wait(()=>Task.FromResult(viewer.PhotoReadyForAutomation&&viewer.GroupTitleForAutomation.StartsWith(nextPhoto.Name!)),"未到下一个有图标注");checks["下一个有图标注且不跨轨迹"]=!viewer.NextEnabledForAutomation;
            viewer.NavigateForAutomation(-1);await Wait(()=>Task.FromResult(viewer.PhotoReadyForAutomation&&viewer.GroupTitleForAutomation.StartsWith(remote.Name!)),"未切回中间图片标注");
            var choice=FavoritesPanel.Descendants<ComboBox>(viewer).First();checks["图片下拉字号及垂直居中"]=choice.FontSize<=14&&choice.Height>=28&&choice.VerticalContentAlignment==System.Windows.VerticalAlignment.Center;
            var zoom=viewer.ZoomForAutomation;viewer.ZoomForTest(1.25);checks["复用放大缩小旋转"]=viewer.ZoomForAutomation>zoom;viewer.RotateForTest(90);checks["旋转生效"]=viewer.RotationForAutomation==90;checks["查看器有另存为"]=FavoritesPanel.Descendants<Button>(viewer).Any(b=>b.Content?.ToString()=="另存为…");await CaptureT10ClientAsync(viewer,Path.Combine(folder,"marker-image-viewer.png"));viewer.Close();await Task.Delay(300);checks["查看后自动加入项目"]=main.MapContextProject!.MediaAttachments.Any(a=>a.OwnerId==remote.Id);
            var decoded=await Task.Run(()=>FavoriteImageService.Decode(bytes));foreach(var ext in new[]{".jpg",".png"}){var encoded=await Task.Run(()=>ImageSaveService.Encode(decoded,ext));await File.WriteAllBytesAsync(Path.Combine(folder,"另存为测试"+ext),encoded);using var read=SkiaSharp.SKBitmap.Decode(encoded);checks[$"导出{ext}可解码"]=read.Width==320&&read.Height==220;}
            await JS("window.hikingGuideMarkerDisplayTest.closePopup();true");
            var imageA=new FavoriteImageViewItem("收藏图A",token=>Task.FromResult(decoded));var imageB=new FavoriteImageViewItem("收藏图B",token=>Task.FromResult(decoded));var favoriteViewer=new FavoriteImageViewerWindow([imageA,imageB]){Owner=owner};favoriteViewer.Show();await Wait(()=>Task.FromResult(favoriteViewer.PhotoReadyForAutomation),"收藏查看器未就绪");favoriteViewer.NavigateForAutomation(1);await Task.Delay(150);checks["收藏点仍按图片切换"]=favoriteViewer.Title.Contains("2/2")&&favoriteViewer.Title.Contains("收藏图B");favoriteViewer.Close();
            var dem=DemCatalog.Databases.First(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id,Exaggeration=1.5,DefaultPitch=55};await main.Map.ApplyGlobalMapLayersAsync(true);await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean(),"3D未就绪");await JS("window.hikingGuide3DTest.camera(55,120);true");await Task.Delay(1500);checks["3D标注图片仍显示"]=(await JS("window.hikingGuideMarkerDisplayTest.inspect().renderedThumbnails")).GetInt32()>0;await main.Map.CapturePreviewAsync(Path.Combine(folder,"markers-3d.png"));
            var about=new AboutWindow(){Owner=owner};about.Show();await Task.Delay(150);await CaptureT10ClientAsync(about,Path.Combine(folder,"29-about.png"));about.Close();
            main.MarkerDownloaderForAutomation=null;
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
