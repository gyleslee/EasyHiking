using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using Microsoft.Web.WebView2.Wpf;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunOsmRoadSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;object? metrics=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            Directory.CreateDirectory(folder);
            for(var i=0;i<300&&!main.Map.IsReady;i++)await Task.Delay(100);
            if(!main.Map.IsReady)throw new InvalidOperationException("地图未就绪");
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            async Task Wait(Func<Task<bool>> ready,string message)
            {for(var i=0;i<1200;i++){if(await ready())return;await Task.Delay(50);}throw new InvalidOperationException(message);}
            GlobalSettingsService.Current.OfflineTerrain=new(){ContoursVisible=false,HillshadeVisible=false};
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=false};
            GlobalSettingsService.Current.OsmRoads=new(){Enabled=true,Opacity=.8};
            await JS("window.hikingGuide3DTest.at([113.32,24.42],16)");
            var clock=Stopwatch.StartNew();await main.Map.ApplyGlobalMapLayersAsync(true);
            await Wait(async()=>{var state=await JS("window.hikingGuideOsmRoadTest.inspect()");return state.GetProperty("loaded").GetBoolean()&&state.GetProperty("features").GetInt32()>0;},"真实rd5路网没有显示");
            checks["真实rd5解码为地图可见矢量道路"]=OsmRoads.ChunksDecoded>0&&OsmRoads.TilesEncoded>0;
            var state=await JS("window.hikingGuideOsmRoadTest.inspect()");
            var order=state.GetProperty("order").EnumerateArray().Select(x=>x.GetString()!).ToList();
            checks["路网位于用户轨迹下方"]=order.FindLastIndex(s=>s.StartsWith("osm-road-"))<order.IndexOf("tracks-normal");
            await using(var capture=File.Create(Path.Combine(folder,"T184-OSM路网-2D.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
            var firstMs=clock.Elapsed.TotalMilliseconds;var chunks=OsmRoads.ChunksDecoded;var encoded=OsmRoads.TilesEncoded;
            GlobalSettingsService.Current.OsmRoads.Opacity=.37;GlobalSettingsService.Current.OsmRoads.Styles[0].Color="#123456";await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(450);
            state=await JS("window.hikingGuideOsmRoadTest.inspect()");
            checks["整体透明度控制线条与描边全部图层"]=state.GetProperty("opacities").EnumerateArray().All(x=>x.GetDouble()<=.370001);
            checks["修改样式不重复解码或编码已有瓦片"]=chunks==OsmRoads.ChunksDecoded&&encoded==OsmRoads.TilesEncoded;
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();
            var tabs=T10ManualField<TabControl>(manager,"LibraryTabs");tabs.SelectedIndex=2;await T10ManualLayoutAsync(manager);
            var panel=T10ManualField<OsmRoadSettingsPanel>(manager,"RoadPanel");
            checks["OSM路网位于地图管理下方第三个标签"]=tabs.Items.Count==4&&((TabItem)tabs.SelectedItem).Header.ToString()=="OSM路网";
            T10ManualField<Button>(panel,"RestoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>Task.FromResult(GlobalSettingsService.Current.OsmRoads.Styles[0].Color!="#123456"),"恢复默认样式未生效");
            checks["恢复默认保留启用透明度和容量"]=GlobalSettingsService.Current.OsmRoads is{Enabled:true,Opacity:.37,CacheCapacityMb:2048,MinimumZoom:10};
            checks["恢复程序默认道路样式和层级"]=JsonSerializer.Serialize(GlobalSettingsService.Current.OsmRoads.Styles)==JsonSerializer.Serialize(OsmRoadDefaults.CreateStyles());
            await T10ManualLayoutAsync(manager);await CaptureT10ClientAsync(manager,Path.Combine(folder,"T184-OSM路网设置.png"));
            var show=T10ManualField<CheckBox>(panel,"ShowRoads");show.IsChecked=false;show.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Wait(async()=>!(await JS("window.hikingGuideOsmRoadTest.inspect()")).GetProperty("enabled").GetBoolean(),"显示开关未关闭路网");
            chunks=OsmRoads.ChunksDecoded;await Task.Delay(400);checks["关闭路网移除图层并停止解码"]=chunks==OsmRoads.ChunksDecoded;
            show.IsChecked=true;show.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));await Wait(async()=>(await JS("window.hikingGuideOsmRoadTest.inspect()")).GetProperty("features").GetInt32()>0,"重新显示失败");
            checks["重新开启路网复用已生成缓存"]=chunks==OsmRoads.ChunksDecoded;
            manager.Close();await Wait(()=>Task.FromResult(!manager.IsVisible),"地图管理窗口未关闭");
            var dem=DemCatalog.Databases.First(d=>d.Status==DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem.Id};await main.Map.ApplyGlobalMapLayersAsync(true);
            await Wait(async()=>{var t=await JS("window.hikingGuide3DTest.inspect()");var r=await JS("window.hikingGuideOsmRoadTest.inspect()");return t.GetProperty("effective").GetBoolean()&&r.GetProperty("features").GetInt32()>0;},"3D路网未显示");
            await JS("window.hikingGuide3DTest.camera(45,25)");await Task.Delay(1600);
            checks["3D地形上独立显示相同路网"]=main.Map.EffectiveTerrainState;
            await using(var capture=File.Create(Path.Combine(folder,"T184-OSM路网-3D.png")))await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
            await JS("window.hikingGuideOfflineTest.begin()");
            var selectionCamera=await JS("window.hikingGuideRasterTest.inspect()");
            checks["3D选区暂时切到俯视且不改变全局3D设置"]=(await JS("window.hikingGuideOfflineTest.inspect()")).GetProperty("active").GetBoolean()&&selectionCamera.GetProperty("pitch").GetDouble()==0&&GlobalSettingsService.Current.Terrain3D!.Enabled;
            await JS("window.hikingGuideOfflineTest.cancel()");
            await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect()")).GetProperty("effective").GetBoolean(),"选区取消后3D未恢复");
            selectionCamera=await JS("window.hikingGuideRasterTest.inspect()");
            checks["取消选区恢复原3D方位俯仰并解除交互锁"]=!(await JS("window.hikingGuideOfflineTest.inspect()")).GetProperty("active").GetBoolean()&&Math.Abs(selectionCamera.GetProperty("pitch").GetDouble()-45)<.1&&Math.Abs(selectionCamera.GetProperty("bearing").GetDouble()-25)<.1;
            GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D with{Enabled=false};await main.Map.ApplyGlobalMapLayersAsync(true);
            await JS("window.hikingGuide3DTest.camera(0,0)");await JS("window.hikingGuide3DTest.at([113.32,24.42],9)");await Task.Delay(600);
            checks["Z10以下不绘制道路"]= (await JS("window.hikingGuideOsmRoadTest.inspect()")).GetProperty("features").GetInt32()==0;
            await GlobalSettingsService.SaveAsync();var disk=JsonDocument.Parse(await File.ReadAllTextAsync(GlobalSettingsService.SettingsFilePath));
            checks["全局设置保存为可恢复配置"]=disk.RootElement.GetProperty("osmRoads").GetProperty("opacity").GetDouble()==.37;
            var oldRevision=OsmRoads.ConfigurationRevision;await OsmRoads.ClearAsync();checks["清缓存使旧请求失效"]=OsmRoads.ConfigurationRevision>oldRevision;
            metrics=new{ColdVisibleMs=firstMs,OsmRoads.AvailableFiles,OsmRoads.ChunksDecoded,OsmRoads.TilesEncoded,Styles=GlobalSettingsService.Current.OsmRoads.Styles.Count};
        }
        catch(Exception exception){error=exception.ToString();}
        var passed=error is null&&checks.Count>0&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Metrics=metrics,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}



