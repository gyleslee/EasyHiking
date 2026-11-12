using System.Windows.Input;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Application.TrackEditing;
namespace HikingGuide.App.ViewModels;
public sealed partial class MainViewModel
{
    private RelayCommand<Track> _showKilometers=null!,_hideKilometers=null!;
    private RelayCommand<HikingProject> _showProjectKilometers=null!,_hideProjectKilometers=null!;
    private RelayCommand _kilometerSettings=null!;
    public ICommand ShowKilometersCommand=>_showKilometers;public ICommand HideKilometersCommand=>_hideKilometers;
    public ICommand ShowProjectKilometersCommand=>_showProjectKilometers;public ICommand HideProjectKilometersCommand=>_hideProjectKilometers;
    public ICommand KilometerSettingsCommand=>_kilometerSettings;
    private void InitializeKilometerCommands()
    {
        bool Can(Track? t)=>!IsBusy&&(SelectedTracks().Length>1?SelectedTracks():t is null?SelectedTracks():[t]).Any(x=>new TrackKilometerService().Assess(x).Eligible);
        _showKilometers=new(t=>SetTrackKilometers(t,true),Can);_hideKilometers=new(t=>SetTrackKilometers(t,false),Can);
        _showProjectKilometers=new(p=>{if(p is not null)ApplyKilometers(p,p.Tracks,true);},p=>!IsBusy&&p is not null&&p.Tracks.Any(t=>new TrackKilometerService().Assess(t).Eligible));
        _hideProjectKilometers=new(p=>{if(p is not null)ApplyKilometers(p,p.Tracks,false);},p=>!IsBusy&&p is not null&&p.Tracks.Any(t=>new TrackKilometerService().Assess(t).Eligible));
        _kilometerSettings=new(()=>_=OpenKilometerSettingsAsync(),()=>!IsBusy&&_projects.OpenProjects.Count>0);
    }
    private void RaiseKilometerCommands(){_showKilometers?.RaiseCanExecuteChanged();_hideKilometers?.RaiseCanExecuteChanged();_showProjectKilometers?.RaiseCanExecuteChanged();_hideProjectKilometers?.RaiseCanExecuteChanged();_kilometerSettings?.RaiseCanExecuteChanged();}
    private void SetTrackKilometers(Track? clicked,bool visible)
    {
        if(CurrentProject is not {} project)return;var selected=SelectedTracks();var targets=selected.Length>1?selected:clicked is null?selected:[clicked];ApplyKilometers(project,targets,visible);
    }
    internal void ApplyKilometers(HikingProject project,IEnumerable<Track> tracks,bool visible)
    {
        var targets=tracks.Where(project.Tracks.Contains).Where(t=>!visible||new TrackKilometerService().Assess(t).Eligible).Distinct().ToArray();
        var changed=targets.Where(t=>TrackKilometerService.IsEnabled(t)!=visible||visible&&(KilometerRouteDirections.NeedsConfirmation(t)||TrackKilometerService.RequiresKindConfirmation(t))).ToArray();if(changed.Length==0)return;
        var kinds=new Dictionary<Guid,string?>();
        foreach(var t in changed.Where(t=>visible&&TrackKilometerService.RequiresKindConfirmation(t)))
        {
            var choice=System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,$"轨迹“{t.Name}”含间隙，请确认路线性质。\n选择“是”：一条行进路线；选择“否”：轨迹合集（不显示公里标识）；取消：不应用本次设置。","确认路线性质",System.Windows.MessageBoxButton.YesNoCancel);
            if(choice==System.Windows.MessageBoxResult.Cancel)return;kinds[t.Id]=choice==System.Windows.MessageBoxResult.Yes?"route":"collection";
        }
        var directions=new Dictionary<Guid,string?>();
        var unresolved=changed.Where(t=>visible&&kinds.GetValueOrDefault(t.Id)!="collection"&&KilometerRouteDirections.NeedsConfirmation(t)).ToArray();
        var random=false;
        if(unresolved.Length>0&&targets.Length>1){var choice=System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,"部分轨迹的各段方向相冲。选择“是”逐条设置起终方向，选择“否”由程序衔接各段并随机选择整体起终方向。取消则不应用。","公里标识起终方向",System.Windows.MessageBoxButton.YesNoCancel);if(choice==System.Windows.MessageBoxResult.Cancel)return;random=choice==System.Windows.MessageBoxResult.No;}
        foreach(var t in unresolved)
        {
            IReadOnlyList<KilometerRouteDirections.Part>? parts;
            if(random){parts=KilometerRouteDirections.Suggest(t);if(Random.Shared.Next(2)==1)parts=KilometerRouteDirections.Reverse(parts);}
            else{var dialog=new KilometerDirectionWindow(t,c=>_=Map.FocusCoordinateAsync(c)){Owner=System.Windows.Application.Current.MainWindow};if(dialog.ShowDialog()!=true)return;parts=dialog.Result;}
            directions[t.Id]=KilometerRouteDirections.Serialize(t,parts!);
        }
        var previous=changed.ToDictionary(t=>t.Id,t=>(Enabled:TrackKilometerService.IsEnabled(t),Directions:t.Metadata.GetValueOrDefault(KilometerRouteDirections.Key),Kind:t.Metadata.GetValueOrDefault(TrackKilometerService.RouteKindKey)));var next=changed.ToDictionary(t=>t.Id,t=>(Enabled:visible&&kinds.GetValueOrDefault(t.Id)!="collection",Directions:directions.GetValueOrDefault(t.Id,t.Metadata.GetValueOrDefault(KilometerRouteDirections.Key)),Kind:kinds.GetValueOrDefault(t.Id,t.Metadata.GetValueOrDefault(TrackKilometerService.RouteKindKey))));
        var context=_projects.OpenProjects.FirstOrDefault(c=>ReferenceEquals(c.Project,project));if(context is null)return;
        if(!_histories.TryGetValue(context.ContextId,out var history)){history=new ProjectEditHistory{Project=project};history.Changed+=(_,_)=>{((RelayCommand)UndoCommand).RaiseCanExecuteChanged();((RelayCommand)RedoCommand).RaiseCanExecuteChanged();};_histories[context.ContextId]=history;}
        history.Execute(visible?"显示轨迹公里数标识":"隐藏轨迹公里数标识",()=>Apply(next),()=>Apply(previous));
        RefreshProjectView(false);if(ReferenceEquals(CurrentProject,project))_=Map.RefreshVisibilityAsync();
        void Apply(IReadOnlyDictionary<Guid,(bool Enabled,string? Directions,string? Kind)> states)=>project.ExecuteAtomicUpdate(p=>{foreach(var t in changed){TrackKilometerService.SetEnabled(t,states[t.Id].Enabled);if(states[t.Id].Kind is {} kind)t.Metadata[TrackKilometerService.RouteKindKey]=kind;else t.Metadata.Remove(TrackKilometerService.RouteKindKey);if(states[t.Id].Directions is {} value)t.Metadata[KilometerRouteDirections.Key]=value;else t.Metadata.Remove(KilometerRouteDirections.Key);}p.MarkDirty();});
    }

    private async Task OpenKilometerSettingsAsync()
    {
        var projects=_projects.OpenProjects.Select(c=>c.Project).ToArray();var rows=await Task.Run(()=>projects.Select(p=>new KilometerProjectRow(p,p.Tracks.Count(t=>new TrackKilometerService().Assess(t).Eligible),p.Tracks.Count(t=>TrackKilometerService.IsEnabled(t)&&new TrackKilometerService().Assess(t).Eligible))).ToArray());
        new KilometerSettingsWindow(rows,ApplyKilometers){Owner=System.Windows.Application.Current.MainWindow}.ShowDialog();
    }
}
