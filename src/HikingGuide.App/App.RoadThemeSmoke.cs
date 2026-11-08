using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using Microsoft.Web.WebView2.Wpf;
using SkiaSharp;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunRoadThemeSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var metrics=new List<object>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            async Task Wait(Func<Task<bool>> condition,string message){for(var i=0;i<1200;i++){if(await condition())return;await Task.Delay(50);}throw new InvalidOperationException(message);}
            await Wait(()=>Task.FromResult(main.Map.IsReady),"地图未就绪");window.Width=1800;window.Height=1000;await T10ManualLayoutAsync(window);
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            GlobalSettingsService.Current.OfflineTerrain=new(){ContoursVisible=false,HillshadeVisible=false};GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            GlobalSettingsService.Current.MapLayerStack=GlobalSettingsService.Current.MapLayerStack! with{BaseOpacity=0,Overlay1Visible=false,Overlay2Visible=false};
            GlobalSettingsService.Current.OsmRoads=new(){Enabled=true,Opacity=1};await main.Map.ApplyGlobalMapLayersAsync(true);
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();T10ManualField<TabControl>(manager,"LibraryTabs").SelectedIndex=2;
            var panel=T10ManualField<OsmRoadSettingsPanel>(manager,"RoadPanel");var grid=T10ManualField<DataGrid>(panel,"StylesGrid");await T10ManualLayoutAsync(manager);
            checks["每种道路类型只保留一行"]=grid.Items.Count==OsmRoadDefaults.CreateStyles().Count&&((IEnumerable<OsmRoadStyle>)grid.ItemsSource).Select(s=>s.Name).Distinct().Count()==grid.Items.Count;
            checks["名称收窄且只读样式示例与两列可编辑层级"]=grid.Columns.Count==4&&grid.Columns[0].IsReadOnly&&grid.Columns[0].Width.Value==160&&grid.Columns[1].IsReadOnly&&grid.Columns.Skip(2).All(c=>!c.IsReadOnly);
            checks["面板显示道路主题线条示例"]=FavoritesPanel.Descendants<OsmRoadStylePreview>(panel).Any(p=>p.ActualWidth>0);
            checks["没有颜色线宽虚线描边的编辑入口"]=!FavoritesPanel.Descendants<Button>(panel).Any(b=>b.Content?.ToString()?.Contains("选择线色")==true||b.Content?.ToString()?.Contains("选择描边色")==true);
            checks["紧凑行高24和字号12且文字垂直居中"]=grid.RowHeight==24&&grid.FontSize==12&&FavoritesPanel.Descendants<DataGridRow>(grid).Where(r=>r.IsVisible).All(r=>r.ActualHeight<=24.1)&&FavoritesPanel.Descendants<OsmRoadStylePreview>(grid).Where(p=>p.IsVisible).All(p=>p.ActualHeight==20);
            await CaptureT10ClientAsync(manager,Path.Combine(folder,"T188-每种道路一行.png"));
            var row=((IEnumerable<OsmRoadStyle>)grid.ItemsSource).Single(s=>s.Id=="highway-path");row.MinZoom=12;row.MaxZoom=21;grid.Items.Refresh();await panel.ApplyAsync();
            T10ManualField<Button>(panel,"RestoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>Task.FromResult(!T10ManualField<bool>(panel,"_busy")),"恢复层级未结束");
            checks["每类型层级保存后仍自动成为默认"]=GlobalSettingsService.Current.OsmRoads.Styles.Single(s=>s.Id=="highway-path") is{MinZoom:12,MaxZoom:21};
            manager.Close();await Wait(()=>Task.FromResult(!manager.IsVisible),"管理窗口未关闭");
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T188_UI_ONLY")!="1")
            {
            foreach(var type in new[]{"path","track"})
            {
                var settings=new OsmRoadSettings{Enabled=true,Opacity=1};foreach(var other in settings.Styles.Where(s=>s.Id!="highway-"+type)){other.MinZoom=0;other.MaxZoom=0;}
                GlobalSettingsService.Current.OsmRoads=settings;await main.Map.ApplyGlobalMapLayersAsync(true);await JS(type=="track"?"window.hikingGuide3DTest.at([113.336076,24.387548],15)":"window.hikingGuide3DTest.at([113.32,24.42],15)");
                await Wait(async()=>{var p=await JS("window.hikingGuideOsmRoadTest.sample('"+type+"')");return p.ValueKind==JsonValueKind.Array;},"没有找到真实"+type+"道路");
                var coordinate=(await JS("window.hikingGuideOsmRoadTest.sample('"+type+"')")).GetRawText();var min=settings.Styles.Single(s=>s.Id=="highway-"+type).MinZoom;
                for(var z=Math.Max(10,min);z<=22;z++)
                {
                    await JS("window.hikingGuide3DTest.at("+coordinate+","+z+")");await Wait(async()=>{var r=await JS("window.hikingGuideOsmRoadTest.inspect()");return r.GetProperty("loaded").GetBoolean()&&r.GetProperty("features").GetInt32()>0;},"Z"+z+type+"道路未加载");await Task.Delay(350);
                    using var memory=new MemoryStream();await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,memory);var bytes=memory.ToArray();await File.WriteAllBytesAsync(Path.Combine(folder,$"T188-{type}-Z{z}.png"),bytes);
                    using var bitmap=SKBitmap.Decode(bytes);var ink=0;for(var y=Math.Max(0,bitmap.Height/2-180);y<Math.Min(bitmap.Height,bitmap.Height/2+180);y++)for(var x=Math.Max(0,bitmap.Width/2-250);x<Math.Min(bitmap.Width,bitmap.Width/2+250);x++){var c=bitmap.GetPixel(x,y);if(c.Red<185&&c.Green<185&&c.Blue<185)ink++;}
                    metrics.Add(new{Type=type,Zoom=z,DarkPixels=ink,Coordinate=coordinate});checks[type+" Z"+z+"真实道路有可见深色主题线条"]=ink>10;
                }
            }
            var dem=DemCatalog.Databases.First(d=>d.Status==DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            GlobalSettingsService.Current.OsmRoads=new(){Enabled=true};GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id};await main.Map.ApplyGlobalMapLayersAsync(true);await JS("window.hikingGuide3DTest.at([113.32,24.42],16)");
            await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect()")).GetProperty("effective").GetBoolean(),"3D地形未就绪");await JS("window.hikingGuide3DTest.camera(45,25)");
            await Wait(async()=>(await JS("window.hikingGuideOsmRoadTest.inspect()")).GetProperty("features").GetInt32()>0,"3D主题道路未显示");checks["3D使用相同固定主题道路"]=true;await Task.Delay(700);
            await using var capture=File.Create(Path.Combine(folder,"T188-主题道路3D.png"));await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
            }
        }
        catch(Exception e){error=e.ToString();foreach(var manager in Windows.OfType<MapSourceManagerWindow>().ToArray())manager.Close();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Metrics=metrics,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}
