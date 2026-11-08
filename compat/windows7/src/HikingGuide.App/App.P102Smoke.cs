using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Favorites;
using HikingGuide.Application.Guides;
using HikingGuide.Application.Projects;
using HikingGuide.Application.Maps;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
using SkiaSharp;

namespace HikingGuide.App;

public partial class App
{
    private async Task RunP102SmokeAsync(MainWindow window,MainViewModel main,WebView2 web,string folder,Dictionary<string,bool> checks,List<object> diagnostics)
    {
        var generator=TerrainTiles??throw new InvalidOperationException("地形运行库未初始化。");
        async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
        Task<JsonElement> State()=>JS("window.hikingGuide3DTest.inspect()");
        async Task MapCommand(string type,object payload)
        {
            var context=await JS("window.hikingGuideT10Test.inspect()");
            var command=new{protocolVersion=1,type,projectId=context.GetProperty("projectId").GetString(),revision=context.GetProperty("revision").GetInt64(),payload};
            await JS($"window.chrome.webview.dispatchEvent(new MessageEvent('message',{{data:{JsonSerializer.Serialize(command)}}}))");
        }
        async Task Mouse(string type,double x,double y,string button="none",int buttons=0)
        {await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button,buttons,clickCount=type=="mouseMoved"?0:1}));}
        async Task DragAt(JsonElement point,string check)
        {
            var count=main.Map.TerrainBlockedEditCount;var px=point.GetProperty("x").GetDouble();var py=point.GetProperty("y").GetDouble();
            await Mouse("mouseMoved",px,py);await Mouse("mousePressed",px,py,"left",1);await Mouse("mouseMoved",px+8,py+6,"left",1);await Mouse("mouseReleased",px+8,py+6,"left");await Task.Delay(120);
            checks[check]=main.Map.TerrainBlockedEditCount>count;
        }
        async Task Wait(Func<JsonElement,bool> predicate,string message,int attempts=900)
        {
            for(var i=0;i<attempts;i++){var state=await State();if(predicate(state))return;await Task.Delay(100);}
            throw new TimeoutException(message+";状态="+(await State()).GetRawText()+";提示="+main.Map.MapStatusText);
        }
        var choices=await generator.Sampler.AvailableAsync();
        var fathom=choices.First(c=>c.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
        var alos=choices.First(c=>c.Id!=fathom.Id&&DemCatalog.GetEntries(c.Id).Any(e=>e.Format==HikingGuide.Infrastructure.Elevation.DemRasterFormat.GeoTiff));
        var original2D=GlobalSettingsService.Current.OfflineTerrain with{DemDatabaseId=fathom.Id,ContoursVisible=true,HillshadeVisible=true};
        GlobalSettingsService.Current.OfflineTerrain=original2D;
        var options=new Terrain3DSettings{Enabled=true,TerrainDemDatabaseId=fathom.Id,ShowContours=true,ShowHillshade=true};
        var configuration=generator.Configure(options);var z=configuration.MaxNativeZoom;
        var x=(int)((113.32+180)/360*(1<<z));var y=(int)((1-Math.Asinh(Math.Tan(24.42*Math.PI/180))/Math.PI)/2*(1<<z));
        var url=configuration.TerrainUrlTemplate!.Replace("{z}",z.ToString()).Replace("{x}",x.ToString()).Replace("{y}",y.ToString());
        var watch=System.Diagnostics.Stopwatch.StartNew();var startCpu=System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        var heartbeat=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};var heartbeatWatch=System.Diagnostics.Stopwatch.StartNew();var delays=new List<double>();double lastBeat=0;
        heartbeat.Tick+=(_,_)=>{var now=heartbeatWatch.Elapsed.TotalMilliseconds;delays.Add(now-lastBeat);lastBeat=now;};heartbeat.Start();
        var png=await generator.GetAsync(new Uri(url));var missMs=watch.Elapsed.TotalMilliseconds;
        heartbeat.Stop();diagnostics.Add(new{TerrainGenerationMaxUiTickMs=delays.Count>0?delays.Max():0});
        await File.WriteAllBytesAsync(Path.Combine(folder,"P102-TerrainRGB.png"),png);
        using(var bitmap=SKBitmap.Decode(png))
        {
            checks["P102真实TerrainPNG为512无损像素"]=bitmap.Width==512&&bitmap.Height==512;
            var errors=new List<double>();
            foreach(var row in new[]{32,128,256,480})foreach(var col in new[]{32,128,256,480})
            {var coord=TerrainRgb.Coordinate(z,x,y,col+.5,row+.5);var expected=DemProvider!.SampleDetailed(fathom.Id,new GeoCoordinate(coord.Latitude,coord.Longitude)).ElevationMeters!.Value;var c=bitmap.GetPixel(col,row);errors.Add(Math.Abs(TerrainRgb.Decode(c.Red,c.Green,c.Blue)-expected));}
            checks["P102真实PNG解码与P8采样误差不超过0点1米"]=errors.Max()<=.10001;
            diagnostics.Add(new{TerrainPixelMaxError=errors.Max(),NativeZoom=z,Dem=fathom.Summary});
        }
        var generated=generator.GeneratedTiles;watch.Restart();await generator.GetAsync(new Uri(url));var hitMs=watch.Elapsed.TotalMilliseconds;
        checks["P102重复地形瓦片复用缓存"]=generator.GeneratedTiles==generated;
        checks["P102三十米DEM不原生生成Z18"]=z<18;
        var storage=new ProjectFileService();var realPath=@"F:\徒步规划项目\船底顶.hikeproj";
        var realHash=Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(await File.ReadAllBytesAsync(realPath)));
        var loadedReal=await storage.LoadAsync(realPath);var target=loadedReal.Tracks.Single(t=>t.Name=="船底顶-联A西A");
        var routePoints=target.Segments.SelectMany(s=>s.Points).ToArray();var midpoint=routePoints.Length/2;var routeCenter=routePoints[midpoint].Coordinate;
        var real=new HikingProject{Name="船底顶：3D真实轨迹隔离验证"};real.AddTrack(target,false);real.MainGuideTrackId=target.Id;
        var structure=new GuideStructureService();
        foreach(var index in new[]{midpoint-20,midpoint+20}){var createdNode=new GuideNode{Name="山脊攻略节点",TrackId=target.Id,OriginalCoordinate=routePoints[index].Coordinate};structure.SnapNode(createdNode,target);real.GuideNodes.Add(createdNode);}
        GuideStructureService.ReplaceSegments(real,structure.RebuildSegments(real));
        real.Waypoints.Add(new(){Name="普通标注验证",AssociatedTrackId=target.Id,Latitude=routeCenter.Latitude-.0006,Longitude=routeCenter.Longitude});
        real.Settings.Map.Center=routeCenter;real.Settings.Map.Zoom=15;real.Settings.Map.Pitch=0;
        var copy=Path.Combine(folder,"P102真实船底顶副本.hikeproj");await storage.SaveAsync(real,copy,false);await main.OpenForAutomationAsync(copy);await main.WaitForMapRefreshForAutomationAsync();
        await JS($"window.hikingGuide3DTest.at({JsonSerializer.Serialize(new[]{routeCenter.Longitude,routeCenter.Latitude})},15)");await main.Map.SelectObjectAsync(target);await Task.Delay(700);
        var favorite=new FavoritePoint{Name="山脊收藏点",Latitude=routeCenter.Latitude,Longitude=routeCenter.Longitude+.0008};
        var favorite2=new FavoritePoint{Name="备选观景点",Latitude=routeCenter.Latitude+.0007,Longitude=routeCenter.Longitude+.0007};
        await Favorites.SaveAsync(favorite,[]);await Favorites.SaveAsync(favorite2,[]);await window.FavoritesContent.RefreshAsync();FavoriteSelection.SelectFavorite(favorite);
        var project=main.MapContextProject!;var pointsBefore=JsonSerializer.Serialize(project.Tracks.SelectMany(t=>t.Segments).SelectMany(s=>s.Points).Select(p=>new{p.Id,p.Latitude,p.Longitude,p.SourceElevation}));
        target=project.Tracks.Single(t=>t.Id==target.Id);routePoints=target.Segments.SelectMany(s=>s.Points).ToArray();
        var manualBefore=JsonSerializer.Serialize(project.GuideData.Manual);var dirtyBefore=project.IsDirty;
        string MarkerCoordinates()=>JsonSerializer.Serialize(new{waypoints=project.Waypoints.Select(w=>new{w.Id,w.Latitude,w.Longitude}),nodes=project.GuideNodes.Select(n=>new{n.Id,n.OriginalCoordinate,n.SnappedCoordinate})});
        var markerCoordinatesBefore=MarkerCoordinates();
        watch.Restart();await main.Map.SaveTerrainSettingsAsync(options);await Wait(s=>s.GetProperty("effective").GetBoolean(),"3D地形未就绪");var viewportMs=watch.Elapsed.TotalMilliseconds;
        await Wait(s=>s.GetProperty("labelCount").GetInt32()>0,"3D屏幕朝向标签未加载");await Task.Delay(900);
        checks["P1023D有效状态与按钮一致"]=main.Map.EffectiveTerrainState&&main.Map.TerrainToggleLabel=="2D";
        checks["P102进入3D不覆盖2D派生设置"]=GlobalSettingsService.Current.OfflineTerrain==original2D;
        checks["P102地形设置不修改项目Dirty"]=project.IsDirty==dirtyBefore;
        var state=await State();checks["P102默认3D倾角55且中心缩放不变"]=Math.Abs(state.GetProperty("camera").GetProperty("pitch").GetDouble()-55)<.01&&Math.Abs(state.GetProperty("camera").GetProperty("zoom").GetDouble()-15)<.001;
        checks["P1023D数值标签采用屏幕朝向符号"]=!(await JS("window.hikingGuide3DTest.layers()")).EnumerateArray().All(l=>l.GetProperty("id").GetString()!="dem-contour-labels");
        var favoriteState=await JS("window.hikingGuideFavoriteTest.inspect()");
        checks["P102收藏点紫色金色发光与名称均存在"]=favoriteState.GetProperty("goldImage").GetBoolean()&&favoriteState.GetProperty("glowLayer").GetBoolean()&&favoriteState.GetProperty("labels").GetInt32()>=2;
        var mapView=FavoritesPanel.Descendants<MapView>(window).Single();
        var bridge=T10ManualField<HikingGuide.App.Services.WebViewMapBridge>(mapView,"_bridge");var contours=T10ManualField<HikingGuide.App.Services.DemTerrainTiles>(bridge,"_terrainTiles");
        var contourSignature=T10ManualField<string>(contours,"_signature");var cz=15;var cx=(int)((routeCenter.Longitude+180)/360*(1<<cz));var cy=(int)((1-Math.Asinh(Math.Tan(routeCenter.Latitude*Math.PI/180))/Math.PI)/2*(1<<cz));
        var activeContourSettings=T10ManualField<OfflineTerrainSettings>(contours,"_settings");
        checks["P102主等高线在3D仍用同源DEM与屏幕朝向标签"]=activeContourSettings.MajorContoursEnabled&&activeContourSettings.MajorContourIntervalMeters==50&&activeContourSettings.ScreenAlignedLabels&&activeContourSettings.DemDatabaseId==fathom.Id;
        var numbers=JsonSerializer.Deserialize<JsonElement>(await contours.GetAsync(new Uri($"https://hikingguide-terrain.local/{contourSignature}/labels/{cz}/{cx}/{cy}.json")));
        var contourErrors=numbers.EnumerateArray().Take(100).Select(label=>Math.Abs(DemProvider!.SampleDetailed(fathom.Id,new(label.GetProperty("latitude").GetDouble(),label.GetProperty("longitude").GetDouble())).ElevationMeters!.Value-label.GetProperty("elevation").GetDouble())).ToArray();
        checks["P102真实等高线高度与同源DEM一致"]=contourErrors.Length>0&&contourErrors.Max()<10;diagnostics.Add(new{ContourSamples=contourErrors.Length,ContourMaxError=contourErrors.Length>0?contourErrors.Max():-1});
        await File.WriteAllTextAsync(Path.Combine(folder,"P102-图层顺序.json"),(await JS("window.hikingGuide3DTest.layers()")).GetRawText());
        var originalSources=GlobalSettingsService.Current.MapSources.ToList();var originalStack=GlobalSettingsService.Current.MapLayerStack!;
        var baseSource=originalSources.Single(s=>s.Id==originalStack.BaseSourceId);var overlay1=baseSource.DeepCopy();overlay1.Id=Guid.NewGuid();overlay1.Name="P102叠加层1";overlay1.Usage=MapSourceUsage.Both;
        var overlay2=baseSource.DeepCopy();overlay2.Id=Guid.NewGuid();overlay2.Name="P102叠加层2";overlay2.Usage=MapSourceUsage.Both;
        GlobalSettingsService.Current.MapSources.AddRange([overlay1,overlay2]);
        GlobalSettingsService.Current.MapLayerStack=originalStack with{Overlay1SourceId=overlay1.Id,Overlay2SourceId=overlay2.Id,Overlay1Visible=true,Overlay2Visible=true,Overlay1Opacity=.1,Overlay2Opacity=.1};
        GlobalSettingsService.Current.OfflineTerrain=original2D with{HillshadeOpacity=1};GlobalSettingsService.NotifyMapSettingsChanged();await Task.Delay(1200);
        var rasterState=await JS("window.hikingGuideRasterTest.inspect()");
        checks["P102三层Raster与百分百阴影仍保持3D"]=rasterState.GetProperty("slots").EnumerateArray().Count(s=>s.GetProperty("visible").GetString()=="visible")==3&&main.Map.EffectiveTerrainState;
        await CaptureT10ClientAsync(window,Path.Combine(folder,"P102-三层Raster与阴影100.png"));
        GlobalSettingsService.Current.MapSources=originalSources;GlobalSettingsService.Current.MapLayerStack=originalStack;GlobalSettingsService.Current.OfflineTerrain=original2D;GlobalSettingsService.NotifyMapSettingsChanged();await Task.Delay(500);
        await CaptureT10ClientAsync(window,Path.Combine(folder,"P102-真实船底顶-55度.png"));
        var settings=new Terrain3DWindow(main.Map){Owner=window};settings.Show();
        for(var i=0;i<200&&settings.DemBox.Items.Count==0;i++)await Task.Delay(100);
        await T10ManualLayoutAsync(settings);
        async Task SubmitExaggeration(string text)
        {
            settings.ExaggerationInput.Text=text;
            settings.ExaggerationInput.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,Environment.TickCount,settings.ExaggerationInput,settings.Pitch)
                {RoutedEvent=System.Windows.Input.Keyboard.LostKeyboardFocusEvent});
            await Task.Delay(500);
        }
        await SubmitExaggeration("1.37");
        checks["P102输入1点37四舍五入1点4同步实际地形"]=Math.Abs(settings.Exaggeration.Value-1.4)<1e-10&&
            Math.Abs((await State()).GetProperty("terrain").GetProperty("exaggeration").GetDouble()-1.4)<1e-10&&GlobalSettingsService.Current.Terrain3D?.Exaggeration==1.4;
        var savedDecimal=await File.ReadAllTextAsync(GlobalSettingsService.SettingsFilePath);
        checks["P102一位小数夸张写入设置"]=savedDecimal.Contains("1.4",StringComparison.Ordinal);
        await SubmitExaggeration("3.5");
        checks["P102超出范围不截断或覆盖已保存小数"]=GlobalSettingsService.Current.Terrain3D?.Exaggeration==1.4&&settings.StatusText.Text.Contains("0.5",StringComparison.Ordinal);
        await SubmitExaggeration("非数字");
        checks["P102非法输入不覆盖已保存小数"]=GlobalSettingsService.Current.Terrain3D?.Exaggeration==1.4;
        await SubmitExaggeration("1.37");
        await CaptureT10ClientAsync(settings,Path.Combine(folder,"P102-3D设置.png"));settings.Close();
        var reopenedSettings=new Terrain3DWindow(main.Map){Owner=window};reopenedSettings.Show();
        for(var i=0;i<200&&reopenedSettings.DemBox.Items.Count==0;i++)await Task.Delay(100);
        await T10ManualLayoutAsync(reopenedSettings);
        checks["P102重开窗口仍显示1点4"]=reopenedSettings.ExaggerationInput.Text=="1.4"&&Math.Abs(reopenedSettings.Exaggeration.Value-1.4)<1e-10;
        reopenedSettings.Close();
        await RunTerrainNavigationSmokeAsync(window,main,web,folder,checks,diagnostics);
        var cameraResults=new List<object>();
        foreach(var pitch in new[]{0,30,55,75})foreach(var bearing in new[]{0,90,180,270})
        {
            await JS($"window.hikingGuide3DTest.camera({pitch},{bearing})");await Task.Delay(300);
            state=await State();cameraResults.Add(new{Pitch=pitch,Bearing=bearing,Effective=state.GetProperty("effective").GetBoolean(),Elevation=state.GetProperty("elevation").GetDouble()});
            if((pitch==55&&bearing==90)||(pitch==75&&bearing==180))await CaptureT10ClientAsync(window,Path.Combine(folder,$"P102-视角-{pitch}-{bearing}.png"));
        }
        checks["P10216组俯仰方位保持3D"]=cameraResults.Count==16&&cameraResults.All(item=>JsonSerializer.SerializeToElement(item).GetProperty("Effective").GetBoolean());
        foreach(var exaggeration in new[]{1d,1.5,2})
        {
            await main.Map.SaveTerrainSettingsAsync(options with{Exaggeration=exaggeration});await Task.Delay(500);state=await State();
            checks[$"P102夸张{exaggeration}保持真实地形"]=Math.Abs(state.GetProperty("terrain").GetProperty("exaggeration").GetDouble()-exaggeration)<.001;
        }
        await JS("window.p102TerrainSource=window.hikingGuide3DTest.inspect().state.terrainUrlTemplate");
        GlobalSettingsService.Current.OfflineTerrain=original2D with{ContoursVisible=false,HillshadeVisible=false};
        await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(500);
        checks["P102隐藏派生效果不关闭地形或重算Terrain"]=main.Map.EffectiveTerrainState&&(await JS("window.p102TerrainSource===window.hikingGuide3DTest.inspect().state.terrainUrlTemplate")).GetBoolean();
        await main.Map.SaveTerrainSettingsAsync(options);await Task.Delay(500);
        // 高倾角组合已逐一验证；切换尚未缓存的另一DEM前恢复常用俯仰，避免远处视野影响源切换等待。
        await JS("window.hikingGuide3DTest.camera(55,0)");await Task.Delay(300);
        await main.Map.SaveTerrainSettingsAsync(options with{TerrainDemDatabaseId=alos.Id});await Wait(s=>s.GetProperty("effective").GetBoolean()&&s.GetProperty("state").GetProperty("demDatabaseId").GetGuid()==alos.Id,"第二套DEM未就绪");
        checks["P102第二套DEM同步切换山体"]=GlobalSettingsService.Current.OfflineTerrain==original2D;
        diagnostics.Add(new{TerrainOtherDem=alos.Summary});await CaptureT10ClientAsync(window,Path.Combine(folder,"P102-ALOS-3D.png"));
        await main.Map.SaveTerrainSettingsAsync(options);await Wait(s=>s.GetProperty("effective").GetBoolean(),"Fathom地形未恢复");
        // 同一JS事件循环内完成前后快照，排除两次跨进程读取之间的合法新状态更新。
        var staleCheck=await JS("(()=>{const before=JSON.stringify(window.hikingGuide3DTest.inspect().state);window.chrome.webview.dispatchEvent(new MessageEvent('message',{data:{protocolVersion:1,type:'SetTerrainState',payload:{enabled:false,terrainRevision:-1}}}));return before===JSON.stringify(window.hikingGuide3DTest.inspect().state)&&window.hikingGuide3DTest.inspect().terrain!==null;})()");
        checks["P102旧TerrainRevision不能覆盖当前3D"]=staleCheck.GetBoolean();
        await JS("window.hikingGuide3DTest.camera(55,0)");await Task.Delay(500);
        var favoritePoint=await JS($"window.hikingGuideFavoriteTest.point('{favorite.Id:D}')");
        await Mouse("mouseMoved",10,10);await Task.Delay(150);await Mouse("mouseMoved",favoritePoint.GetProperty("x").GetDouble(),favoritePoint.GetProperty("y").GetDouble());await Task.Delay(1100);
        checks["P102实际3D收藏点Hover卡片可读"]= (await JS("window.hikingGuideFavoriteTest.inspect()")).GetProperty("popupCount").GetInt32()>0;
        await DragAt(favoritePoint,"P102实体鼠标事件收藏点拖动仅2D");
        var node=real.GuideNodes[0];var nodePosition=node.SnappedCoordinate??node.OriginalCoordinate;
        var nodePoint=await JS($"window.hikingGuide3DTest.surface({nodePosition.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{nodePosition.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)})");await DragAt(nodePoint,"P102实体鼠标事件攻略节点拖动仅2D");
        var waypoint=real.Waypoints[0];var waypointPoint=await JS($"window.hikingGuide3DTest.surface({waypoint.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{waypoint.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)})");await DragAt(waypointPoint,"P102实体鼠标事件普通标注拖动仅2D");
        foreach(var kind in new[]{"waypoint","guidenode"})
        {
            var count=main.Map.TerrainBlockedEditCount;
            await MapCommand("BeginMarkerAdd",new{guideNode=kind=="guidenode"});
            await Mouse("mousePressed",450,220,"left",1);await Mouse("mouseReleased",450,220,"left");await Task.Delay(100);checks[$"P102实际地图添加{kind}仅2D"]=main.Map.TerrainBlockedEditCount>count;
        }
        main.SelectNavigationNode(new NavigationNode(target.Name??"轨迹",target),focusOnMap:false);main.EnterTrackEditCommand.Execute(null);await Task.Delay(400);
        var session=main.EditingSessionForAutomation;
        if(session is {} editId)
        {
            var point=routePoints[midpoint];var segment=target.Segments.Single(s=>s.Points.Contains(point));
            await main.Map.SetTrackEditHandlesAsync(target,editId,[new TrackEditHandle(point.Id,segment.Id,target.Segments.IndexOf(segment),segment.Points.IndexOf(point),point.Coordinate,true)]);
            await Task.Delay(200);await DragAt(await JS($"window.hikingGuideT10Test.handle('{point.Id:D}')"),"P102实体鼠标事件轨迹点拖动仅2D");
        }
        else checks["P102实体鼠标事件轨迹点拖动仅2D"]=false;
        main.ExitTrackEditCommand.Execute(null);await Task.Delay(200);
        foreach(var manual in new[]{true,false})
        {
            if(manual)main.StartManualPlanCommand.Execute(null);else main.StartBRouterPlanCommand.Execute(null);await Task.Delay(200);
            var count=main.Map.TerrainBlockedEditCount;await Mouse("mousePressed",450,220,"left",1);await Mouse("mouseReleased",450,220,"left");await Task.Delay(100);
            checks[manual?"P102实际手绘规划选点仅2D":"P102实际BRouter规划选点仅2D"]=main.Map.TerrainBlockedEditCount>count;
            T10ManualInvoke(main,"CancelRoutePlan",false);await Task.Delay(100);
        }
        var rightTarget=await JS($"window.hikingGuide3DTest.surface({(routeCenter.Longitude+.002).ToString(System.Globalization.CultureInfo.InvariantCulture)},{(routeCenter.Latitude+.001).ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        MapEventMessage? rightEvent=null;void CaptureRight(object? sender,MapEventMessage e)=>rightEvent=e;main.Map.RightClicked+=CaptureRight;
        await Mouse("mousePressed",rightTarget.GetProperty("x").GetDouble(),rightTarget.GetProperty("y").GetDouble(),"right",2);await Mouse("mouseReleased",rightTarget.GetProperty("x").GetDouble(),rightTarget.GetProperty("y").GetDouble(),"right");await Task.Delay(300);
        main.Map.RightClicked-=CaptureRight;
        checks["P102右击使用地表位置不是相机中心"]=rightEvent?.Latitude is {} rightLat&&Math.Abs(rightLat-routeCenter.Latitude)>.0003&&Math.Abs(rightLat-(routeCenter.Latitude+.001))<.0002;
        if(mapView.ActiveMapMenuForAutomation is {} contextMenu)contextMenu.IsOpen=false;
        foreach(var eventType in new[]{"FavoriteDragStarted","FavoriteDragCompleted","WaypointDragEnded","GuideNodeDragEnded","TrackPointDragEnded","MarkerAddLocationPicked","TrackSnapLocationPicked","TrackEditLineClicked","RoutePlanningPointPicked"})
        {var blocked=main.Map.TerrainBlockedEditCount;await JS($"window.hikingGuide3DTest.postEdit('{eventType}')");await Task.Delay(70);checks[$"P102安全限制{eventType}"]=main.Map.TerrainBlockedEditCount>blocked;}
        var surface=await JS($"window.hikingGuide3DTest.surface({routeCenter.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{routeCenter.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)})");checks["P102地表坐标投影反算正确"]=Math.Abs(surface.GetProperty("latitude").GetDouble()-routeCenter.Latitude)<.0002&&Math.Abs(surface.GetProperty("longitude").GetDouble()-routeCenter.Longitude)<.0002;
        checks["P1023D未写回原始轨迹或人工攻略"]=pointsBefore==JsonSerializer.Serialize(project.Tracks.SelectMany(t=>t.Segments).SelectMany(s=>s.Points).Select(p=>new{p.Id,p.Latitude,p.Longitude,p.SourceElevation}))&&manualBefore==JsonSerializer.Serialize(project.GuideData.Manual);
        checks["P1023D受限操作未改写标注与攻略节点坐标"]=markerCoordinatesBefore==MarkerCoordinates();
        var savedFavorite=(await Favorites.SearchAsync()).Single(p=>p.Id==favorite.Id);checks["P102受限拖动未改写收藏数据库坐标"]=savedFavorite.Latitude==favorite.Latitude&&savedFavorite.Longitude==favorite.Longitude;
        state=await State();var before2D=state.GetProperty("camera");await main.Map.SaveTerrainSettingsAsync(options with{Enabled=false});await Wait(s=>!s.GetProperty("state").GetProperty("enabled").GetBoolean(),"未退出3D");await Task.Delay(400);state=await State();
        checks["P102退出3D保留中心方位缩放并回0倾角"]=before2D.GetProperty("center").GetRawText()==state.GetProperty("camera").GetProperty("center").GetRawText()&&Math.Abs(state.GetProperty("camera").GetProperty("pitch").GetDouble())<.001;
        checks["P102退出3D恢复原等高线与阴影配置"]=GlobalSettingsService.Current.OfflineTerrain==original2D;
        await main.Map.SaveTerrainSettingsAsync(options);await Wait(s=>s.GetProperty("effective").GetBoolean(),"恢复3D失败");
        var projects=T10ManualField<IProjectService>(main,"_projects");
        var third=new HikingProject{Name="P102第三项目"};third.Settings.Map.Center=routeCenter;third.Settings.Map.Zoom=15;
        var thirdPath=Path.Combine(folder,"P102第三项目.hikeproj");await storage.SaveAsync(third,thirdPath,false);await main.OpenForAutomationAsync(thirdPath);await main.WaitForMapRefreshForAutomationAsync();
        foreach(var context in projects.OpenProjects.Take(3).ToArray()){projects.Activate(context.ContextId);main.RefreshWorkspaceForAutomation();await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(350);checks[$"P102切换项目{context.Project.Name}不关闭3D"]=GlobalSettingsService.Current.Terrain3D==options&&(await State()).GetProperty("state").GetProperty("enabled").GetBoolean();}
        var reloadCamera=(await State()).GetProperty("camera");main.Map.ReloadCommand.Execute(null);
        for(var i=0;i<200&&!main.Map.IsReady;i++)await Task.Delay(100);await Wait(s=>s.GetProperty("effective").GetBoolean(),"地图重载未恢复3D");await Task.Delay(600);
        checks["P102地图重载保留3D与相机"]=Math.Abs((await State()).GetProperty("camera").GetProperty("zoom").GetDouble()-reloadCamera.GetProperty("zoom").GetDouble())<.01;
        while(projects.OpenProjects.Count>0)
        {
            // 隔离演示项目无需保留测试修改；必须走真实关闭入口以同步树和面板。
            if(projects.CurrentProject is {} closing)closing.MarkSaved();
            await main.CloseForAutomationAsync();
        }
        await main.WaitForMapRefreshForAutomationAsync();await Task.Delay(800);
        checks["P102无项目时3D与收藏图层继续存在"]=main.MapContextProject is null&&main.Map.EffectiveTerrainState&&(await JS("window.hikingGuideFavoriteTest.inspect()")).GetProperty("labels").GetInt32()>=2;
        await CaptureT10ClientAsync(window,Path.Combine(folder,"P102-无项目3D.png"));
        var configPath=Path.Combine(Environment.GetEnvironmentVariable("HIKINGGUIDE_APPDATA_DIR")!,"dem-databases.json");var configBytes=await File.ReadAllBytesAsync(configPath);
        try
        {
            var mounts=DemCatalog.Databases.Select(d=>d.Id==fathom.Id?d with{RootPath=Path.Combine(folder,"模拟断开-不存在的DEM目录")}:d).ToArray();
            await File.WriteAllTextAsync(configPath,JsonSerializer.Serialize(mounts));await DemCatalog.LoadAsync();
            for(var i=0;i<100&&main.Map.EffectiveTerrainState;i++)await Task.Delay(100);
            checks["P102真实挂载失联检测安全回2D"]=!main.Map.EffectiveTerrainState&&GlobalSettingsService.Current.Terrain3D?.Enabled==false;
        }
        finally{await File.WriteAllBytesAsync(configPath,configBytes);await DemCatalog.LoadAsync();}
        await main.Map.SaveTerrainSettingsAsync(options);await Wait(s=>s.GetProperty("effective").GetBoolean(),"恢复连接后3D未恢复");
        await main.Map.ClearTerrainCacheAsync();await Wait(s=>s.GetProperty("effective").GetBoolean(),"清理缓存后未恢复");
        checks["P102清3D缓存不删除DEM及2D缓存"]=Directory.Exists(DemCatalog.Get(fathom.Id).RootPath)&&Directory.Exists(Services.DemTerrainTiles.CacheRoot);
        var beforeClearOffline=GlobalSettingsService.Current.OfflineTerrain;var beforeTerrainOptions=GlobalSettingsService.Current.Terrain3D;
        var rgbHashes=Directory.GetFiles(Services.TerrainTileGenerator.CacheRoot,"*.tile",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(File.ReadAllBytes(p))));
        var clearManager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};clearManager.Show();clearManager.LibraryTabs.SelectedIndex=1;
        await clearManager.ClearTerrainCacheAsync(true);await clearManager.ClearTerrainCacheAsync(false);
        checks["P102通过管理器清除派生缓存不退出3D或改配置"]=main.Map.EffectiveTerrainState&&(await State()).GetProperty("terrain").ValueKind!=JsonValueKind.Null&&GlobalSettingsService.Current.OfflineTerrain==beforeClearOffline&&GlobalSettingsService.Current.Terrain3D==beforeTerrainOptions;
        checks["P102清缓存后按钮恢复可用"]=clearManager.ClearContoursButton.IsEnabled&&clearManager.ClearShadeButton.IsEnabled;
        checks["P102清等高线阴影不删除3D地形缓存"]=rgbHashes.Count>0&&rgbHashes.All(p=>File.Exists(p.Key)&&Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(File.ReadAllBytes(p.Key)))==p.Value);
        clearManager.Close();
        await JS("window.hikingGuide3DTest.fail()");await Task.Delay(700);checks["P102失联安全回退2D且保留DEM身份"]=!main.Map.EffectiveTerrainState&&GlobalSettingsService.Current.Terrain3D is {Enabled:false,TerrainDemDatabaseId:var kept}&&kept==fathom.Id;
        checks["P102真实项目文件未改变"]=realHash==Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(await File.ReadAllBytesAsync(realPath)));
        diagnostics.Add(new{TerrainMissMs=missMs,TerrainHitMs=hitMs,FirstViewportMs=viewportMs,CameraResults=cameraResults,ProcessCpuSeconds=(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime-startCpu).TotalSeconds,WorkingSetMb=System.Diagnostics.Process.GetCurrentProcess().WorkingSet64/1048576d});
    }
}
