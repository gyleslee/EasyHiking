using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.App;
public partial class MapSourceManagerWindow : Window
{
    private readonly IGlobalSettingsService _settings;
    private readonly IMapSourceTestService _tester;
    private readonly MapSourceTemplateService _templates;
    private readonly ObservableCollection<MapSourceDefinition> _items;
    private bool _loading;
    private bool _allowClose;
    private bool _modelessAfterSelection;
    public void HideForOfflineSelection(){_modelessAfterSelection=true;Hide();}
    public void ShowAfterOfflineSelection(){Show();Activate();}
    private CancellationTokenSource? _saveDelay;
    private bool _terrainCacheBusy;
    private bool _onlineCacheBusy;
    public Guid? CurrentSourceId => _settings.Current.MapLayerStack?.BaseSourceId;
    public MapSourceManagerWindow(IGlobalSettingsService settings,IMapSourceTestService tester,MapSourceTemplateService templates)
    {
        InitializeComponent(); _settings=settings;_tester=tester;_templates=templates;
        _items=new(settings.Current.MapSources.Select(s=>s.DeepCopy())); SourcesGrid.ItemsSource=_items;
        LoadTerrain(); RoadPanel.Initialize(settings); OfflinePanel.Initialize(settings);
        RefreshSlots(); _settings.MapSettingsChanged+=OnSettingsChanged;
        AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler(OnDragCompleted));
        Closed+=(_,_)=>{_settings.MapSettingsChanged-=OnSettingsChanged;_saveDelay?.Cancel();};
        Closing+=async (_,e)=>{if(_allowClose)return;e.Cancel=true;_saveDelay?.Cancel();if(await RoadPanel.ApplyAsync() && await SaveSafelyAsync()){_allowClose=true;Close();}else LibraryTabs.SelectedIndex=2;};
    }
    private void OnSettingsChanged(object? sender,EventArgs e) => Dispatcher.Invoke(()=>{RefreshSlots();TerrainContours.IsChecked=_settings.Current.OfflineTerrain.ContoursVisible;TerrainHillshade.IsChecked=_settings.Current.OfflineTerrain.HillshadeVisible;RoadPanel.SynchronizeVisibility();});
    private void LoadTerrain()
    {
        var s=_settings.Current.OfflineTerrain ?? new();
        TerrainDem.ItemsSource=((App)System.Windows.Application.Current).DemCatalog.Databases;
        TerrainDem.SelectedValue=_settings.Current.Terrain3D?.TerrainDemDatabaseId;
        TerrainContours.IsChecked=s.ContoursVisible;TerrainHillshade.IsChecked=s.HillshadeVisible;
        TerrainColor.Content=s.ContourColor;TerrainContourCache.Text=s.ContourCacheMb.ToString();TerrainShadeCache.Text=s.HillshadeCacheMb.ToString();TerrainOpacity.Value=s.HillshadeOpacity*100;
        TerrainMajorContours.IsChecked=s.MajorContoursEnabled;TerrainMajorInterval.Text=s.MajorContourIntervalMeters.ToString();
        TerrainContourOpacity.Value=s.ContourOpacity*100;
        TerrainLowInterval.Text=s.LowContourIntervalMeters.ToString();TerrainHighInterval.Text=s.HighContourIntervalMeters.ToString();
        TerrainLineWidth.Text=s.ContourLineWidth.ToString(System.Globalization.CultureInfo.CurrentCulture);TerrainMajorLineWidth.Text=s.MajorContourLineWidth.ToString(System.Globalization.CultureInfo.CurrentCulture);
        TerrainMajorColor.Content=s.MajorContourColor??s.ContourColor;
        TerrainSmooth.IsChecked=s.SmoothContours;
        TerrainStatus.Text="没有可选DEM时，请先在“DEM管理”挂载并扫描数据库。缺测或未覆盖区域透明显示。";
    }
    private void OnTerrainColor(object sender,RoutedEventArgs e)
    {
        var button=(Button)sender;var picker=new TrackColorWindow(button.Content.ToString()!){Owner=this,Title="等高线颜色选择器"};
        if(picker.ShowDialog()==true)button.Content=picker.SelectedColor;
    }
    private async void OnApplyTerrain(object sender,RoutedEventArgs e)
    {
        if(_terrainCacheBusy)return;
        if(!int.TryParse(TerrainMajorInterval.Text,out var major)||major<=0)
        {TerrainStatus.Text="主等高线间隔请输入大于0的整数，单位米，例如50、100、200。";return;}
        if(!int.TryParse(TerrainLowInterval.Text,out var low)||!int.TryParse(TerrainHighInterval.Text,out var high)||
            !double.TryParse(TerrainLineWidth.Text,out var lineWidth)||!double.TryParse(TerrainMajorLineWidth.Text,out var majorWidth))
        {TerrainStatus.Text="等高距请输入整数米，线宽请输入数值（像素）。";return;}
        if(!int.TryParse(TerrainContourCache.Text,out var contours)||!int.TryParse(TerrainShadeCache.Text,out var shade))
        {TerrainStatus.Text="缓存容量请输入0至1048576的整数，单位MB。";return;}
        var s=_settings.Current.OfflineTerrain with{DemDatabaseId=_settings.Current.Terrain3D?.TerrainDemDatabaseId,ContoursVisible=TerrainContours.IsChecked==true,HillshadeVisible=TerrainHillshade.IsChecked==true,ContourColor=TerrainColor.Content.ToString()!,ContourCacheMb=contours,HillshadeCacheMb=shade,HillshadeOpacity=TerrainOpacity.Value/100,MajorContoursEnabled=TerrainMajorContours.IsChecked==true,MajorContourIntervalMeters=major};
        if(!s.IsValid){TerrainStatus.Text="缓存容量或颜色无效，请检查设置。";return;}
        var app=(App)System.Windows.Application.Current;
        if((s.ContoursVisible||s.HillshadeVisible)&&(s.DemDatabaseId is not {} id||app.DemCatalog.Databases.FirstOrDefault(d=>d.Id==id) is not {Status:HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready}||app.DemProvider is null))
        {TerrainStatus.Text="请先选择可读取、已完成扫描的DEM数据库。";return;}
        s=s with {ContourOpacity=TerrainContourOpacity.Value/100,LowContourIntervalMeters=low,HighContourIntervalMeters=high,ContourLineWidth=lineWidth,MajorContourLineWidth=majorWidth,MajorContourColor=TerrainMajorColor.Content.ToString(),SmoothContours=TerrainSmooth.IsChecked==true};
        if(!s.IsValid){TerrainStatus.Text="等高距应为1—10000米；普通线宽0.3—20像素，主线宽0.3—40像素。";return;}
        _settings.Current.OfflineTerrain=s;
        if(!await SaveSafelyAsync()){TerrainStatus.Text="保存失败，请检查设置目录权限。";return;}
        _settings.NotifyMapSettingsChanged();
        try
        {
            var cache=HikingGuide.Infrastructure.Maps.MapTileDiskCache.Shared(Services.DemTerrainTiles.CacheRoot);
            await cache.TrimAsync(Services.DemTerrainTiles.ContourCacheId,(long)contours*1024*1024);
            await cache.TrimAsync(Services.DemTerrainTiles.HillshadeCacheId,(long)shade*1024*1024);
            TerrainStatus.Text="离线图层设置已保存；地图当前区域按需后台生成，已有缓存直接复用。";
        }
        catch(System.IO.IOException){TerrainStatus.Text="设置已保存，但缓存暂时无法清理，请检查缓存目录。";}
        catch(UnauthorizedAccessException){TerrainStatus.Text="设置已保存，但缓存目录不可写。";}
    }
    private async void OnClearContours(object sender,RoutedEventArgs e)=>await ConfirmClearTerrainCacheAsync(true);
    private void OnSharedTerrainDem(object sender,RoutedEventArgs e)
    {
        if(System.Windows.Application.Current.MainWindow?.DataContext is HikingGuide.App.ViewModels.MainViewModel main)
        {new Terrain3DWindow(main.Map){Owner=this}.ShowDialog();TerrainDem.SelectedValue=_settings.Current.Terrain3D?.TerrainDemDatabaseId;}
    }
    private async void OnClearShade(object sender,RoutedEventArgs e)=>await ConfirmClearTerrainCacheAsync(false);
    private async Task ConfirmClearTerrainCacheAsync(bool contours)
    {
        if(_terrainCacheBusy)return;var name=contours?"高程采样":"阴影";
        if(MessageBox.Show(this,$"仅清除{name}缓存（含2D及3D使用的缓存），不删除DEM、项目或其他缓存。已显示图块暂时保留，之后按地图请求重新生成。是否继续？",$"清除{name}缓存",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        await ClearTerrainCacheAsync(contours);
    }
    internal async Task ClearTerrainCacheAsync(bool contours)
    {
        if(_terrainCacheBusy)return;_terrainCacheBusy=true;ClearContoursButton.IsEnabled=ClearShadeButton.IsEnabled=false;
        var name=contours?"高程采样":"阴影";TerrainStatus.Text=$"正在清除{name}缓存…";
        try{await ((App)System.Windows.Application.Current).ClearOfflineTerrainCacheAsync(contours);TerrainStatus.Text=$"{name}缓存已清除；未删除DEM或其他缓存，后续按视野请求重新生成。";}
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException){TerrainStatus.Text=$"清除{name}缓存失败：{error.Message}";}
        finally{_terrainCacheBusy=false;ClearContoursButton.IsEnabled=ClearShadeButton.IsEnabled=true;}
    }
    private void RefreshSlots()
    {
        _loading=true;
        try
        {
            var sources=_settings.Current.MapSources; var s=_settings.Current.MapLayerStack??new();
            BaseBox.ItemsSource=sources.Where(v=>MapLayerStackService.IsAvailable(v,MapSourceUsage.Base)).ToArray();
            var overlays=new[]{new MapSourceDefinition{Id=Guid.Empty,Name="无"}}.Concat(sources.Where(v=>MapLayerStackService.IsAvailable(v,MapSourceUsage.Overlay))).ToArray();
            Overlay1Box.ItemsSource=overlays;Overlay2Box.ItemsSource=overlays;
            BaseBox.SelectedValue=s.BaseSourceId;Overlay1Box.SelectedValue=s.Overlay1SourceId??Guid.Empty;Overlay2Box.SelectedValue=s.Overlay2SourceId??Guid.Empty;
            Overlay1Visible.IsChecked=s.Overlay1Visible;Overlay2Visible.IsChecked=s.Overlay2Visible;
            Overlay1Visible.IsEnabled=s.Overlay1SourceId.HasValue;Overlay2Visible.IsEnabled=s.Overlay2SourceId.HasValue;
            BaseOpacity.Value=s.BaseOpacity*100;Overlay1Opacity.Value=s.Overlay1Opacity*100;Overlay2Opacity.Value=s.Overlay2Opacity*100;
        }
        finally {_loading=false;}
    }
    private async void OnSlotChanged(object sender,RoutedEventArgs e)
    {
        if(_loading||_settings is null)return;
        var old=_settings.Current.MapLayerStack??new(); Guid? Id(ComboBox b)=> b.SelectedValue is Guid id&&id!=Guid.Empty?id:null;
        var o1=Id(Overlay1Box);var o2=Id(Overlay2Box);
        var s=old with {BaseSourceId=Id(BaseBox)??old.BaseSourceId,Overlay1SourceId=o1,Overlay2SourceId=o2,Overlay1Visible=o1.HasValue&&(old.Overlay1SourceId!=o1||Overlay1Visible.IsChecked==true),Overlay2Visible=o2.HasValue&&(old.Overlay2SourceId!=o2||Overlay2Visible.IsChecked==true)};
        if(MapLayerStackService.Validate(s,_settings.Current.MapSources) is {} error) {RefreshSlots();MessageBox.Show(this,error,"图层设置无效",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        _settings.Current.MapLayerStack=s; _settings.NotifyMapSettingsChanged(); await SaveSafelyAsync();
    }
    private void OnOpacityChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(_loading||_settings is null)return;
        _settings.Current.MapLayerStack=(_settings.Current.MapLayerStack??new()) with {BaseOpacity=BaseOpacity.Value/100,Overlay1Opacity=Overlay1Opacity.Value/100,Overlay2Opacity=Overlay2Opacity.Value/100};
        _settings.NotifyMapSettingsChanged(); _saveDelay?.Cancel();_saveDelay=new(); _=SaveDebouncedAsync(_saveDelay.Token);
    }
    private async Task SaveDebouncedAsync(CancellationToken token) {try{await Task.Delay(400,token);await SaveSafelyAsync();}catch(OperationCanceledException){}}
    private async void OnDragCompleted(object sender,DragCompletedEventArgs e) { _saveDelay?.Cancel();await SaveSafelyAsync(); }
    private async void OnOpacityCommit(object sender,System.Windows.Input.KeyboardFocusChangedEventArgs e) {if(!_loading&&_settings is not null) {_saveDelay?.Cancel();await SaveSafelyAsync();}}
    private async Task<bool> SaveSafelyAsync() {try{await _settings.SaveAsync();return true;}catch{TestResults.Text="保存全局地图设置失败，请检查文件权限或磁盘空间。";return false;}}
    private MapSourceDefinition? Selected=>SourcesGrid.SelectedItem as MapSourceDefinition;
    private async Task SaveLibraryAsync()
    {
        if(!_items.Any(s=>MapLayerStackService.IsAvailable(s,MapSourceUsage.Base))) {TestResults.Text="必须保留至少一个有效底图。";ReloadLibrary();return;}
        _settings.Current.MapSources=_items.Select(s=>s.DeepCopy()).ToList();
        _settings.Current.MapLayerStack=MapLayerStackService.Normalize(_settings.Current.MapLayerStack,_settings.Current.MapSources);
        _settings.NotifyMapSettingsChanged();await SaveSafelyAsync();
        try
        {
            var cache=HikingGuide.Infrastructure.Maps.MapTileDiskCache.Shared(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_settings.SettingsFilePath)!,"MapTileCache"));
            foreach(var source in _settings.Current.MapSources)await cache.TrimAsync(source.Id,(long)source.CacheCapacityMb*1024*1024);
        }
        catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException)
        {MessageBox.Show(this,"图源设置已保存，但暂时无法清理瓦片缓存。请检查缓存目录是否可写。","缓存清理提示");}
        ReloadLibrary();RefreshSlots();
    }
    private void ReloadLibrary(){_items.Clear();foreach(var s in _settings.Current.MapSources)_items.Add(s.DeepCopy());}
    private async void OnAdd(object sender,RoutedEventArgs e)
    {
        var source=new MapSourceDefinition{Name="新建图源",UrlTemplate="https://example.com/{z}/{x}/{y}.png",Usage=MapSourceUsage.Both};
        if(new MapSourceEditorWindow(source,_templates){Owner=this}.ShowDialog()!=true)return;
        _items.Add(source);await SaveLibraryAsync();SourcesGrid.SelectedItem=_items.FirstOrDefault(s=>s.Id==source.Id);
    }
    private void OnEdit(object sender,RoutedEventArgs e)=>EditSelected();
    private void OnGridDoubleClick(object sender,System.Windows.Input.MouseButtonEventArgs e)=>EditSelected();
    private async void EditSelected()
    {
        if(Selected is not {} selected)return;var copy=selected.DeepCopy();
        if(new MapSourceEditorWindow(copy,_templates){Owner=this}.ShowDialog()!=true)return;
        _items[_items.IndexOf(selected)]=copy;await SaveLibraryAsync();SourcesGrid.SelectedItem=_items.FirstOrDefault(s=>s.Id==copy.Id);
    }
    private async void OnToggleEnabled(object sender,RoutedEventArgs e)
    {if(Selected is not {} s)return;s.Enabled=!s.Enabled;await SaveLibraryAsync();SourcesGrid.SelectedItem=_items.FirstOrDefault(v=>v.Id==s.Id);}
    private async void OnDelete(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} s)return;
        if(s.Id==GlobalMapLayerStackSettings.GermanyId){MessageBox.Show(this,"内置默认底图OSM Germany不能删除。","无法删除");return;}
        var stack=_settings.Current.MapLayerStack??new(); var usage=stack.BaseSourceId==s.Id?"底图":stack.Overlay1SourceId==s.Id?"叠加层1":stack.Overlay2SourceId==s.Id?"叠加层2":null;
        var prompt=usage is null?$"确定删除图源“{s.Name}”吗？":usage=="底图"?$"该图源当前用于底图，删除后将回退有效默认底图。是否继续？":$"该图源当前用于{usage}，删除后该叠加层将设为‘无’。是否继续？";
        if(MessageBox.Show(this,prompt,"删除图源",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        _items.Remove(s);await SaveLibraryAsync();
    }
    private async void OnTest(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} s)return;TestButton.IsEnabled=false;TestResults.Text="正在逐一测试各服务器…";
        try{var result=await _tester.TestAsync(s);TestResults.Text=result.Message+$"\n总耗时：{result.Duration.TotalMilliseconds:F0}毫秒";}
        catch(OperationCanceledException){TestResults.Text="图源测试已取消。";}catch{TestResults.Text="图源测试失败，请检查网络和请求设置。";}finally{TestButton.IsEnabled=true;}
    }
    private void OnSourceSelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(ClearOnlineCacheButton is not null)ClearOnlineCacheButton.IsEnabled=!_onlineCacheBusy&&Selected is not null;
    }
    private async void OnClearOnlineCache(object sender,RoutedEventArgs e)
    {
        if(_onlineCacheBusy||Selected is not {} source)return;
        if(MessageBox.Show(this,$"确认清空在线图源“{source.Name}”的本地瓦片缓存？\n\n不删除图源设置、项目、DEM或其他图源缓存。当前已显示的图块暂时保留，后续请求会重新下载。","清空图源缓存",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        await ClearOnlineCacheAsync(source.Id,source.Name);
    }
    internal async Task ClearOnlineCacheAsync(Guid sourceId,string sourceName)
    {
        if(_onlineCacheBusy)return;
        if(!_settings.Current.MapSources.Any(s=>s.Id==sourceId))throw new ArgumentException("未找到所选在线图源。");
        _onlineCacheBusy=true;ClearOnlineCacheButton.IsEnabled=false;
        TestResults.Text=$"正在清空“{sourceName}”的缓存…";
        try
        {
            var root=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_settings.SettingsFilePath)!,"MapTileCache");
            await HikingGuide.Infrastructure.Maps.MapTileDiskCache.Shared(root).ClearAsync(sourceId);
            TestResults.Text=$"“{sourceName}”缓存已清空，未更改缓存容量或其他图源；后续地图请求会重新下载。";
        }
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException)
        {TestResults.Text=$"清空“{sourceName}”缓存失败：{error.Message}";}
        finally{_onlineCacheBusy=false;ClearOnlineCacheButton.IsEnabled=Selected is not null;}
    }
    private async void OnSave(object sender,RoutedEventArgs e){_saveDelay?.Cancel();if(await SaveSafelyAsync()){_allowClose=true;if(_modelessAfterSelection)Close();else DialogResult=true;}}
}


