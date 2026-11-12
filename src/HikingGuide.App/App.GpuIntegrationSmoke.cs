using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using Microsoft.Web.WebView2.Wpf;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunGpuIntegrationSmokeAsync(MainWindow owner,MainViewModel main,string output)
    {
        var folder=Path.GetDirectoryName(output)!;var checks=new Dictionary<string,bool>();var diagnostics=new List<object>();var samples=new List<object>();string? error=null;
        var web=FavoritesPanel.Descendants<WebView2>(owner).First();
        async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
        async Task Wait(Func<Task<bool>> test,string reason,int ms=120000){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){if(await test())return;await Task.Delay(100);}throw new TimeoutException(reason);}
        Services.WebViewMapBridge? bridge=null;
        async Task Capture(string name){using var stream=File.Create(Path.Combine(folder,name+".png"));await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream);}
        try
        {
            foreach(var suite in Environment.GetEnvironmentVariable("HIKINGGUIDE_GPU_RECOVERY_ONLY")=="1"?Array.Empty<string>():new[]{"gpu","continuity","tracks","favorites"})
            {
                if(suite!="continuity"&&suite!="gpu")
                {
                    GlobalSettingsService.Current.Terrain3D=(GlobalSettingsService.Current.Terrain3D??new()) with{Enabled=false};
                    await main.Map.ApplyGlobalMapLayersAsync(true);await Wait(()=>Task.FromResult(!main.Map.EffectiveTerrainState),"独立回归前未回到2D");
                }
                var target=Path.Combine(folder,suite+"-result.json");
                var suitePassed=suite=="gpu"?await RunGpuContourSmokeAsync(owner,main,target):suite=="continuity"?await RunT195SmokeAsync(owner,main,target):suite=="tracks"?await RunTrackSceneSmokeAsync(owner,main,target):await RunT194SmokeAsync(owner,main,target);
                checks[suite+"-原有真实WPF回归"]=suitePassed;if(!suitePassed)throw new InvalidOperationException(suite+"回归未通过，见独立报告");
            }
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");
            web=FavoritesPanel.Descendants<WebView2>(owner).First(w=>w.CoreWebView2 is not null&&w.Source?.Host=="hikingguide.local");
            diagnostics.Add(new{WebView2=web.CoreWebView2.Environment.BrowserVersionString,OS=Environment.OSVersion.VersionString,LogicalProcessors=Environment.ProcessorCount});
            bridge=FavoritesPanel.Descendants<MapView>(owner).First().BridgeForAutomation!;
            using var surface=SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(256,256));surface.Canvas.Clear(new SkiaSharp.SKColor(211,228,192));using var png=surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();
            bridge.TileFetchForTest=(request,token)=>{var content=new ByteArrayContent(bytes);content.Headers.ContentType=new("image/png");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});};
            var databases=DemCatalog.Databases.Where(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready).Take(2).ToArray();
            if(databases.Length<2)throw new InvalidOperationException("至少两套真实DEM");
            var selected=databases.FirstOrDefault(d=>d.Name.Contains("Fathom"))??databases[0];
            GlobalSettingsService.Current.Terrain3D=new(){TerrainDemDatabaseId=selected.Id,Enabled=false};
            GlobalSettingsService.Current.OfflineTerrain=new(){DemDatabaseId=selected.Id,ContoursVisible=true,HillshadeVisible=true,ContourOpacity=.8};
            GlobalSettingsService.Current.OsmRoads.Enabled=true;await main.Map.ApplyGlobalMapLayersAsync(true);
            await JS("window.hikingGuide3DTest.at([113.269571,24.464423],16);true");
            async Task Ready()=>await Wait(async()=>{var state=await JS("window.hikingGuideGpuTest.inspect()");return state.TryGetProperty("tiles",out var tiles)&&tiles.GetInt32()>0&&state.GetProperty("pending").GetInt32()==0&&state.GetProperty("queued").GetInt32()==0;},"GPU数据未就绪");
            await Ready();await Task.Delay(400);
            var before=await JS("window.hikingGuideGpuTest.inspect()");var generated=TerrainTiles!.SurfaceGeneratedTiles;
            checks["引擎实际固定6.17.0"]=before.GetProperty("version").GetString()=="6.17.0";
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=owner};manager.Show();
            FavoritesPanel.Descendants<TabControl>(manager).Single().SelectedIndex=1;await T10ManualLayoutAsync(manager);
            checks["生成器显示统一DEM且禁止独立另选"]=!manager.TerrainDem.IsEnabled&&Equals(manager.TerrainDem.SelectedValue,selected.Id);
            manager.TerrainLineWidth.Text="1.8";manager.TerrainMajorLineWidth.Text="3.6";manager.TerrainLowInterval.Text="100";manager.TerrainHighInterval.Text="20";manager.TerrainColor.Content="#985A2B";manager.TerrainMajorColor.Content="#704020";
            manager.TerrainSmooth.IsChecked=false;
            var apply=FavoritesPanel.Descendants<Button>(manager).Single(b=>b.Content?.ToString()=="应用并保存等高线生成设置");apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>Task.FromResult(GlobalSettingsService.Current.OfflineTerrain.HighContourIntervalMeters==20),"WPF设置未保存");await Task.Delay(500);await Ready();
            checks["真实WPF可保存等高距线宽颜色"]=GlobalSettingsService.Current.OfflineTerrain is{ContourLineWidth:1.8,MajorContourLineWidth:3.6,ContourColor:"#985A2B",MajorContourColor:"#704020"};
            checks["参数变化不重新采样DEM"]=generated==TerrainTiles.SurfaceGeneratedTiles;
            checks["参数变化不重新编译Shader"]=(await JS("window.hikingGuideGpuTest.inspect().compiles")).GetInt32()==before.GetProperty("compiles").GetInt32();
            checks["WPF可关闭圆滑显示"]=!GlobalSettingsService.Current.OfflineTerrain.SmoothContours;
            manager.TerrainSmooth.IsChecked=true;manager.TerrainHighInterval.Text="10";manager.TerrainLineWidth.Text="1.7";manager.TerrainMajorLineWidth.Text="3.4";apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>Task.FromResult(GlobalSettingsService.Current.OfflineTerrain.SmoothContours&&GlobalSettingsService.Current.OfflineTerrain.HighContourIntervalMeters==10),"圆滑设置没有保存");await Task.Delay(300);
            checks["圆滑切换不读取DEM或编译Shader"]=generated==TerrainTiles.SurfaceGeneratedTiles&&(await JS("window.hikingGuideGpuTest.inspect().compiles")).GetInt32()==before.GetProperty("compiles").GetInt32();
            manager.Height=900;await T10ManualLayoutAsync(manager);await CaptureT10ClientAsync(manager,Path.Combine(folder,"GPU-生成器设置.png"));manager.Close();
            var about=new AboutWindow{Owner=owner};about.Show();await T10ManualLayoutAsync(about);await CaptureT10ClientAsync(about,Path.Combine(folder,"29-about.png"));about.Close();
            var terrainWindow=new Terrain3DWindow(main.Map){Owner=owner};terrainWindow.Show();await Wait(()=>Task.FromResult(terrainWindow.DemBox.Items.Count>0),"统一地形DEM设置未加载");await T10ManualLayoutAsync(terrainWindow);await CaptureT10ClientAsync(terrainWindow,Path.Combine(folder,"85-terrain-settings.png"));terrainWindow.Close();
            await Capture("GPU-正式2D");
            var loadWatch=Stopwatch.StartNew();await main.Map.ClearOfflineTerrainCacheAsync(true);await Ready();loadWatch.Stop();
            diagnostics.Add(new{Operation="清除等高线磁盘及内存缓存后，当前视野全部GPU请求完成（含WPF桥接与100ms轮询）",Milliseconds=loadWatch.Elapsed.TotalMilliseconds});
            checks["清除GPU缓存后重建且未删除DEM"]=TerrainTiles.SurfaceGeneratedTiles>generated&&Directory.Exists(selected.RootPath);
            var contextRestored=(await JS("window.hikingGuideGpuTest.inspect().contextRestored")).GetInt32();
            var canLose=(await JS("window.hikingGuideGpuTest.contextLoss()")).GetBoolean();
            if(canLose){await Task.Delay(1000);await Wait(async()=>{var state=await JS("window.hikingGuideGpuTest.inspect()");return state.GetProperty("contextRestored").GetInt32()>contextRestored&&state.TryGetProperty("active",out var active)&&active.GetBoolean();},"WebGL上下文未恢复");await Ready();await Capture("GPU-上下文恢复");}
            checks["WebGL上下文丢失恢复"]=canLose;
            loadWatch.Restart();main.Map.ReloadCommand.Execute(null);await Task.Delay(1200);await Wait(()=>Task.FromResult(main.Map.IsReady),"地图重载未恢复");await Ready();loadWatch.Stop();
            diagnostics.Add(new{Operation="保留磁盘缓存后整张地图重载至当前视野GPU请求完成（含固定1.2秒等待、地图初始化和100ms轮询；不是纯磁盘读取）",Milliseconds=loadWatch.Elapsed.TotalMilliseconds});
            checks["地图重载恢复GPU等高线"]=(await JS("window.hikingGuideGpuTest.inspect().active")).GetBoolean();
            for(var i=0;i<8;i++)
            {
                var dem=databases[i%2];var pitch=new[]{30d,55,75,55}[i%4];var bearing=(i%4)*90;
                GlobalSettingsService.Current.Terrain3D=new(){TerrainDemDatabaseId=dem.Id,Enabled=true,DefaultPitch=pitch,Exaggeration=new[]{1d,1.5,2}[i%3]};
                await main.Map.ApplyGlobalMapLayersAsync(true);await JS($"window.hikingGuide3DTest.camera({pitch},{bearing});true");await Ready();
                await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean(),"3D没有保持启用");await Task.Delay(350);
                var state=await JS("window.hikingGuideGpuTest.inspect()");checks[$"真实地形{dem.Name}-Pitch{pitch}-Bearing{bearing}"]=state.GetProperty("active").GetBoolean()&&state.GetProperty("draws3D").GetInt32()>0;
                if(i is 1 or 2)await Capture("GPU-正式3D-"+pitch);
                diagnostics.Add(new{Camera=i,DEM=dem.Name,Pitch=pitch,Bearing=bearing,State=state});
            }
            GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=false};await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(300);
            checks["3D关等高线不关闭地形与阴影"]=!(await JS("window.hikingGuideGpuTest.inspect().active")).GetBoolean()&&(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean();
            GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=true};await main.Map.ApplyGlobalMapLayersAsync(true);await Ready();
            var favorite=new HikingGuide.Core.Favorites.FavoritePoint{Name="GPU回归：地形收藏点",Latitude=24.464423,Longitude=113.269571};
            var favorite2=new HikingGuide.Core.Favorites.FavoritePoint{Name="GPU回归：普通收藏点",Latitude=24.4647,Longitude=113.2700};
            await Favorites.SaveAsync(favorite,[]);await Favorites.SaveAsync(favorite2,[]);await owner.FavoritesContent.RefreshAsync();FavoriteSelection.SelectFavorite(favorite);await Task.Delay(700);
            await JS("window.hikingGuide3DTest.at([113.269571,24.464423],16);window.hikingGuide3DTest.camera(55,0);true");await Ready();await Task.Delay(500);
            var favoriteState=await JS("window.hikingGuideFavoriteTest.inspect()");
            checks["GPU地形保留收藏点金色高亮Glow及名称"]=favoriteState.GetProperty("goldImage").GetBoolean()&&favoriteState.GetProperty("glowLayer").GetBoolean()&&favoriteState.GetProperty("goldIds").GetArrayLength()==1&&favoriteState.GetProperty("labelImages").GetInt32()>=2;
            var favoritePoint=await JS("window.hikingGuide3DTest.surface(113.269571,24.464423)");
            var px=favoritePoint.GetProperty("x").GetDouble();var py=favoritePoint.GetProperty("y").GetDouble();
            async Task Mouse(string type,double x,double y,string button="none",int buttons=0)=>await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button,buttons,clickCount=type=="mouseMoved"?0:1}));
            await Mouse("mouseMoved",px-60,py);await Task.Delay(250);await Mouse("mouseMoved",px,py);
            await Wait(async()=>(await JS("window.hikingGuideFavoriteTest.inspect().popupCount")).GetInt32()>0,"3D收藏点Hover未显示",10000);
            checks["GPU地形收藏点Hover可读"]=(await JS("window.hikingGuideFavoriteTest.inspect().popupText")).GetString()!.Contains(favorite.Name);
            await Capture("GPU-地形收藏点Hover");
            var blocked=main.Map.TerrainBlockedEditCount;await Mouse("mousePressed",px,py,"left",1);await Mouse("mouseMoved",px+10,py+8,"left",1);await Mouse("mouseReleased",px+10,py+8,"left");await Task.Delay(150);
            checks["GPU地形保留收藏点3D编辑安全限制"]=main.Map.TerrainBlockedEditCount>blocked;
            var minutes=int.TryParse(Environment.GetEnvironmentVariable("HIKINGGUIDE_GPU_STABILITY_MINUTES"),out var requested)?requested:0;
            var start=DateTimeOffset.UtcNow;var watch=Stopwatch.StartNew();var startCpu=Process.GetCurrentProcess().TotalProcessorTime;var step=0;
            await File.WriteAllTextAsync(Path.Combine(folder,"stability-start.json"),JsonSerializer.Serialize(new{Start=start,Minutes=minutes}));
            while(watch.Elapsed.TotalMinutes<minutes)
            {
                var tick=watch.Elapsed;var dem=databases[(step/4)%2];var enabled=step%4!=0;
                GlobalSettingsService.Current.Terrain3D=new(){Enabled=enabled,TerrainDemDatabaseId=dem.Id,DefaultPitch=55,Exaggeration=new[]{1d,1.5,2}[step%3]};
                GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=step%7!=0,HillshadeVisible=step%5!=0,HighContourIntervalMeters=step%2==0?10:20};
                await main.Map.ApplyGlobalMapLayersAsync(true);
                var camera=JsonSerializer.Serialize(new{center=new[]{113.269571+Math.Sin(step*.4)*.001,24.464423+Math.Cos(step*.4)*.001},zoom=15.5+(step%4)*.3,pitch=enabled?new[]{30d,55,75}[step%3]:0,bearing=(step*35)%360});
                await JS("window.hikingGuideGpuTest.animate("+camera+")");await Task.Delay(900);
                var process=Process.GetCurrentProcess();process.Refresh();
                var webProcesses=new List<object>();long webMemory=0;
                foreach(var info in web.CoreWebView2.Environment.GetProcessInfos())try{using var p=Process.GetProcessById(info.ProcessId);webMemory+=p.WorkingSet64;webProcesses.Add(new{PID=p.Id,Kind=info.Kind.ToString(),WorkingSet=p.WorkingSet64,CpuSeconds=p.TotalProcessorTime.TotalSeconds});}catch(ArgumentException){}
                var state=await JS("window.hikingGuideGpuTest.inspect()");
                var sample=new{Utc=DateTimeOffset.UtcNow,Elapsed=watch.Elapsed.TotalSeconds,WpfWorkingSet=process.WorkingSet64,WpfPrivateBytes=process.PrivateMemorySize64,WpfCpuSeconds=(process.TotalProcessorTime-startCpu).TotalSeconds,WebMemory=webMemory,WebProcesses=webProcesses,Gpu=state,Metrics=await JS("window.hikingGuideGpuTest.metrics()")};
                samples.Add(sample);await File.AppendAllTextAsync(Path.Combine(folder,"stability-samples.jsonl"),JsonSerializer.Serialize(sample)+Environment.NewLine);
                if(state.GetProperty("tiles").GetInt32()>64)throw new InvalidOperationException("GPU瓦片数量越界");
                step++;var remaining=5000-(watch.Elapsed-tick).TotalMilliseconds;if(remaining>0)await Task.Delay((int)remaining);
            }
            checks["稳定性运行达到请求时长"]=watch.Elapsed.TotalMinutes>=minutes;diagnostics.Add(new{StabilityMinutes=minutes,ActualSeconds=watch.Elapsed.TotalSeconds,Start=start,End=DateTimeOffset.UtcNow,Samples=samples.Count});
            checks["GPU没有累计请求错误"]=(await JS("window.hikingGuideGpuTest.inspect().errors")).GetArrayLength()==0;
            await Capture("GPU-稳定性结束");
        }
        catch(Exception e){error=e.ToString();}
        finally{if(bridge is not null)bridge.TileFetchForTest=null;}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
