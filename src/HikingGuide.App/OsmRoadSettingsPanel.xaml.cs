using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.App;
public partial class OsmRoadSettingsPanel:UserControl
{
    private IGlobalSettingsService? _settings;
    private OsmRoadSettings _draft=new();
    private bool _loading=true,_busy;
    private readonly SemaphoreSlim _saveGate=new(1,1);
    private readonly DispatcherTimer _liveDelay=new(){Interval=TimeSpan.FromMilliseconds(160)};
    public OsmRoadSettingsPanel(){InitializeComponent();_liveDelay.Tick+=async(_,_)=>{_liveDelay.Stop();await SaveLiveAsync();};Unloaded+=(_,_)=>_liveDelay.Stop();}
    public void Initialize(IGlobalSettingsService settings)
    {
        _settings=settings;_draft=settings.Current.OsmRoads.DeepCopy();
        ShowRoads.IsChecked=_draft.Enabled;OpacitySlider.Value=_draft.Opacity*100;CacheSize.Text=_draft.CacheCapacityMb.ToString();
        MinimumZoomBox.ItemsSource=Enumerable.Range(8,7).ToArray();MinimumZoomBox.SelectedItem=_draft.MinimumZoom;
        StylesGrid.ItemsSource=_draft.Styles;_loading=false;UpdateInfo();
    }
    private void UpdateInfo(){var app=(App)System.Windows.Application.Current;DataInfo.Text="数据来自BRouter离线路网，数据目录："+app.OsmRoads.DataDirectory;StatusText.Text=app.OsmRoads.Status;}
    public void SynchronizeVisibility()
    {if(_settings is null)return;var wasLoading=_loading;_loading=true;ShowRoads.IsChecked=_settings.Current.OsmRoads.Enabled;_draft.Enabled=_settings.Current.OsmRoads.Enabled;_loading=wasLoading;}
    private void OnLiveChanged(object sender,RoutedEventArgs e){if(!_loading){_liveDelay.Stop();_liveDelay.Start();}}
    private void OnOpacityChanged(object sender,RoutedPropertyChangedEventArgs<double> e)=>OnLiveChanged(sender,e);
    private async Task SaveLiveAsync()
    {
        if(_settings is null)return;
        await _saveGate.WaitAsync();
        try
        {
            var previous=_settings.Current.OsmRoads;var next=previous.DeepCopy();next.Enabled=ShowRoads.IsChecked==true;next.Opacity=OpacitySlider.Value/100;
            _settings.Current.OsmRoads=next;
            try{await _settings.SaveAsync();StatusText.Text=next.Enabled?"路网已开启，放大至起显层级后按需生成。":"路网已隐藏并停止新的解码请求；样式已保留。";}
            catch{_settings.Current.OsmRoads=previous;StatusText.Text="路网设置保存失败，请检查文件权限或磁盘空间。";}
        }
        finally{_saveGate.Release();}
    }
    public async Task<bool> ApplyAsync(bool onlyIfDirty=false)
    {
        if(_settings is null||_busy)return false;
        _liveDelay.Stop();
        if(!StylesGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!StylesGrid.CommitEdit(DataGridEditingUnit.Row,true)){StatusText.Text="请先修正表格内无效的数值。";return false;}
        if(!int.TryParse(CacheSize.Text,out var mb)||MinimumZoomBox.SelectedItem is not int min){StatusText.Text="缓存容量请输入0至1048576的整数。";return false;}
        _draft.Enabled=ShowRoads.IsChecked==true;_draft.Opacity=OpacitySlider.Value/100;_draft.CacheCapacityMb=mb;_draft.MinimumZoom=min;
        if(!_draft.IsValid){StatusText.Text="显示层级无效：请输入0至22的整数，最小层级不能大于最大层级。";return false;}
        await _saveGate.WaitAsync();_busy=true;
        try
        {
            var previous=_settings.Current.OsmRoads;_settings.Current.OsmRoads=_draft.DeepCopy();
            try{await _settings.SaveAsync();_draft.DefaultZoomLevels=new(_settings.Current.OsmRoads.DefaultZoomLevels);StatusText.Text="道路显示层级已保存并设为默认；固定主题样式与路网缓存保持不变，2D与3D共用。";return true;}
            catch{_settings.Current.OsmRoads=previous;StatusText.Text="保存失败，请检查文件权限或磁盘空间。";return false;}
        }
        finally{_busy=false;_saveGate.Release();}
    }
    private async void OnApply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    private async void OnRestore(object sender,RoutedEventArgs e)
    {
        _draft.RestoreDefaultStyles();SearchBox.Text="";StylesGrid.ItemsSource=_draft.Styles;
        if(await ApplyAsync())StatusText.Text="已恢复最近保存的默认道路层级；显示开关、整体透明度、缓存容量及起显层级保持当前设置。";
    }
    private void OnSearch(object sender,TextChangedEventArgs e)
    {if(StylesGrid?.ItemsSource is null)return;var query=SearchBox.Text.Trim();CollectionViewSource.GetDefaultView(StylesGrid.ItemsSource).Filter=o=>o is OsmRoadStyle s&&(query.Length==0||s.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||s.Group.Contains(query,StringComparison.OrdinalIgnoreCase)||s.Description.Contains(query,StringComparison.OrdinalIgnoreCase));}
    private async void OnClear(object sender,RoutedEventArgs e)
    {
        if(_busy)return;_busy=true;ClearButton.IsEnabled=false;
        try{await ((App)System.Windows.Application.Current).OsmRoads.ClearAsync();_settings?.NotifyMapSettingsChanged();StatusText.Text="路网缓存已清空；原始rd5、等高线、阴影和在线图源缓存未改动。";}
        catch{StatusText.Text="清理失败，请检查缓存目录权限。";}
        finally{_busy=false;ClearButton.IsEnabled=true;}
    }
    private async void OnRefresh(object sender,RoutedEventArgs e)
    {try{((App)System.Windows.Application.Current).OsmRoads.InvalidateData();if(_settings is not null)await ((App)System.Windows.Application.Current).OsmRoads.ConfigureAsync(_settings.Current.OsmRoads);_settings?.NotifyMapSettingsChanged();UpdateInfo();}catch{StatusText.Text="刷新路网数据状态失败，请检查数据目录。";}}
}





