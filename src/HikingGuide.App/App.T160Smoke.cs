using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;

public partial class App
{
    private async Task RunT160SmokeAsync(MainWindow window,MainViewModel main,WebView2 web,string folder,Dictionary<string,bool> checks)
    {
        if(!folder.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("测试必须使用隔离目录。");
        async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2!.ExecuteScriptAsync(code));
        var originalWidths=JsonSerializer.Serialize(GlobalSettingsService.Current.GuideTableColumnWidths);
        var originalLayout=window.CapturePanelLayout()!;
        var elements=new[]{("Project","ProjectPanel"),("Map","MapPanel"),("Properties","PropertiesPanel"),("Guide","GuideTab"),("Planning","PlanningTab"),("Analysis","AnalysisTab"),("Favorites","FavoritesTab"),("Status","MainStatusBar")};
        foreach(var (name,element) in elements)
        {
            await window.SetPanelVisibilityAsync(name,false);
            checks[$"T160{name}面板可以独立隐藏"]=((UIElement)window.FindName(element)).Visibility==Visibility.Collapsed;
            await window.SetPanelVisibilityAsync(name,true);
        }
        var restored=window.CapturePanelLayout()!;
        checks["T160逐项隐藏再显示保留比例"]=Math.Abs(restored.BottomFraction-originalLayout.BottomFraction)<.003&&Math.Abs(restored.ProjectFraction-originalLayout.ProjectFraction)<.003&&Math.Abs(restored.PropertiesFraction-originalLayout.PropertiesFraction)<.003;
        await window.SetPanelVisibilityAsync("Map",false);
        var upper=(Grid)window.FindName("UpperPanels");var project=(FrameworkElement)window.FindName("ProjectPanel");var properties=(FrameworkElement)window.FindName("PropertiesPanel");
        checks["T160隐藏地图后项目和属性相邻且分隔条可用"]=Grid.GetColumn(project)==0&&Grid.GetColumn(properties)==2&&((UIElement)window.FindName("ProjectPanelSplitter")).Visibility==Visibility.Visible;
        upper.ColumnDefinitions[0].Width=new GridLength(1.3,GridUnitType.Star);upper.ColumnDefinitions[2].Width=new GridLength(.7,GridUnitType.Star);window.UpdateLayout();
        ((GridSplitter)window.FindName("ProjectPanelSplitter")).RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});await window.PanelLayoutSaveTask;
        var partial=window.CapturePanelLayout()!;
        checks["T160局部布局调整不把隐藏地图宽度保存为零"]=Math.Abs((1-partial.ProjectFraction-partial.PropertiesFraction)-(1-restored.ProjectFraction-restored.PropertiesFraction))<.003;
        foreach(var (name,_) in elements)await window.SetPanelVisibilityAsync(name,false);
        checks["T160可隐藏全部面板且无残留分隔区域"]=upper.ActualHeight<1&&((TabControl)window.FindName("BottomTabs")).ActualHeight<1&&((UIElement)window.FindName("UpperBottomSplitter")).Visibility==Visibility.Collapsed;
        await GlobalSettingsService.LoadAsync();
        checks["T160面板显隐保存后重新读取保持"]=GlobalSettingsService.Current.PanelVisibility==new PanelVisibilitySettings{Project=false,Map=false,Properties=false,Guide=false,Planning=false,Analysis=false,Favorites=false,Status=false};
        await window.SetPanelVisibilityAsync("Analysis",true);
        checks["T160单独显示数据分析可占用主区域"]=window.IsAnalysisTabActive&&((TabControl)window.FindName("BottomTabs")).ActualHeight>400;
        await window.ResetPanelLayoutAsync();
        checks["T160恢复默认重新显示全部面板且不改列宽"]=GlobalSettingsService.Current.PanelVisibility==new PanelVisibilitySettings()&&originalWidths==JsonSerializer.Serialize(GlobalSettingsService.Current.GuideTableColumnWidths);
        await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(500);
        checks["T160隐藏再显示地图保留WebView实例"]=web.CoreWebView2 is not null&&(await JS("typeof window.hikingGuide3DTest.inspect==='function'")).GetBoolean();
        checks["T160回2D后不保留3D天空"]=(await JS("!window.hikingGuide3DTest.inspect().sky")).GetBoolean();

        var a=new MapSourceDefinition{Name="缓存清理验收A",UrlTemplate="https://example.com/a/{z}/{x}/{y}.png"};
        var b=new MapSourceDefinition{Name="缓存保留验收B",UrlTemplate="https://example.com/b/{z}/{x}/{y}.png"};
        GlobalSettingsService.Current.MapSources.AddRange([a,b]);
        var root=Path.Combine(Path.GetDirectoryName(GlobalSettingsService.SettingsFilePath)!,"MapTileCache");var cache=MapTileDiskCache.Shared(root);
        Task<CachedMapTile> Tile()=>Task.FromResult(new CachedMapTile([1,2,3,4],"image/png",DateTimeOffset.MaxValue));
        await cache.GetAsync(a.Id,"verify",10000,Tile);await cache.GetAsync(b.Id,"verify",10000,Tile);
        var kept=Directory.GetFiles(Path.Combine(root,b.Id.ToString("N")),"*.tile").Single();var bytes=await File.ReadAllBytesAsync(kept);
        var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();await T10ManualLayoutAsync(manager);
        checks["T160未选图源时清空缓存按钮禁用"]=!manager.ClearOnlineCacheButton.IsEnabled;
        manager.SourcesGrid.SelectedItem=manager.SourcesGrid.Items.Cast<MapSourceDefinition>().Single(s=>s.Id==a.Id);
        checks["T160清空缓存按钮位于测试按钮右侧"]=manager.ClearOnlineCacheButton.IsEnabled&&manager.ClearOnlineCacheButton.TranslatePoint(new Point(),manager).X>manager.TestButton.TranslatePoint(new Point(),manager).X;
        await CaptureT10ClientAsync(manager,Path.Combine(folder,"T160-在线图源清空缓存.png"));
        await manager.ClearOnlineCacheAsync(a.Id,a.Name);
        var keptAfter=await File.ReadAllBytesAsync(kept);
        checks["T160只清除选中在线图源且不影响其他缓存"]=Directory.GetFiles(Path.Combine(root,a.Id.ToString("N")),"*.tile").Length==0&&bytes.SequenceEqual(keptAfter);
        checks["T160清缓存保留图源设置且按钮恢复"]=manager.ClearOnlineCacheButton.IsEnabled&&GlobalSettingsService.Current.MapSources.Any(s=>s.Id==a.Id&&s.CacheCapacityMb==a.CacheCapacityMb);
        var old=GlobalSettingsService.Current.OfflineTerrain;
        manager.LibraryTabs.SelectedIndex=1;manager.TerrainContourOpacity.Value=37;
        await T10ManualLayoutAsync(manager);
        checks["T160透明度设置位于颜色右侧"]=manager.TerrainContourOpacity.TranslatePoint(new Point(),manager).X>manager.TerrainColor.TranslatePoint(new Point(),manager).X;
        await T10ManualLayoutAsync(manager);await CaptureT10ClientAsync(manager,Path.Combine(folder,"T160-等高线透明度.png"));
        FavoritesPanel.Descendants<Button>(manager).Single(v=>v.Content?.ToString()=="应用并保存等高线生成设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(800);
        checks["T160等高线透明度经真实应用按钮保存"]=Math.Abs(GlobalSettingsService.Current.OfflineTerrain.ContourOpacity-.37)<1e-8;
        if(DemProvider is not null)
        {
            var generator=new DemTerrainTiles(DemCatalog,DemProvider,DemTerrainTiles.CacheRoot);
            var before=JsonSerializer.SerializeToElement(generator.Configure(old));
            var after=JsonSerializer.SerializeToElement(generator.Configure(old with{ContourOpacity=.37}));
            checks["T160仅调透明度不改变瓦片缓存签名"]=before.GetProperty("signature").GetString()==after.GetProperty("signature").GetString();
        }
        await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(300);
        var layers=(await JS("window.hikingGuideTerrainTest.inspect()")).GetProperty("layers");
        checks["T160在2D中两档等高线透明度同步"]=layers.EnumerateArray().Where(l=>l.ValueKind!=JsonValueKind.Null&&l.GetProperty("id").GetString()!.Contains("contours")).All(l=>Math.Abs(l.GetProperty("opacity").GetDouble()-.37)<1e-8);
        manager.Close();await Task.Delay(300);
        GlobalSettingsService.Current.OfflineTerrain=old;
        GlobalSettingsService.Current.MapSources.RemoveAll(s=>s.Id==a.Id||s.Id==b.Id);await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync(true);
    }
}
