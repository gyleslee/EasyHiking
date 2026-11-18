using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HikingGuide.Application.Routing;
using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed partial class MainViewModel
{
    private Track? _continuationTrack;
    private long _continuationRevision;
    private RelayCommand<Track>? _continueRoute;
    public ICommand ContinueRoutePlanCommand => _continueRoute??=new(t=>_=ContinueRouteAsync(t),
        t=>!IsBusy&&!_routePlanningBusy&&(t??_selectedModel as Track??CurrentProject?.ActiveTrack)?.EndPoint is not null);

    private async Task ContinueRouteAsync(Track? selected)
    {
        try { await ContinueRouteCoreAsync(selected); }
        catch(Exception exception) { _log.Error(exception,"[RoutePlan] 继续规划失败"); _dialogs.ShowError("继续规划失败",exception.Message); }
    }
    private async Task ContinueRouteCoreAsync(Track? selected)
    {
        var track=selected??_selectedModel as Track??CurrentProject?.ActiveTrack;
        if(CurrentProject is not{} project||track?.EndPoint is not{} end||!project.Tracks.Contains(track))return;
        var mode=new ComboBox {ItemsSource=new[]{"BRouter路由规划","手绘规划","混合规划"},SelectedIndex=2,Margin=new(0,8,0,14)};
        var panel=new StackPanel {Margin=new(18)};
        var window=new Window {Title="继续规划路线",Width=370,Height=215,ResizeMode=ResizeMode.NoResize,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=System.Windows.Application.Current.MainWindow,Content=panel};
        panel.Children.Add(new TextBlock {Text=$"从“{track.Name}”的终点继续规划",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(mode);
        var start=new Button {Content="开始继续规划",Height=30};start.Click+=(_,_)=>window.DialogResult=true;panel.Children.Add(start);
        if(!AutoConfirmTrackEditForAutomation&&window.ShowDialog()!=true)return;
        if(_routePlan is not null)
        {
            if(_routePlan.Plan.ControlPoints.Count>0&&!AutoConfirmTrackEditForAutomation&&!ConfirmDiscardRoutePlan())return;
            CancelRoutePlan(false);
        }
        if(IsTrackEditActive)await ExitTrackEditAsync();
        await StartRoutePlanAsync((RoutePlanningMode)mode.SelectedIndex);
        if(_routePlan is not{} service)return;
        _continuationTrack=track;_continuationRevision=track.GeometryRevision;
        await AddRoutePointAsync(service,end.Coordinate);
        RoutePlanningStatus="已固定原轨迹终点。请在地图点击下一点；完成后追加到原轨迹，名称、颜色和标注保持不变。";
    }
}
