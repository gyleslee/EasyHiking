using System.Windows;
using System.Windows.Input;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed partial class MainViewModel
{
    private AsyncRelayCommand? _deleteGeometryUnified, _reverseGeometryUnified;
    private RelayCommand? _cutJoinUnified;
    private RelayCommand<MapEventMessage>? _trimBeforeUnified, _trimAfterUnified;
    private readonly Dictionary<Guid, TrackEditHandle> _selectedEditPoints = [];
    private Guid? _rangeScopeSectionId;
    private bool _cutJoinPending;
    public ICommand DeleteGeometryCommand => _deleteGeometryUnified ??= new(() => EditGeometrySelectionAsync(true), CanEditGeometrySelection);
    public ICommand ReverseGeometryCommand => _reverseGeometryUnified ??= new(() => EditGeometrySelectionAsync(false), CanEditGeometrySelection);
    public ICommand CutJoinRangeCommand => _cutJoinUnified ??= new(() =>
    {
        var scope = (_selectedModel as SplitSection)?.Id;
        BeginTrackClickMode(TrackClickMode.SelectRange);
        _rangeScopeSectionId = scope;
        _cutJoinPending = true;
        StatusText = "区间裁剪：请在目标轨迹上依次选择两个边界点，确认后删除中间轨迹并直线连接。Esc取消。";
    }, () => !IsBusy && !Map.TerrainCoordinateEditingBlocked && _trackEditSessionId is not null);
    public ICommand TrimBeforeCommand => _trimBeforeUnified ??= new(v => _ = TrimAtAsync(v, true), CanTrimAt);
    public ICommand TrimAfterCommand => _trimAfterUnified ??= new(v => _ = TrimAtAsync(v, false), CanTrimAt);

    private bool CanTrimAt(MapEventMessage? value) => !IsBusy && !Map.TerrainCoordinateEditingBlocked &&
        _trackEditSessionId is not null && (value is null ? _trackEditPick is not null : Map.IsTrackMapContextCurrent(value));
    private bool CanEditGeometrySelection() => !IsBusy && !Map.TerrainCoordinateEditingBlocked && GeometrySelection().Length > 0;
    private TrackGeometrySelection[] GeometrySelection()
    {
        if (CurrentProject is not { } project) return [];
        var whole = SelectedTracks().Select(t => t.Id).ToHashSet();
        var result = whole.Select(id => new TrackGeometrySelection(id)).ToList();
        foreach (var track in project.Tracks.Where(t => !whole.Contains(t.Id)))
        {
            var ids = track.GetSplitSections().Where(s => _selectedSplitSectionIds.Contains(s.Id)).Select(s => s.Id).ToHashSet();
            if (ids.Count > 0) result.Add(new(track.Id, ids));
        }
        if (result.Count == 0)
        {
            if (_selectedModel is Track t && project.Tracks.Contains(t)) result.Add(new(t.Id));
            else if (_selectedModel is SplitSection s && project.Tracks.Any(t => t.Id == s.TrackId)) result.Add(new(s.TrackId, new HashSet<Guid>{s.Id}));
        }
        return result.ToArray();
    }

    private async Task EditGeometrySelectionAsync(bool delete, IReadOnlySet<Guid>? explicitMarkerIds = null)
    {
        if (CurrentProject is not { } project) return;
        var selection = GeometrySelection(); if (selection.Length == 0) return;
        var operation = delete ? "删除轨迹" : "反转轨迹";
        await RunOperationAsync($"正在准备{operation}……", async token =>
        {
            TrackGeometryBatchPlan plan;
            try { plan = await Task.Run(() => TrackGeometryBatchPlan.Prepare(project, selection, delete, token), token); }
            catch (InvalidOperationException e) { _dialogs.ShowWarning($"无法{operation}", e.Message); return; }
            var message = delete
                ? "将删除选中的整条轨迹或拆分分段；选中某条轨迹的全部分段时，会删除整条轨迹。有关联标注时，接下来可选择一并删除或保留。\n\n原始文件不会删除，操作可一次撤销。是否继续？"
                : "将反转所选整条轨迹或拆分分段的方向，未选中的分段保持不变。源时间及节点的起终点类型不会自动修改，请在完成后核对。\n\n操作可一次撤销。是否继续？";
            if (!AutoConfirmTrackEditForAutomation && MessageBox.Show(System.Windows.Application.Current.MainWindow,
                    message, $"确认{operation}", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (!ReferenceEquals(project, CurrentProject)) return;
            var related = plan.RelatedMarkerIds.Where(id => explicitMarkerIds?.Contains(id) != true).ToHashSet();
            var removeMarkers = delete ? ChooseRelatedMarkerDeletion(project, related) : false;
            if (removeMarkers is null) return;
            var deletionIds = (removeMarkers.Value ? related : []).Concat(explicitMarkerIds ?? new HashSet<Guid>()).ToHashSet();
            ExecuteGeometryHistory(operation, project, plan.Apply, plan.Undo, deletionIds, deletionIds.Count > 0);
            foreach (var item in selection) { Map.InvalidateTrack(item.TrackId); _trackEditIndex.Invalidate(item.TrackId); }
            ClearUnifiedPointSelection();
            if (delete) { _selectedProjectItems.Clear(); _selectedNavigationKeys.Clear(); }
            RefreshProjectView(); await _mapRefreshTask;
            StatusText = $"{operation}已完成，可一次撤销。";
        });
    }

    public bool DeleteSelectionFromKeyboard(bool fromProjectTree)
    {
        var command = !fromProjectTree && _selectedEditPoints.Count > 0 ? DeleteTrackPointCommand : DeleteGeometryCommand;
        if (!command.CanExecute(null)) return false;
        command.Execute(null); return true;
    }
    private void ClearUnifiedPointSelection() { _selectedEditPoints.Clear(); _cutJoinPending = false; _rangeScopeSectionId = null; }
    private void RaiseUnifiedTrackCommands()
    {
        _deleteGeometryUnified?.RaiseCanExecuteChanged(); _reverseGeometryUnified?.RaiseCanExecuteChanged();
        _cutJoinUnified?.RaiseCanExecuteChanged(); _trimBeforeUnified?.RaiseCanExecuteChanged(); _trimAfterUnified?.RaiseCanExecuteChanged();
    }
    private async Task SelectEditPointAsync(Track track, Guid session, TrackPointEditPick pick, bool control)
    {
        if (!control) _selectedEditPoints.Clear();
        if (control && _selectedEditPoints.ContainsKey(pick.SelectedPoint.PointId)) _selectedEditPoints.Remove(pick.SelectedPoint.PointId);
        else _selectedEditPoints[pick.SelectedPoint.PointId] = pick.SelectedPoint;
        var primary = _selectedEditPoints.Values.LastOrDefault();
        _trackEditPick = primary is null ? null : pick with { SelectedPoint = primary, LinePosition = PositionForEditHandle(track, primary) };
        var handles = pick.VisibleHandles.Concat(_selectedEditPoints.Values).DistinctBy(h => h.PointId)
            .Select(h => h with { Selected = _selectedEditPoints.ContainsKey(h.PointId) }).ToArray();
        await Map.SetTrackEditHandlesAsync(track, session, handles);
        OnPropertyChanged(nameof(SelectedTrackEditPointId)); RaiseCommandStates();
        StatusText = $"已选择 {_selectedEditPoints.Count} 个GPS点。Ctrl点击圆点可多选，Delete删除；拖动只移动当前圆点。";
    }

    private static TrackPosition PositionForEditHandle(Track track, TrackEditHandle handle)
    {
        var part = track.Segments[handle.SegmentIndex]; var point = part.Points[handle.PointIndex];
        var edge = Math.Min(handle.PointIndex, part.Points.Count - 2);
        return new TrackPosition { TrackId = track.Id, TrackSegmentId = part.Id, SegmentIndex = handle.SegmentIndex,
            StartPointIndex = edge, EndPointIndex = edge + 1, Fraction = handle.PointIndex == edge ? 0 : 1,
            SnappedCoordinate = point.Coordinate, DistanceFromTrackStart = point.DistanceFromStart,
            DistanceFromSegmentStart = point.DistanceFromStart - part.Points[0].DistanceFromStart,
            TrackGeometryFingerprint = new HikingGuide.Core.Services.TrackSnapService().GetGeometryFingerprint(track) };
    }

    private async Task TrimAtAsync(MapEventMessage? context, bool before)
    {
        if (_trackEditTrack is not { } track || _trackEditProject is not { } project) return;
        var pick = _trackEditPick; var scope = (_selectedModel as SplitSection)?.Id;
        if (context is not null)
        {
            if (!Map.IsTrackMapContextCurrent(context) || context.Latitude is not double lat || context.Longitude is not double lon) return;
            pick = await Task.Run(() => PickTrackEditPoint(track, new(lat, lon), context.Zoom ?? 15, context.SplitSectionId));
            if (!Map.IsTrackMapContextCurrent(context) || !ReferenceEquals(_trackEditProject, project) || !ReferenceEquals(_trackEditTrack, track)) return;
            scope = context.SplitSectionId;
        }
        if (pick is null) return;
        await ApplyTrackEditAsync(before ? "删除此点前轨迹" : "删除此点后轨迹",
            token => _trackEdits.PrepareTrimAtPoint(project, pick.LinePosition, before, scope, token), true);
    }

    internal bool DeleteRelatedMarkersForAutomation { get; set; }
    private bool? ChooseRelatedMarkerDeletion(HikingProject project, IReadOnlySet<Guid> ids)
    {
        if (ids.Count == 0) return false;
        if (AutoConfirmTrackEditForAutomation) return DeleteRelatedMarkersForAutomation;
        var w = project.Waypoints.Count(p => ids.Contains(p.Id)); var g = project.GuideNodes.Count(p => ids.Contains(p.Id));
        var answer = MessageBox.Show(System.Windows.Application.Current.MainWindow,
            $"本次删除范围内关联了 {w} 个普通标注点、{g} 个攻略节点。是否一并删除？\n\n" +
            "是：同时删除这些标注及其项目内图片关联。\n否：保留标注，必要时转为未关联或失联状态。\n取消：不执行本次轨迹删除。\n\n" +
            "默认保留。删除操作和相关标注均可一次撤销恢复；原始文件不删除。",
            "是否同时删除相关标注点", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        return answer == MessageBoxResult.Cancel ? null : answer == MessageBoxResult.Yes;
    }
    private void ExecuteGeometryHistory(string operation, HikingProject project, Action apply, Action undo,
        IReadOnlySet<Guid> ids, bool removeMarkers)
    {
        RelatedMarkerDeletion? deletion = null;
        _history.Execute(operation, () =>
        {
            apply();
            try { if (removeMarkers) { deletion = new(project, ids); deletion.Apply(); } }
            catch { undo(); throw; }
        }, () => { deletion?.Undo(); undo(); });
    }
}
