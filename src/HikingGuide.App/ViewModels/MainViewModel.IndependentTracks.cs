using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed partial class MainViewModel
{
    private void ClearMapTrackSelection()
    {
        _selectedNavigationKeys.Clear();_selectedProjectItems.Clear();
        _selectedSplitSectionIds.Clear();_selectedSplitTrackId=null;
        _selectionAnchorKey=null;_primaryCandidateKeys.Clear();_cancelledPrimaryKey=null;
        ClearUnifiedPointSelection();_trackEditPick=null;_trackRangeStart=null;_trackRangeEnd=null;
        _trackClickMode=TrackClickMode.SelectPoint;
        if(_trackEditTrack is {} editTrack&&_trackEditSessionId is {} session)
        {
            _=Map.SetTrackEditHandlesAsync(editTrack,session,[]);
            _=Map.PreviewTrackRangeAsync(editTrack,session,new TrackRangePreview([],0,0,0,0));
        }
        OnPropertyChanged(nameof(SelectedTrackEditPointId));
        SelectNavigationNode(null,false);
        foreach(var node in _mapNodes.Values)node.IsSelected=false;
        SyncNavigationSelection();
        _=Map.SetSplitSectionSelectionAsync([]);
        _=Map.SelectObjectAsync(null);
    }

    private void ConvertLegacySplitTracks()
    {
        foreach(var context in _projects.OpenProjects)
        foreach(var track in context.Project.Tracks.ToArray())
        {
            if(track.GetSplitSections().Count<2)continue;
            // 主攻略轨迹受既有保护规则约束，保留其攻略关系，普通轨迹转换不涉及主线。
            if(track.Id==context.Project.MainGuideTrackId)continue;
            new IndependentTracks(context.Project,track).Apply();
        }
    }
}
