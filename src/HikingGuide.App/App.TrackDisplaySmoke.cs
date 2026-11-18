using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;
using Track=HikingGuide.Core.Models.Track;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunTrackDisplaySmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var errors=new List<string>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            async Task Wait(Func<Task<bool>> condition,string message){for(int i=0;i<900;i++){if(await condition())return;await Task.Delay(100);}throw new TimeoutException(message+"；"+main.Map.MapStatusText);}
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");
            window.ProjectPanelColumn.Width=new GridLength(240);window.PropertiesPanelColumn.Width=new GridLength(180);
            var view=FavoritesPanel.Descendants<MapView>(window).First();var bridge=view.BridgeForAutomation!;
            bridge.EventReceived+=(_,e)=>{if(e.Type=="MapError")errors.Add(e.Message??"未知地图错误");};
            using var bitmap=new SkiaSharp.SKBitmap(256,256);using(var canvas=new SkiaSharp.SKCanvas(bitmap))canvas.Clear(new SkiaSharp.SKColor(231,238,223));
            using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var png=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,90);var bytes=png.ToArray();
            bridge.TileFetchForTest=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
            var source=new MapSourceDefinition{Name="轨迹显示操作演示底图",UrlTemplate="https://track-display.invalid/{z}/{x}/{y}.png"};
            GlobalSettingsService.Current.MapSources.Add(source);GlobalSettingsService.Current.DefaultMapSourceId=source.Id;GlobalSettingsService.Current.MapLayerStack=new(){BaseSourceId=source.Id};
            GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{ContoursVisible=false,HillshadeVisible=false};
            GlobalSettingsService.Current.OsmRoads.Enabled=false;GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            Track Make(string name,double lat,string color)=>new(){Name=name,DisplayColor=color,Segments=[new(){Points=Enumerable.Range(0,16).Select(i=>new TrackPoint{Latitude=lat+Math.Sin(i*.5)*.00022,Longitude=113.257+i*.0005}).ToList()}]};
            var p=new HikingProject{Name="轨迹显示操作示例"};var a=Make("主攻略路线",24.4815,"#195CAD");var b=Make("普通路线",24.4802,"#D78523");var c=Make("备选路线",24.4828,"#185935");foreach(var t in new[]{a,b,c}){t.RecalculateDistances();p.AddTrack(t,false);}p.MainGuideTrackId=a.Id;p.Waypoints.Add(new(){Name="起点标注",AssociatedTrackId=c.Id,Latitude=c.StartPoint!.Latitude,Longitude=c.StartPoint.Longitude});
            p.Settings.Map.Center=new(24.4815,113.261);p.Settings.Map.Zoom=16;
            var path=Path.Combine(folder,"轨迹显示示例.hikeproj");await new ProjectFileService().SaveAsync(p,path,false);var original=File.ReadAllBytes(path);
            await main.OpenForAutomationAsync(path);p=main.MapContextProject!;a=p.Tracks[0];b=p.Tracks[1];c=p.Tracks[2];await main.WaitForMapRefreshForAutomationAsync();
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string s)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(s));
            async Task<JsonElement> State()=>await JS("window.hikingGuideTrackDisplayTest.inspect()");
            await Wait(async()=>(await State()).TryGetProperty("lines",out _),"轨迹样式模块未加载");
            var menu=FavoritesPanel.Descendants<Menu>(window).Single().Items.OfType<MenuItem>().Single(m=>m.Header?.ToString()=="设置");checks["设置菜单提供轨迹显示入口"]=menu.Items.OfType<MenuItem>().Any(m=>m.Header?.ToString()=="轨迹显示…");
            await Task.Delay(900);p.Settings.Map.Zoom=16;await main.Map.RefreshVisibilityAsync();await Task.Delay(600);
            var data=T10ManualField<object>(main.Map,"_lastDisplayData");var beforeCamera=(await JS("window.hikingGuideT10Test.inspect().camera")).GetRawText();
            GlobalSettingsService.Current.TrackDisplay=new(){SelectedColor="#A000CC",Selected=new(){Width=10,Opacity=.6,LineStyle="dashdot",OutlineWidth=1,Endpoints="both",EndpointMinZoom=0},MainGuide=new(){Width=6,Opacity=.7,LineStyle="dash",Endpoints="both",EndpointMinZoom=12},Ordinary=new(){Width=3,Opacity=.5,LineStyle="dot",Endpoints="both",EndpointMinZoom=12}};
            await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync();
            await Wait(async()=>(await State()).GetProperty("lines").GetProperty("mainGuide").GetProperty("width").GetDouble()==6,"全局样式未应用");
            var state=await State();checks["三类线宽透明度与线型分别应用"]=state.GetProperty("lines").GetProperty("ordinary").GetProperty("opacity").GetDouble()==.5&&state.GetProperty("lines").GetProperty("selected").GetProperty("color").GetString()=="#A000CC"&&state.GetProperty("lines").GetProperty("mainGuide").GetProperty("dash").GetArrayLength()==2;
            await File.WriteAllTextAsync(Path.Combine(folder,"style-only-update.json"),JsonSerializer.Serialize(new {SameData=ReferenceEquals(data,T10ManualField<object>(main.Map,"_lastDisplayData")),Before=beforeCamera,After=(await JS("window.hikingGuideT10Test.inspect().camera")).GetRawText()}));
            checks["修改样式不重建轨迹几何或移动相机"]=ReferenceEquals(data,T10ManualField<object>(main.Map,"_lastDisplayData"))&&beforeCamera==(await JS("window.hikingGuideT10Test.inspect().camera")).GetRawText();
            checks["主攻略和普通轨迹保留原颜色且无重复覆盖"]=a.DisplayColor=="#195CAD"&&b.DisplayColor=="#D78523"&&state.GetProperty("guideOverlay").GetString()=="none";
            main.UpdateProjectItemSelection(T10ManualNodes(main.NavigationNodes).Single(n=>ReferenceEquals(n.Model,b)),ModifierKeys.None,focusOnMap:false);
            await Wait(async()=>(await State()).GetProperty("filters").GetProperty("selected").GetRawText().Contains(b.Id.ToString()),"选中样式未同步");
            main.UpdateProjectItemSelection(T10ManualNodes(main.NavigationNodes).Single(n=>ReferenceEquals(n.Model,c)),ModifierKeys.Control,focusOnMap:false);await Task.Delay(400);
            state=await State();checks["多选轨迹共用选中颜色且原始线条仍保留"]=state.GetProperty("filters").GetProperty("selected").GetRawText().Contains(c.Id.ToString())&&!state.GetProperty("lineFilters").GetProperty("ordinary").GetRawText().Contains("!");
            var layerOrder=(await JS("window.hikingGuideRasterTest.inspect().layers")).EnumerateArray().Select(l=>l.GetProperty("id").GetString()).ToArray();
            checks["圆内文字不会被同位置的起点普通标注遮挡"]=Array.IndexOf(layerOrder,"track-endpoint-labels-selected")>Array.IndexOf(layerOrder,"waypoints");
            checks["每条轨迹只生成原始起终点而非分块端点"]=state.GetProperty("endpoints").GetProperty("features").GetArrayLength()==6;
            await CaptureT10ClientAsync(window,Path.Combine(folder,"track-display-map.png"));
            var dialog=new TrackDisplaySettingsWindow(GlobalSettingsService){Owner=window};dialog.Show();await T10ManualLayoutAsync(dialog);
            var tabs=FavoritesPanel.Descendants<TabControl>(dialog).Single();checks["面板提供选中主攻略普通三类设置"]=tabs.Items.Count==3;
            checks["选中轨迹提供颜色选择器"]=FavoritesPanel.Descendants<Button>(dialog).Any(v=>v.Content?.ToString()?.StartsWith("#")==true);
            await CaptureT10ClientAsync(dialog,Path.Combine(folder,"track-display-settings-custom.png"));
            tabs.SelectedIndex=1;await T10ManualLayoutAsync(dialog);checks["主攻略轨迹不提供颜色设置"]=!FavoritesPanel.Descendants<Button>(dialog).Any(v=>v.Content?.ToString()?.StartsWith("#")==true);
            tabs.SelectedIndex=2;await T10ManualLayoutAsync(dialog);checks["普通轨迹不提供颜色设置"]=!FavoritesPanel.Descendants<Button>(dialog).Any(v=>v.Content?.ToString()?.StartsWith("#")==true);
            var boxes=FavoritesPanel.Descendants<TextBox>(dialog).ToArray();boxes[0].Text="4.5";
            var save=FavoritesPanel.Descendants<Button>(dialog).Single(v=>v.Content?.ToString()=="应用并保存");save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(async()=>(await State()).GetProperty("lines").GetProperty("ordinary").GetProperty("width").GetDouble()==4.5,"面板保存未生效");checks["通过实际面板保存小数线宽并即时应用"]=GlobalSettingsService.Current.TrackDisplay.Ordinary.Width==4.5;
            var reset=FavoritesPanel.Descendants<Button>(dialog).Single(v=>v.Content?.ToString()=="恢复默认样式");reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));checks["恢复默认只修改草稿直到应用"]=GlobalSettingsService.Current.TrackDisplay.Ordinary.Width==4.5;
            tabs.SelectedIndex=0;await T10ManualLayoutAsync(dialog);await CaptureT10ClientAsync(dialog,Path.Combine(folder,"track-display-settings.png"));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(async()=>(await State()).GetProperty("settings").GetProperty("selectedColor").GetString()=="#FD9700","当前预设未成为默认样式");
            checks["恢复默认采用用户当前整套预设"]=GlobalSettingsService.Current.TrackDisplay is {SelectedColor:"#FD9700",Selected:{Width:8,EndpointMinZoom:9,LineStyle:"dashdot"},MainGuide:{Width:7,Opacity:1,EndpointSize:6},Ordinary:{Width:5,EndpointSize:5}};
            dialog.Close();await Task.Delay(700);await JS("window.hikingGuideTrackSceneTest.camera(16,0,0,[113.261,24.4815]);true");await Task.Delay(600);await CaptureT10ClientAsync(window,Path.Combine(folder,"track-display-map.png"));
            var mapImage=Path.Combine(folder,"overlay-pixel-check.png");await CaptureT10ElementAsync(web,Brushes.White,mapImage);
            using(var pixels=SkiaSharp.SKBitmap.Decode(mapImage))
            {
                var green=0;var orange=0;
                var screen=await JS("({w:document.querySelector('canvas.maplibregl-canvas').clientWidth,h:document.querySelector('canvas.maplibregl-canvas').clientHeight})");
                var sx=pixels.Width/screen.GetProperty("w").GetDouble();var sy=pixels.Height/screen.GetProperty("h").GetDouble();
                for(var i=3;i<12;i++)for(var sample=0;sample<10;sample++)
                {
                    var f=sample/10d;var x=c.Segments[0].Points[i];var y=c.Segments[0].Points[i+1];
                    var point=await JS(FormattableString.Invariant($"window.hikingGuide3DTest.surface({x.Longitude+(y.Longitude-x.Longitude)*f:R},{x.Latitude+(y.Latitude-x.Latitude)*f:R})"));
                    var px=(int)Math.Round(point.GetProperty("x").GetDouble()*sx);var py=(int)Math.Round(point.GetProperty("y").GetDouble()*sy);
                    if(px<0||py<0||px>=pixels.Width||py>=pixels.Height)continue;var color=pixels.GetPixel(px,py);
                    if(color.Red<100&&color.Green>color.Red*1.4&&color.Green>color.Blue*1.15)green++;
                    if(color.Red>170&&color.Green>70&&color.Green<200&&color.Blue<70)orange++;
                }
                checks["实际画面中橙色点划线的空隙透出原绿色轨迹"]=green>=5&&orange>=5;
                await File.WriteAllTextAsync(Path.Combine(folder,"overlay-pixels.json"),JsonSerializer.Serialize(new{green,orange}));
            }
            var styles=GlobalSettingsService.Current.TrackDisplay;
            GlobalSettingsService.Current.TrackDisplay=styles with{Selected=styles.Selected with{EndpointMinZoom=18,Arrows=false},MainGuide=styles.MainGuide with{Arrows=false},Ordinary=styles.Ordinary with{Arrows=false}};
            await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync();await Task.Delay(400);
            state=await State();checks["起终点显示层级可配置且箭头可全部关闭"]=state.GetProperty("lines").GetProperty("selected").GetProperty("endpointMinZoom").GetInt32()==18&&state.GetProperty("lines").GetProperty("selected").GetProperty("endpointRendered").GetInt32()==0;
            GlobalSettingsService.Current.TrackDisplay=styles;await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync();
            var arrowStates=new List<JsonElement>();
            foreach(var z in new[]{14,16,18,20,22})
            {
                await JS($"window.hikingGuideTrackSceneTest.camera({z},0,0,[113.261,24.481333503451]);true");await Task.Delay(450);
                var arrows=await JS("window.hikingGuideTrackSceneTest.inspect().arrowLayout");arrowStates.Add(arrows);
                var expected=z switch{14=>96,16=>108,18=>128,20=>148,_=>168};
                checks[$"Z{z}箭头按屏幕间距适度分布"]=arrows.GetProperty("spacingPixels").GetInt32()==expected;
                checks[$"Z{z}双尖角放大且尖角突出轨迹"]=arrows.GetProperty("categories").EnumerateObject().All(v=>Math.Abs(Math.Max(10,v.Value.GetProperty("lineWidth").GetDouble()+4)-v.Value.GetProperty("height").GetDouble())<1e-8);
                if(z is 18 or 20)await CaptureT10ClientAsync(window,Path.Combine(folder,$"track-arrow-z{z}.png"));
            }
            await File.WriteAllTextAsync(Path.Combine(folder,"arrow-layouts.json"),JsonSerializer.Serialize(arrowStates));
            var dem=DemCatalog.Databases.First(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            GlobalSettingsService.Current.TerrainCamera=new(24.4815,113.261,16,35,50);GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id,Exaggeration=1.5};
            await main.Map.ApplyGlobalMapLayersAsync(true);await JS("window.hikingGuideTrackSceneTest.camera(16,35,50,[113.261,24.4815]);true");
            await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean(),"真实3D地形未就绪");await Task.Delay(800);
            state=await State();checks["真实DEM三维下保留样式与起终点"]=state.GetProperty("lines").GetProperty("selected").GetProperty("width").GetDouble()==styles.Selected.Width&&state.GetProperty("endpoints").GetProperty("features").GetArrayLength()==6;
            var arrow3d=await JS("window.hikingGuideTrackSceneTest.inspect().arrowLayout");
            checks["三维俯仰增加间距而不改变贴线尺寸"]=arrow3d.GetProperty("spacingPixels").GetInt32()>108&&arrow3d.GetProperty("categories").GetProperty("selectedMainGuide").GetProperty("height").GetDouble()>15;
            await CaptureT10ClientAsync(window,Path.Combine(folder,"track-display-3d.png"));
            GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D with{Enabled=false};await main.Map.ApplyGlobalMapLayersAsync(true);
            checks["样式修改不覆盖原项目轨迹或分析内容"]=File.ReadAllBytes(path).SequenceEqual(original);
            checks["地图未出现样式渲染错误"]=errors.Count==0;
            await File.WriteAllTextAsync(Path.Combine(folder,"render-errors.json"),JsonSerializer.Serialize(errors));
        }
        catch(Exception e){error=e.ToString();try{await CaptureT10ClientAsync(window,Path.Combine(folder,"failure.png"));}catch{}}
        var passed=error is null&&checks.Values.All(x=>x);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
