using System.Globalization;
using System.Windows;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;

namespace HikingGuide.App;

public partial class Terrain3DWindow : Window
{
    private readonly MapViewModel _map;
    private readonly App _app;
    private bool _loading=true;
    private CancellationTokenSource? _delay;
    public Terrain3DWindow(MapViewModel map)
    {
        InitializeComponent();_map=map;_app=(App)System.Windows.Application.Current;
        Loaded+=async(_,_)=>await LoadAsync();Closed+=(_,_)=>_delay?.Cancel();
    }
    private async Task LoadAsync()
    {
        _loading=true;StatusText.Text="正在检查已挂载DEM的可采样状态…";
        try
        {
            var s=_app.GlobalSettingsService.Current.Terrain3D??new();
            DemBox.ItemsSource=_app.TerrainTiles is {} generator?await generator.Sampler.AvailableAsync():[];
            EnabledBox.IsChecked=s.Enabled;DemBox.SelectedValue=s.TerrainDemDatabaseId;
            var offline=_app.GlobalSettingsService.Current.OfflineTerrain;
            Exaggeration.Value=s.Exaggeration;Pitch.Value=s.DefaultPitch;Contours.IsChecked=offline.ContoursVisible;Shade.IsChecked=offline.HillshadeVisible;
            ExaggerationInput.Text=Terrain3DSettings.RoundExaggeration(s.Exaggeration).ToString("F1",CultureInfo.CurrentCulture);
            CacheGb.Text=(s.CacheLimitMb/1024d).ToString("0.###",CultureInfo.CurrentCulture);
            ShadeOpacity.Value=_app.GlobalSettingsService.Current.OfflineTerrain.HillshadeOpacity*100;
            Summaries();await UsageAsync();StatusText.Text=MapViewModel.TerrainEditingWarning;
        }
        catch(Exception error){StatusText.Text="读取3D设置失败："+error.Message;}
        finally{_loading=false;}
    }
    private void Summaries()
    {
        NativeText.Text=DemBox.SelectedItem is TerrainDemChoice choice?$"地形原生最高级别：Z{choice.MaxNativeZoom}；从地图Z{choice.MaxNativeZoom+1}起可启用3D，更高层级放大现有地形，不生成虚假高精度瓦片。":"请选择可采样的DEM数据库。";
        var offline=_app.GlobalSettingsService.Current.OfflineTerrain;
        var major=offline.MajorContoursEnabled?$"{offline.MajorContourIntervalMeters}米倍数为主等高线（自定义主线宽）":"主等高线加粗已关闭";
        var dem=_app.GlobalSettingsService.Current.Terrain3D?.TerrainDemDatabaseId is {} id?_app.DemCatalog.Databases.FirstOrDefault(d=>d.Id==id)?.Name:null;
        EffectsSummary.Text=$"统一地图DEM：{dem??"未选择"}。等高距：Z13—Z14为{offline.LowContourIntervalMeters}米，Z15起{offline.HighContourIntervalMeters}米；0米线不生成。{major}，与粗细线共同贴合地形。颜色：{offline.ContourColor}。阴影光照：方位315°，高度45°。";
    }
    private async Task<bool> CommitAsync()
    {
        if(_loading)return false;
        if(!double.TryParse(ExaggerationInput.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var exaggeration)||
            !double.IsFinite(exaggeration)||exaggeration is <.5 or >3)
        {StatusText.Text="地形夸张请输入0.5—3之间的数值，精确到1位小数，例如1.2、1.4。";return false;}
        if(!double.TryParse(CacheGb.Text,out var gb)||!double.IsFinite(gb)||gb is <1 or >50){StatusText.Text="缓存容量请输入1—50GB。";return false;}
        exaggeration=Terrain3DSettings.RoundExaggeration(exaggeration);
        _loading=true;Exaggeration.Value=exaggeration;ExaggerationInput.Text=exaggeration.ToString("F1",CultureInfo.CurrentCulture);_loading=false;
        var s=(_app.GlobalSettingsService.Current.Terrain3D??new()) with{Enabled=EnabledBox.IsChecked==true,TerrainDemDatabaseId=DemBox.SelectedValue as Guid?,Exaggeration=Exaggeration.Value,
            DefaultPitch=Pitch.Value,CacheLimitMb=(int)Math.Round(gb*1024)};
        if(s.Enabled&&s.TerrainDemDatabaseId is null){StatusText.Text="请先选择可采样的3D高程来源。";return false;}
        try
        {
            await _map.SaveTerrainSettingsAsync(s);Summaries();StatusText.Text="设置已保存并应用；只影响地图，不修改项目。";return true;
        }
        catch(Exception error){StatusText.Text="保存3D设置失败："+error.Message;return false;}
    }
    private async void OnChanged(object sender,RoutedEventArgs e){if(!_loading)await CommitAsync();}
    private async void OnSliderChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(_loading)return;_delay?.Cancel();var stop=_delay=new();
        if(ReferenceEquals(sender,Exaggeration))ExaggerationInput.Text=Terrain3DSettings.RoundExaggeration(Exaggeration.Value).ToString("F1",CultureInfo.CurrentCulture);
        try{await Task.Delay(180,stop.Token);await CommitAsync();}catch(OperationCanceledException){}
    }
    private async void OnConfirm(object sender,RoutedEventArgs e){_delay?.Cancel();if(await CommitAsync())Close();}
    private async void OnExaggerationTextCommitted(object sender,System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {if(_loading)return;_delay?.Cancel();await CommitAsync();}
    private async void OnExaggerationKeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {if(e.Key!=System.Windows.Input.Key.Enter)return;e.Handled=true;_delay?.Cancel();await CommitAsync();}
    private async void OnDefaults(object sender,RoutedEventArgs e)
    {
        var old=_app.GlobalSettingsService.Current.OfflineTerrain;
        _loading=true;EnabledBox.IsChecked=false;DemBox.SelectedIndex=-1;Exaggeration.Value=1;ExaggerationInput.Text="1.0";Pitch.Value=55;Contours.IsChecked=old.ContoursVisible;Shade.IsChecked=old.HillshadeVisible;CacheGb.Text="4";_loading=false;
        await CommitAsync();
    }
    private async Task UsageAsync(){UsageText.Text=$"当前缓存：{(_app.TerrainTiles is {} g?await g.UsageAsync():0)/1048576d:0.0} MB";}
    private async void OnClearCache(object sender,RoutedEventArgs e)
    {
        if(MessageBox.Show(this,"仅清除3D地形缓存，不删除DEM、等高线或阴影缓存。是否继续？","清除3D缓存",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        try{await _map.ClearTerrainCacheAsync();await UsageAsync();}catch(Exception error){StatusText.Text="清除缓存失败："+error.Message;}
    }
    private async void OnNorth(object sender,RoutedEventArgs e)=>await _map.SetTerrainCameraAsync(true);
    private async void OnDefaultCamera(object sender,RoutedEventArgs e)=>await _map.SetTerrainCameraAsync(false);
    private async void OnEffects(object sender,RoutedEventArgs e)
    {
        var window=new MapSourceManagerWindow(_app.GlobalSettingsService,_app.MapSourceTestService,_app.MapSourceTemplateService){Owner=this};
        window.LibraryTabs.SelectedIndex=1;window.ShowDialog();await LoadAsync();
    }
}
