using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Infrastructure.Elevation;

namespace HikingGuide.App;

/// <summary>仅安装版首次正常启动自动显示；不代替用户选择目录、发起下载或确认高程来源。</summary>
public sealed class FirstRunSetupWindow : Window
{
    private readonly App _host;
    private readonly MapViewModel _map;
    private readonly ComboBox _dem = new() { MinWidth = 350, DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new(0,8,0,12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0,8,0,8) };
    private bool _saving, _closeApproved;
    internal event Action<Window>? StepOpened;
    internal event Action? StepsFinished;
    internal Button FinishButton { get; } = new() { Content = "完成设置", Padding = new(16,6,16,6), Margin = new(6) };

    public FirstRunSetupWindow(Window owner,MapViewModel map)
    {
        _host=(App)System.Windows.Application.Current;_map=map;Owner=owner;
        Title="首次使用设置向导";Width=760;Height=450;MinWidth=650;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new(22)};Content=panel;
        panel.Children.Add(new TextBlock{Text="准备 DEM 高程与 BRouter 路由数据",FontSize=21,FontWeight=FontWeights.Bold,Margin=new(0,0,0,12)});
        panel.Children.Add(new TextBlock{Text="将依次打开两项设置。您可以现在配置，也可关闭设置窗口后选择稍后设置；之后可从“设置”菜单重新打开向导。",TextWrapping=TextWrapping.Wrap});
        var buttons=new WrapPanel{Margin=new(0,12,0,8)};panel.Children.Add(buttons);
        Add(buttons,"1．挂载 DEM 高程文件…",OpenDem);
        Add(buttons,"2．导入或下载 BRouter 数据…",OpenRouting);
        panel.Children.Add(new TextBlock{Text="统一地图DEM（3D地形、等高线与阴影共用）：",FontWeight=FontWeights.Bold});panel.Children.Add(_dem);
        panel.Children.Add(new TextBlock{Text="默认启用等高线和阴影叠加，阴影不透明度为30%。请先挂载并选择可用DEM；未配置或不覆盖当前地图时不生成地形。此选择仅用于地图显示，不改变攻略最终高程来源。",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(_status);
        var footer=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(footer);
        Add(footer,"稍后设置",()=>_ = FinishAsync());footer.Children.Add(FinishButton);FinishButton.Click+=async(_,_)=>await FinishAsync();
        Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>
        {
            if(!IsVisible)return;
            OpenDem();if(!IsVisible)return;OpenRouting();RefreshDem();StepsFinished?.Invoke();
        }),DispatcherPriority.Background);
        Closing+=async(_,e)=>{if(_closeApproved)return;e.Cancel=true;await FinishAsync();};
        RefreshDem();
    }
    private static void Add(Panel panel,string text,Action action)
    {var b=new Button{Content=text,Padding=new(10,6,10,6),Margin=new(0,0,8,0)};b.Click+=(_,_)=>action();panel.Children.Add(b);}
    private void RefreshDem()
    {
        var selected=_dem.SelectedValue as Guid? ?? _host.GlobalSettingsService.Current.OfflineTerrain.DemDatabaseId;
        var choices=_host.DemCatalog.Databases.Where(d=>d.Status==DemDatabaseStatus.Ready).ToArray();
        _dem.ItemsSource=choices;_dem.SelectedValue=selected;
        if(_dem.SelectedIndex<0&&choices.Length==1)_dem.SelectedIndex=0;
        _status.Text=choices.Length==0?"尚未挂载可用DEM，等高线和阴影暂不生成。BRouter数据请手动导入rd5或确认下载；向导不会自动下载。":"请选择地图地形显示使用的DEM。BRouter设置完成后可关闭其窗口返回向导。";
    }
    private void OpenDem()
    {
        var dialog=new DemDatabaseWindow(_host.DemCatalog){Owner=this,Title="首次设置 1/2：挂载 DEM 高程文件"};
        StepOpened?.Invoke(dialog);dialog.ShowDialog();RefreshDem();
        if(Owner is MainWindow window)window.RefreshP8Sources();
    }
    private void OpenRouting()
    {
        if(_host.BRouterController is null)return;
        var dialog=new BRouterSettingsWindow(_host.BRouterController){Owner=this,Title="首次设置 2/2：BRouter 导入或下载路由数据"};
        dialog.ProgressText.Text="已有rd5文件请使用“导入已有BRouter路由数据”；没有数据可选择中国70瓦片范围或手动选择世界范围。下载需要您确认。完成后关闭本窗口。";
        StepOpened?.Invoke(dialog);dialog.ShowDialog();
    }
    internal async Task FinishAsync()
    {
        if(_saving||_closeApproved)return;_saving=true;
        var settings=_host.GlobalSettingsService.Current;var old=settings.OfflineTerrain;var oldTerrain=settings.Terrain3D;var wasCompleted=settings.FirstRunSetupCompleted;
        try
        {
            if(_dem.SelectedValue is Guid id){settings.Terrain3D=(settings.Terrain3D??new()) with{TerrainDemDatabaseId=id};settings.OfflineTerrain=old with{DemDatabaseId=id};}
            settings.FirstRunSetupCompleted=true;
            await _host.GlobalSettingsService.SaveAsync();
            try { await _map.ApplyGlobalMapLayersAsync(true); }
            catch(Exception error){_host.LoggingService.Error(error,"初始设置已保存，地图图层暂未刷新");}
            _closeApproved=true;await Dispatcher.InvokeAsync(Close,DispatcherPriority.Background);
        }
        catch(Exception error)
        {
            settings.FirstRunSetupCompleted=wasCompleted;settings.OfflineTerrain=old;settings.Terrain3D=oldTerrain;
            _status.Text="设置尚未保存，请重试："+error.Message;
        }
        finally{_saving=false;}
    }
}

