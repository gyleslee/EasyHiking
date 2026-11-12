using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Elevation;
using Microsoft.Web.WebView2.Wpf;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunMapControlsSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        await RunOsmRoadSmokeAsync(window,main,output);
        using var previous=JsonDocument.Parse(await File.ReadAllTextAsync(output));
        var checks=previous.RootElement.GetProperty("Checks").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetBoolean());
        string? error=previous.RootElement.GetProperty("Error").GetString();var folder=Path.GetDirectoryName(output)!;
        try
        {
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            window.Width=1800;window.Height=950;await T10ManualLayoutAsync(window);
            var view=FavoritesPanel.Descendants<MapView>(window).First();
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            async Task Wait(Func<Task<bool>> ready,string message){for(var i=0;i<1200;i++){if(await ready())return;await Task.Delay(50);}throw new InvalidOperationException(message);}
            var box=T10ManualField<ComboBox>(view,"BaseMapSourceBox");var stable=true;
            for(var i=0;i<12;i++)
            {
                var id=GlobalSettingsService.Current.MapLayerStack!.BaseSourceId;var index=GlobalSettingsService.Current.MapSources.FindIndex(s=>s.Id==id);
                var copy=JsonSerializer.Deserialize<MapSourceDefinition>(JsonSerializer.Serialize(GlobalSettingsService.Current.MapSources[index]))!;
                copy.Name="底图名称同步验证 "+i;GlobalSettingsService.Current.MapSources[index]=copy;
                GlobalSettingsService.NotifyMapSettingsChanged();await T10ManualLayoutAsync(window);
                stable &= box.SelectedValue is Guid selected&&selected==id&&box.SelectedItem is MapSourceDefinition source&&source.Name==copy.Name&&!string.IsNullOrWhiteSpace(box.Text);
            }
            checks["连续刷新和替换图源对象后底图名称不空白"]=stable;
            var alternative=main.Map.MapSources.First(s=>s.Id!=main.Map.SelectedMapSourceId);box.SelectedValue=alternative.Id;
            await Wait(()=>Task.FromResult(GlobalSettingsService.Current.MapLayerStack!.BaseSourceId==alternative.Id),"底图选择未保存");
            checks["底图下拉选择按稳定ID更新全局底图"]=main.Map.SelectedMapSourceId==alternative.Id;
            checks["上方工具栏移除重复放大缩小按钮"]=!FavoritesPanel.Descendants<Button>(view).Any(b=>b.Content is string text&&(text=="放大"||text=="缩小"));
            var open=T10ManualField<Button>(view,"OpenMapManagerButton");_=Dispatcher.BeginInvoke(new Action(()=>open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            await Wait(()=>Task.FromResult(Windows.OfType<MapSourceManagerWindow>().Any()),"地图管理入口未打开窗口");
            var manager=Windows.OfType<MapSourceManagerWindow>().First();checks["工具栏地图管理打开现有设置窗口"]=manager.IsVisible;
            var tabs=T10ManualField<TabControl>(manager,"LibraryTabs");tabs.SelectedIndex=2;await T10ManualLayoutAsync(manager);
            var panel=T10ManualField<OsmRoadSettingsPanel>(manager,"RoadPanel");
            checks["OSM标题及数据目录说明正确"]=FavoritesPanel.Descendants<TextBlock>(panel).Any(t=>t.Text=="OSM离线路网（独立叠加，不占用在线图源位置）")&&T10ManualField<TextBlock>(panel,"DataInfo").Text.StartsWith("数据来自BRouter离线路网，数据目录：");
            checks["程序默认及恢复默认层级满足道路规则"]=GlobalSettingsService.Current.OsmRoads.Styles.All(s=>s.MaxZoom==22)&&GlobalSettingsService.Current.OsmRoads.Styles.Where(s=>s.Conditions.Any(c=>c.Key=="highway"&&c.Values.Split('|').Any(v=>v is "track" or "path"))).All(s=>s.MinZoom==OsmRoadDefaults.CreateStyles().First(original=>original.Id==s.Id).MinZoom);
            await CaptureT10ClientAsync(manager,Path.Combine(folder,"T186-OSM路网设置.png"));
            tabs.SelectedIndex=3;await T10ManualLayoutAsync(manager);var offline=T10ManualField<OfflineMapDownloadPanel>(manager,"OfflinePanel");
            checks["下载面板不再显示文件名及存储位置提示"]=!FavoritesPanel.Descendants<TextBlock>(offline).Any(t=>t.Text.StartsWith("文件名：")||t.Name=="LocationInfo");
            T10ManualField<Expander>(offline,"TasksExpander").IsExpanded=true;T10ManualField<DataGrid>(offline,"TasksGrid").ItemsSource=new[]{new OfflineDownloadTask{Name="下载进度演示",Source=new(){Name="演示图源"},CompletedTiles=8435,PlannedTiles=8874,State=OfflineDownloadState.Paused,Message="演示数据，不执行下载"}};
            await T10ManualLayoutAsync(manager);checks["下载进度格完整显示百分比"]=FavoritesPanel.Descendants<TextBlock>(offline).Any(t=>t.Text.Contains("95.1%"));
            await CaptureT10ClientAsync(manager,Path.Combine(folder,"T186-下载进度.png"));
            tabs.SelectedIndex=1;await T10ManualLayoutAsync(manager);
            checks["等高线面板标题已更新"]=FavoritesPanel.Descendants<TextBlock>(manager).Any(t=>t.Text=="动态等高线（独立叠加，不占用在线图源位置）");
            await CaptureT10ClientAsync(manager,Path.Combine(folder,"T186-等高线生成器.png"));manager.Close();await Wait(()=>Task.FromResult(!manager.IsVisible),"地图管理尚未关闭");
            var dem=DemCatalog.Databases.First(d=>d.Status==DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{DemDatabaseId=dem.Id,ContoursVisible=false,HillshadeVisible=false};GlobalSettingsService.Current.OsmRoads.Enabled=false;GlobalSettingsService.NotifyMapSettingsChanged();
            var contour=T10ManualField<CheckBox>(view,"QuickContoursBox");var shade=T10ManualField<CheckBox>(view,"QuickHillshadeBox");var roads=T10ManualField<CheckBox>(view,"QuickOsmRoadsBox");
            await Wait(()=>Task.FromResult(contour.IsChecked==false&&shade.IsChecked==false&&roads.IsChecked==false),"面板设置未同步快捷开关");
            checks["全局设置反向同步三个快捷开关"]=true;
            main.Map.OsmRoadsVisible=true;await main.Map.ApplyGlobalMapLayersAsync(true);await JS("window.hikingGuide3DTest.at([113.336076,24.387548],22)");
            await Wait(async()=>(await JS("window.hikingGuideOsmRoadTest.inspect()")).GetProperty("features").GetInt32()>0,"Z22真实道路未显示");checks["Z22仍可显示真实rd5道路"]=true;
            main.Map.OsmRoadsVisible=false;
            foreach(var in3D in new[]{false,true})
            {
                GlobalSettingsService.Current.Terrain3D=new(){Enabled=in3D,TerrainDemDatabaseId=dem.Id};await main.Map.ApplyGlobalMapLayersAsync(true);await JS("window.hikingGuide3DTest.at([113.32,24.42],16)");
                if(in3D)await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect()")).GetProperty("effective").GetBoolean(),"3D未就绪");
                contour.IsChecked=true;shade.IsChecked=true;roads.IsChecked=true;
                await Wait(async()=>{var state=await JS("window.hikingGuideTerrainTest.inspect()");return state.GetProperty("layers").EnumerateArray().All(l=>l.ValueKind!=JsonValueKind.Null&&l.GetProperty("visible").GetBoolean())&&(await JS("window.hikingGuideOsmRoadTest.inspect()")).GetProperty("enabled").GetBoolean();},"快捷开关未显示图层");
                checks[(in3D?"3D":"2D")+"快捷开关启用DEM等高线阴影及OSM路网"]=true;
                contour.IsChecked=false;shade.IsChecked=false;roads.IsChecked=false;
                await Wait(async()=>{var state=await JS("window.hikingGuideTerrainTest.inspect()");return state.GetProperty("layers").EnumerateArray().All(l=>l.ValueKind==JsonValueKind.Null||!l.GetProperty("visible").GetBoolean())&&!(await JS("window.hikingGuideOsmRoadTest.inspect()")).GetProperty("enabled").GetBoolean();},"快捷开关未隐藏图层");
                checks[(in3D?"3D":"2D")+"快捷开关关闭全部派生图层"]=true;
            }
            manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();
            main.Map.DemContoursVisible=true;main.Map.TerrainHillshadeVisible=true;main.Map.OsmRoadsVisible=true;
            await Wait(()=>Task.FromResult(T10ManualField<CheckBox>(manager,"TerrainContours").IsChecked==true&&T10ManualField<CheckBox>(manager,"TerrainHillshade").IsChecked==true&&T10ManualField<CheckBox>(T10ManualField<OsmRoadSettingsPanel>(manager,"RoadPanel"),"ShowRoads").IsChecked==true),"快捷开关未同步已打开管理面板");
            checks["快捷开关同步已打开地图管理面板"]=true;manager.Close();await Wait(()=>Task.FromResult(!manager.IsVisible),"地图管理尚未关闭");await GlobalSettingsService.SaveAsync();
            using var disk=JsonDocument.Parse(await File.ReadAllTextAsync(GlobalSettingsService.SettingsFilePath));checks["快捷开关保存到全局设置"]=disk.RootElement.GetProperty("offlineTerrain").GetProperty("contoursVisible").GetBoolean()&&disk.RootElement.GetProperty("osmRoads").GetProperty("enabled").GetBoolean();
            await T10ManualLayoutAsync(window);await CaptureT10ClientAsync(window,Path.Combine(folder,"T186-地图快捷开关.png"));
        }
        catch(Exception e){error=(error is null?"":error+"\n")+e;foreach(var manager in Windows.OfType<MapSourceManagerWindow>().ToArray())manager.Close();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}
