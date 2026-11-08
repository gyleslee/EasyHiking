using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
namespace HikingGuide.App;
public partial class OfflineMapDownloadPanel:UserControl
{
    private IGlobalSettingsService? _settings;
    private OfflineMapRegion? _region;
    private OfflineDownloadEstimate? _estimate;
    private CancellationTokenSource? _estimateCancel;
    private bool _loading,_busy,_subscribed;
    private string _sourceSignature="";
    private int _previousTaskCount;
    private App App=>(App)System.Windows.Application.Current;
    private MainViewModel Main=>((MainWindow)App.MainWindow).DataContext as MainViewModel??throw new InvalidOperationException("主界面未就绪。");
    public OfflineMapDownloadPanel(){InitializeComponent();Loaded+=OnLoaded;Unloaded+=(_,_)=>Unsubscribe();}
    public void Initialize(IGlobalSettingsService settings)
    {_settings=settings;_loading=true;SourceBox.ItemsSource=settings.Current.MapSources.Where(s=>s.Enabled&&OfflineMapSourceIdentity.DownloadRestriction(s) is null).Select(s=>s.DeepCopy()).ToArray();SourceBox.SelectedItem=((IEnumerable<MapSourceDefinition>)SourceBox.ItemsSource).FirstOrDefault(s=>s.Id==settings.Current.MapLayerStack?.BaseSourceId)??((IEnumerable<MapSourceDefinition>)SourceBox.ItemsSource).FirstOrDefault();ConcurrencyBox.ItemsSource=new[]{1,2,3,4};ConcurrencyBox.SelectedItem=2;_loading=false;_sourceSignature=System.Text.Json.JsonSerializer.Serialize(settings.Current.MapSources.Where(s=>s.Enabled&&OfflineMapSourceIdentity.DownloadRestriction(s) is null));LoadZooms();}
    private async void OnLoaded(object sender,RoutedEventArgs e)
    {try{if(!_subscribed){App.OfflineMaps.Changed+=OnDataChanged;App.OfflineDownloads.Changed+=OnDataChanged;_subscribed=true;}await App.OfflineDownloads.InitializeAsync();RefreshSources();RefreshLists();}catch{StatusText.Text="离线地图库读取失败，请检查目录权限。";}}
    private void Unsubscribe(){if(_subscribed){App.OfflineMaps.Changed-=OnDataChanged;App.OfflineDownloads.Changed-=OnDataChanged;_subscribed=false;}}
    private void OnDataChanged(object? sender,EventArgs e){if(!Dispatcher.HasShutdownStarted)_=Dispatcher.BeginInvoke(RefreshLists);}
    private void RefreshLists()
    {var task=(TasksGrid.SelectedItem as OfflineDownloadTask)?.Id;var pack=(PacksGrid.SelectedItem as OfflineMapPack)?.Id;var tasks=App.OfflineDownloads.Tasks.Where(t=>t.State!=OfflineDownloadState.Completed).ToArray();TasksGrid.ItemsSource=tasks;TasksExpander.Header=$"下载任务（{tasks.Length}）—关闭软件后可续传";if(tasks.Length==0)TasksExpander.IsExpanded=false;else if(_previousTaskCount==0)TasksExpander.IsExpanded=true;_previousTaskCount=tasks.Length;PacksGrid.ItemsSource=App.OfflineMaps.Packs;TasksGrid.SelectedItem=App.OfflineDownloads.Tasks.FirstOrDefault(t=>t.Id==task);PacksGrid.SelectedItem=App.OfflineMaps.Packs.FirstOrDefault(p=>p.Id==pack);if(App.OfflineMaps.Warning is {} warning)StatusText.Text=warning;}
    private void LoadZooms()
    {if(SourceBox.SelectedItem is not MapSourceDefinition source){Invalidate();EstimateButton.IsEnabled=false;EstimateInfo.Text="没有可用于离线下载的图源。";return;}EstimateButton.IsEnabled=true;_loading=true;var levels=Enumerable.Range(source.MinZoom,source.MaxZoom-source.MinZoom+1).ToArray();MinZoomBox.ItemsSource=levels;MaxZoomBox.ItemsSource=levels;MinZoomBox.SelectedItem=Math.Clamp(12,source.MinZoom,source.MaxZoom);MaxZoomBox.SelectedItem=Math.Clamp(15,source.MinZoom,source.MaxZoom);_loading=false;Invalidate();}
    private void OnSourceChanged(object sender,SelectionChangedEventArgs e){if(!_loading&&_settings is not null)LoadZooms();}
    private void OnRangeChanged(object sender,SelectionChangedEventArgs e){if(!_loading)Invalidate();}
    private void Invalidate(){_estimateCancel?.Cancel();_estimate=null;if(StartButton is not null)StartButton.IsEnabled=false;if(EstimateInfo is not null)EstimateInfo.Text="范围或图源已改变，请重新预估。";}
    private async void OnSelectRegion(object sender,RoutedEventArgs e)
    {
        if(Window.GetWindow(this) is not MapSourceManagerWindow manager)return;
        SelectRegionButton.IsEnabled=false;using var canceled=new CancellationTokenSource();var closed=false;EventHandler onClosed=(_,_)=>{closed=true;canceled.Cancel();};manager.Closed+=onClosed;
        try
        {
            manager.HideForOfflineSelection();await Dispatcher.Yield(DispatcherPriority.Background);
            var region=await Main.Map.SelectOfflineRegionAsync(canceled.Token);if(region is not null)SetRegion(region);
        }
        catch(OperationCanceledException){}
        catch(Exception error){StatusText.Text=error.Message;}
        finally{manager.Closed-=onClosed;SelectRegionButton.IsEnabled=true;if(!closed&&manager.Owner?.IsLoaded==true)manager.ShowAfterOfflineSelection();}
    }
    internal void SetRegion(OfflineMapRegion region){OfflineMapCoverage.CreateGeometry(region);_region=region;RegionInfo.Text=$"已选择 {region.Points.Count} 个顶点的多边形";Invalidate();}
    private async void OnClearRegion(object sender,RoutedEventArgs e){_region=null;RegionInfo.Text="尚未选择下载范围";Invalidate();await Main.Map.ClearOfflineRegionAsync();}
    private (MapSourceDefinition Source,OfflineMapRegion Region,int Min,int Max) ReadInput()
    {if(SourceBox.SelectedItem is not MapSourceDefinition source||_region is null||MinZoomBox.SelectedItem is not int min||MaxZoomBox.SelectedItem is not int max)throw new ArgumentException("请先选择图源、多边形范围和下载层级。");if(min>max)throw new ArgumentException("最小层级不能大于最大层级。");return(source.DeepCopy(),_region,min,max);}
    private async void OnEstimate(object sender,RoutedEventArgs e)
    {
        if(_busy)return;_busy=true;EstimateButton.IsEnabled=false;_estimateCancel?.Dispose();_estimateCancel=new();
        try{var input=ReadInput();EstimateInfo.Text="正在统计相交瓦片并抽样预估大小…";_estimate=await App.OfflineDownloads.EstimateAsync(input.Source,input.Region,input.Min,input.Max,_estimateCancel.Token);EstimateInfo.Text=$"瓦片 {(_estimate.Coverage.Exact?"":"至少")}{_estimate.Coverage.Total:N0} 张，预估 {_estimate.SizeText}（抽样 {_estimate.Samples} 张）。{_estimate.Message}";StartButton.IsEnabled=_estimate.Coverage.Total is >0 and <=OfflineMapCoverage.MaximumDownloadTiles;}
        catch(OperationCanceledException){EstimateInfo.Text="预估已取消，请重新预估。";}catch(Exception error){EstimateInfo.Text=error is ArgumentException or InvalidDataException?error.Message:"大小预估失败，请检查图源或网络后重试。";}
        finally{_busy=false;EstimateButton.IsEnabled=true;}
    }
    private async void OnStart(object sender,RoutedEventArgs e)
    {
        try{if(_estimate is null)throw new ArgumentException("请先预估下载大小。");var input=ReadInput();if(!int.TryParse(IntervalBox.Text,out var interval)||ConcurrencyBox.SelectedItem is not int concurrency)throw new ArgumentException("请输入有效的并发数及请求间隔。");if(new DriveInfo(Path.GetPathRoot(App.OfflineMaps.RootDirectory)!).AvailableFreeSpace<_estimate.EstimatedBytes*2+64*1024*1024)throw new IOException("磁盘空间不足，请缩小下载范围或降低最大层级。");var task=await App.OfflineDownloads.StartAsync(MapNameBox.Text,input.Source,input.Region,input.Min,input.Max,concurrency,interval);StatusText.Text="任务已在后台启动，可关闭地图管理窗口继续使用软件。";RefreshLists();TasksGrid.SelectedItem=task;}
        catch(Exception error){StatusText.Text=error.Message;}
    }
    private async void OnPause(object sender,RoutedEventArgs e){await Act(async()=>{await App.OfflineDownloads.PauseAsync();StatusText.Text="当前下载已暂停，进度保留。";});}
    private async void OnResume(object sender,RoutedEventArgs e){await Act(async()=>{if(TasksGrid.SelectedItem is not OfflineDownloadTask task)throw new ArgumentException("请选择需要续传的任务。");await App.OfflineDownloads.ResumeAsync(task.Id);});}
    private async void OnSavePartial(object sender,RoutedEventArgs e){await Act(async()=>{if(TasksGrid.SelectedItem is not OfflineDownloadTask task)throw new ArgumentException("请选择下载任务。");await App.OfflineDownloads.SavePartialAsync(task.Id);});}
    private async void OnDeleteTask(object sender,RoutedEventArgs e)
    {if(TasksGrid.SelectedItem is not OfflineDownloadTask task)return;if(MessageBox.Show(Window.GetWindow(this),$"删除任务“{task.Name}”及其未完成下载数据？已生成的PMTiles离线地图保留。","删除下载任务",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;await Act(()=>App.OfflineDownloads.DeleteTaskAsync(task.Id));}
    private async void OnDeletePack(object sender,RoutedEventArgs e)
    {if(PacksGrid.SelectedItem is not OfflineMapPack pack)return;if(App.OfflineDownloads.ActiveId==pack.Id){StatusText.Text="此离线地图的任务仍在运行，请先暂停。";return;}if(MessageBox.Show(Window.GetWindow(this),$"删除离线地图“{pack.Name}”？文件将从本地删除，不影响原图源和项目。","删除离线地图",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;await Act(()=>App.OfflineMaps.DeleteAsync(pack.Id));}
    private async void OnToggle(object sender,RoutedEventArgs e){await Act(async()=>{if(PacksGrid.SelectedItem is not OfflineMapPack pack)throw new ArgumentException("请选择离线地图。");await App.OfflineMaps.SetEnabledAsync(pack.Id,!pack.Enabled);StatusText.Text="启用状态已保存，后续瓦片请求按新设置读取。";});}
    private async void OnLocate(object sender,RoutedEventArgs e){await Act(async()=>{if(PacksGrid.SelectedItem is not OfflineMapPack pack)throw new ArgumentException("请选择离线地图。");await Main.Map.LocateOfflineRegionAsync(pack.Region,Math.Min(pack.MaxZoom,Math.Max(pack.MinZoom,13)));});}
    private void OnOpenFolder(object sender,RoutedEventArgs e)
    {try{var folder=(PacksGrid.SelectedItem as OfflineMapPack)?.FilePath;if(folder is not null&&File.Exists(folder))Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+folder+"\""){UseShellExecute=true});else{Directory.CreateDirectory(App.OfflineMaps.PacksDirectory);Process.Start(new ProcessStartInfo(App.OfflineMaps.PacksDirectory){UseShellExecute=true});}}catch{StatusText.Text="无法打开离线地图目录，请检查文件是否存在。";}}
    private async void OnRefresh(object sender,RoutedEventArgs e){await Act(async()=>{await App.OfflineMaps.RefreshAsync();RefreshSources();});}
    private void RefreshSources(){if(_settings is null)return;var signature=System.Text.Json.JsonSerializer.Serialize(_settings.Current.MapSources.Where(s=>s.Enabled&&OfflineMapSourceIdentity.DownloadRestriction(s) is null));if(signature==_sourceSignature)return;_sourceSignature=signature;var id=(SourceBox.SelectedItem as MapSourceDefinition)?.Id;_loading=true;var sources=_settings.Current.MapSources.Where(s=>s.Enabled&&OfflineMapSourceIdentity.DownloadRestriction(s) is null).Select(s=>s.DeepCopy()).ToArray();SourceBox.ItemsSource=sources;SourceBox.SelectedItem=sources.FirstOrDefault(s=>s.Id==id)??sources.FirstOrDefault();_loading=false;LoadZooms();}
    private async Task Act(Func<Task> action){try{await action();RefreshLists();}catch(Exception error){StatusText.Text=error.Message;}}
}




