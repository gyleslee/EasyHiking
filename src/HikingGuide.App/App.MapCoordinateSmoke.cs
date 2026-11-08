using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
using SkiaSharp;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunMapCoordinateSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var diagnostics=new List<object>();string? error=null;
        var folder=Path.GetDirectoryName(Path.GetFullPath(output))!;
        using var listener=new HttpListener();var count=0;
        try
        {
            if(!folder.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase)||!GlobalSettingsService.SettingsFilePath.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("坐标系测试必须使用隔离数据。");
            var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
            byte[] Fixture(int z,int x,int y,bool gcj)
            {
                // 独立已知坐标，原图控制点不是通过待测转换函数生成。
                var control=MapCoordinateTransform.ToTile(gcj?116.41024449916938:116.404,gcj?39.91640428150164:39.915,z);
                using var bitmap=new SKBitmap(256,256);
                using var canvas=new SKCanvas(bitmap);canvas.Clear(gcj?new SKColor(244,244,244):new SKColor(220,235,244));
                using var grid=new SKPaint{Color=new SKColor(120,160,120),StrokeWidth=1};
                for(var i=0;i<=256;i+=32){canvas.DrawLine(i,0,i,256,grid);canvas.DrawLine(0,i,256,i,grid);}
                using var point=new SKPaint{Color=gcj?SKColors.Red:SKColors.Blue,IsAntialias=true};
                canvas.DrawCircle((float)((control.X-x)*256),(float)((control.Y-y)*256),6,point);
                using var bytes=bitmap.Encode(SKEncodedImageFormat.Png,100);return bytes.ToArray();
            }
            var serving=Task.Run(async()=>
            {
                while(listener.IsListening)
                {
                    HttpListenerContext ctx;try{ctx=await listener.GetContextAsync();}catch{break;}
                    try
                    {
                        var parts=ctx.Request.Url!.AbsolutePath.Trim('/').Split('/');Interlocked.Increment(ref count);
                        var bytes=Fixture(int.Parse(parts[1]),int.Parse(parts[2]),int.Parse(parts[3]),parts[0]=="gcj");
                        ctx.Response.ContentType="image/png";ctx.Response.Headers["Cache-Control"]="max-age=3600";
                        await ctx.Response.OutputStream.WriteAsync(bytes);ctx.Response.Close();
                    }
                    catch{ctx.Response.Abort();}
                }
            });
            var wgs=new MapSourceDefinition{Name="WGS控制点底图",UrlTemplate=$"http://127.0.0.1:{port}/wgs/{{z}}/{{x}}/{{y}}",Attribution="隔离控制点测试"};
            var gcj=new MapSourceDefinition{Name="GCJ控制点图源",UrlTemplate=$"http://127.0.0.1:{port}/gcj/{{z}}/{{x}}/{{y}}",CoordinateSystem=MapSourceCoordinateSystem.Gcj02,Attribution="隔离控制点测试"};
            var editor=new MapSourceEditorWindow(gcj.DeepCopy(),MapSourceTemplateService){Owner=window};editor.Show();await T10ManualLayoutAsync(editor);
            checks["GCJ坐标系在编辑器正确回显"]=editor.CoordinateSystemBox.SelectedIndex==1;
            await CaptureT10ClientAsync(editor,Path.Combine(folder,"坐标系-编辑图源.png"));editor.Close();
            var edited=wgs.DeepCopy();var dialog=new MapSourceEditorWindow(edited,MapSourceTemplateService){Owner=window};
            checks["旧图源编辑默认WGS84"]=dialog.CoordinateSystemBox.SelectedIndex==0;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(150)};
            timer.Tick+=(_,_)=>{timer.Stop();dialog.CoordinateSystemBox.SelectedIndex=1;FavoritesPanel.Descendants<Button>(dialog).Single(b=>b.Content?.ToString()=="确定").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
            timer.Start();dialog.ShowDialog();checks["实际确定按钮提交坐标系"]=edited.CoordinateSystem==MapSourceCoordinateSystem.Gcj02;
            GlobalSettingsService.Current.MapSources.AddRange([wgs,gcj]);
            GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=wgs.Id,Overlay1SourceId=gcj.Id,Overlay1Visible=true,Overlay1Opacity=.65};await GlobalSettingsService.SaveAsync();
            var project=new HikingProject{Name="坐标系纠偏隔离验证"};
            var track=new Track{Name="WGS84控制点轨迹",Segments=[new(){Points=[new(){Longitude=116.400,Latitude=39.915,SourceElevation=100},new(){Longitude=116.404,Latitude=39.915,SourceElevation=100},new(){Longitude=116.408,Latitude=39.915,SourceElevation=100}]}]};
            track.RecalculateDistances();project.AddTrack(track);project.Settings.Map.Center=new(39.915,116.404);project.Settings.Map.Zoom=17;
            project.Waypoints.Add(new(){Name="WGS控制点",Longitude=116.404,Latitude=39.915,AssociatedTrackId=track.Id});
            var projectPath=Path.Combine(folder,"坐标系测试.hikeproj");await new ProjectFileService().SaveAsync(project,projectPath,true);await main.OpenForAutomationAsync(projectPath);
            for(var i=0;i<200&&!main.Map.IsReady;i++)await Task.Delay(100);
            await main.WaitForMapRefreshForAutomationAsync();await main.Map.ApplyGlobalMapLayersAsync(true);
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string script)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2!.ExecuteScriptAsync(script));
            async Task WaitTiles(){for(var i=0;i<300;i++){var s=await JS("window.hikingGuideRasterTest.inspect()");if(s.GetProperty("slots").EnumerateArray().Take(2).All(v=>v.GetProperty("loaded").GetBoolean()))return;await Task.Delay(100);}throw new TimeoutException("坐标系图源未加载。");}
            await WaitTiles();var initial=await JS("window.hikingGuideRasterTest.inspect()");var camera=initial.GetProperty("center").GetRawText();
            checks["WGS底图与GCJ叠加层均加载"]=initial.GetProperty("slots")[0].GetProperty("loaded").GetBoolean()&&initial.GetProperty("slots")[1].GetProperty("loaded").GetBoolean();
            await CaptureT10ClientAsync(window,Path.Combine(folder,"坐标系-纠偏控制点对齐.png"));
            var view=FavoritesPanel.Descendants<MapView>(window).Single();var bridge=T10ManualField<WebViewMapBridge>(view,"_bridge");
            var slots=bridge.ConfigureRasterLayers(MapLayerStackService.Build(GlobalSettingsService.Current.MapLayerStack!,GlobalSettingsService.Current.MapSources));
            const int zoom=17;var control=MapCoordinateTransform.ToTile(116.404,39.915,zoom);var tx=(int)control.X;var ty=(int)control.Y;
            var uri=MapTileTemplateParser.BuildTileUrl(slots[1].Templates[0],zoom,tx,ty);
            async Task<byte[]> Fetch(string url)
            {
                await JS("window.crsBytes=null;fetch("+JsonSerializer.Serialize(url)+").then(async r=>{if(!r.ok)throw new Error(r.status);window.crsBytes=Array.from(new Uint8Array(await r.arrayBuffer()));}).catch(e=>window.crsBytes={error:e.message})");
                for(var i=0;i<300;i++){var value=await JS("window.crsBytes");if(value.ValueKind==JsonValueKind.Array)return value.EnumerateArray().Select(v=>v.GetByte()).ToArray();if(value.ValueKind==JsonValueKind.Object)throw new InvalidOperationException(value.ToString());await Task.Delay(100);}throw new TimeoutException("纠偏瓦片请求超时。");
            }
            var corrected=await Fetch(uri);var decoded=RasterTileReprojectionService.Decode(corrected);var index=((int)((control.Y-ty)*256)*256+(int)((control.X-tx)*256))*4;
            checks["浏览器实际返回瓦片的控制点已纠偏到WGS位置"]=decoded.Rgba[index]>220&&decoded.Rgba[index+1]<40;
            var before=count;var repeated=await Fetch(uri);checks["纠偏成品瓦片复用缓存且未重复下载"]=corrected.SequenceEqual(repeated)&&count==before;
            await MapTileDiskCache.Shared(Path.Combine(Path.GetDirectoryName(GlobalSettingsService.SettingsFilePath)!,"MapTileCache")).ClearAsync(gcj.Id);
            await Fetch(uri);checks["清缓存后纠偏图源按需重新下载"]=count>before;
            var tracked=main.MapContextProject!;var coords=JsonSerializer.Serialize(tracked.Tracks.SelectMany(t=>t.Segments).SelectMany(s=>s.Points).Select(p=>new{p.Longitude,p.Latitude}));
            gcj.CoordinateSystem=MapSourceCoordinateSystem.Wgs84;await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync(true);await WaitTiles();
            var changed=await JS("window.hikingGuideRasterTest.inspect()");
            checks["切换坐标系重建当前图源且相机不变"]=changed.GetProperty("center").GetRawText()==camera&&initial.GetProperty("slots")[1].GetProperty("templates").GetRawText()!=changed.GetProperty("slots")[1].GetProperty("templates").GetRawText();
            checks["坐标系切换不修改轨迹和项目内容"]=coords==JsonSerializer.Serialize(tracked.Tracks.SelectMany(t=>t.Segments).SelectMany(s=>s.Points).Select(p=>new{p.Longitude,p.Latitude}))&&!tracked.IsDirty;
            var uncorrectedSlots=bridge.ConfigureRasterLayers(MapLayerStackService.Build(GlobalSettingsService.Current.MapLayerStack!,GlobalSettingsService.Current.MapSources));
            var originalImage=RasterTileReprojectionService.Decode(await Fetch(MapTileTemplateParser.BuildTileUrl(uncorrectedSlots[1].Templates[0],zoom,tx,ty)));
            checks["设为WGS84后原始GCJ图片控制点仍在偏移位置"]=originalImage.Rgba[index+1]>80;
            await CaptureT10ClientAsync(window,Path.Combine(folder,"坐标系-未纠偏对照.png"));
            gcj.CoordinateSystem=MapSourceCoordinateSystem.Gcj02;await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync(true);await WaitTiles();
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();await T10ManualLayoutAsync(manager);
            checks["地图管理列表显示图源坐标系"]=manager.SourcesGrid.Columns.Any(c=>c.Header?.ToString()=="坐标系");
            await CaptureT10ClientAsync(manager,Path.Combine(folder,"坐标系-地图管理.png"));manager.Close();
            diagnostics.Add(new{Requests=count,Initial=initial,CoordinatePoint=new{Longitude=116.404,Latitude=39.915},ExpectedGcjPoint=new{Longitude=116.41024449916938,Latitude=39.91640428150164}});
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_GOOGLE_LIVE_TEST")=="1")
            {
                // 仅隔离验收启用：使用用户原始模板，通过实际地图请求链检查四服务器及绘制。
                const string raw="http://mt{$s}.google.com/vt/lyrs=y&hl=zh-CN&gl=cn&scale=2&x={x}&y={y}&z={z}";
                var google=new MapSourceDefinition{Name="Google混合图原始模板测试",UrlTemplate=raw,Attribution="Google"};
                var googleEditor=new MapSourceEditorWindow(google.DeepCopy(),MapSourceTemplateService){Owner=window};
                checks["Google原始模板在编辑器原样回显"]=googleEditor.UrlText.Text==raw;
                googleEditor.Close();
                GlobalSettingsService.Current.MapSources.Add(google);
                GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=google.Id};
                await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync(true);
                await JS("window.hikingGuide3DTest.at([114.06,22.54],14)");
                var googleSlots=bridge.ConfigureRasterLayers(MapLayerStackService.Build(GlobalSettingsService.Current.MapLayerStack!,GlobalSettingsService.Current.MapSources));
                var tile=MapCoordinateTransform.ToTile(114.06,22.54,14);
                for(var server=0;server<4;server++)
                {
                    // 不同的屏外瓦片避免跨服务器共享缓存掩盖某个服务器的真实响应。
                    var bytes=await Fetch(MapTileTemplateParser.BuildTileUrl(googleSlots[0].Templates[server],14,(int)tile.X+32+server,(int)tile.Y));
                    using var image=SKBitmap.Decode(bytes);
                    checks[$"Google mt{server}经地图请求链返回有效图片"]=image is{Width:>0,Height:>0};
                    await File.WriteAllBytesAsync(Path.Combine(folder,$"Google-mt{server}-瓦片.png"),bytes);
                }
                var loaded=false;
                for(var i=0;i<200;i++){loaded=(await JS("window.hikingGuideRasterTest.inspect()")).GetProperty("slots")[0].GetProperty("loaded").GetBoolean();if(loaded)break;await Task.Delay(100);}
                checks["Google原始模板底图实际加载"]=loaded;
                await main.Map.CapturePreviewAsync(Path.Combine(folder,"Google-原始模板地图.png"));
                diagnostics.Add(new{GoogleOriginalTemplate=raw,GoogleServers=MapTileTemplateParser.Parse(raw).ServerTokens,GoogleMap=await JS("window.hikingGuideRasterTest.inspect()")});
            }
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_CRS_LIVE_TEST")=="1")
            {
                // 实图取用户给出的同一端点01号服务器；四服务器模板展开另有独立验收。
                var live=new MapSourceDefinition{Name="高德GCJ02实图测试",CoordinateSystem=MapSourceCoordinateSystem.Gcj02,
                    UrlTemplate="http://webst01.is.autonavi.com/appmaptile?x={x}&y={y}&z={z}&lang=zh_cn&size=1&scale=1&style=8",Attribution="高德地图"};
                GlobalSettingsService.Current.MapSources.Add(live);
                GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=live.Id};await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync(true);
                await JS("window.hikingGuide3DTest.at([116.391327,39.90748],16)");
                for(var i=0;i<600;i++){var s=await JS("window.hikingGuideRasterTest.inspect()");if(s.GetProperty("slots")[0].GetProperty("loaded").GetBoolean())break;await Task.Delay(100);}
                var state=await JS("window.hikingGuideRasterTest.inspect()");
                checks["高德GCJ02真实图源在浏览器完成纠偏显示"]=state.GetProperty("slots")[0].GetProperty("loaded").GetBoolean()&&state.GetProperty("slots")[0].GetProperty("sourceId").GetString()==live.Id.ToString();
                checks["实图底图选择与地图实际图源一致"]=main.Map.SelectedMapSource?.Id==live.Id&&FavoritesPanel.Descendants<ComboBox>(view).Any(c=>c.SelectedItem is MapSourceDefinition s&&s.Id==live.Id);
                await CaptureT10ClientAsync(window,Path.Combine(folder,"坐标系-高德实图纠偏.png"));
                await main.Map.CapturePreviewAsync(Path.Combine(folder,"高德-纠偏地图.png"));
                var liveSlots=bridge.ConfigureRasterLayers(MapLayerStackService.Build(GlobalSettingsService.Current.MapLayerStack!,GlobalSettingsService.Current.MapSources));
                var actual=MapCoordinateTransform.ToTile(116.391327,39.90748,16);var ax=(int)actual.X;var ay=(int)actual.Y;
                var actualBytes=await Fetch(MapTileTemplateParser.BuildTileUrl(liveSlots[0].Templates[0],16,ax,ay));
                checks["高德实图纠偏输出为有效256像素PNG"]=RasterTileReprojectionService.Decode(actualBytes) is{Width:256,Height:256};
                await File.WriteAllBytesAsync(Path.Combine(folder,"高德-纠偏瓦片.png"),actualBytes);
                var liveCamera=state.GetProperty("center").GetRawText();
                live.CoordinateSystem=MapSourceCoordinateSystem.Wgs84;await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync(true);
                for(var i=0;i<300;i++){var s=await JS("window.hikingGuideRasterTest.inspect()");if(s.GetProperty("slots")[0].GetProperty("loaded").GetBoolean())break;await Task.Delay(100);}
                await CaptureT10ClientAsync(window,Path.Combine(folder,"坐标系-高德实图未纠偏对照.png"));
                await main.Map.CapturePreviewAsync(Path.Combine(folder,"高德-未纠偏地图.png"));
                checks["高德实图对照保持相同WGS视角"]=(await JS("window.hikingGuideRasterTest.inspect()")).GetProperty("center").GetRawText()==liveCamera;
                live.CoordinateSystem=MapSourceCoordinateSystem.Gcj02;await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync(true);
                diagnostics.Add(new{LiveTest="用户给定高德端点01号服务器",State=state,MapCrs="WGS84",SourceCrs="GCJ02"});
            }
        }
        catch(Exception ex){error=ex.ToString();}
        finally{listener.Stop();}
        var passed=error is null&&checks.Count>0&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
