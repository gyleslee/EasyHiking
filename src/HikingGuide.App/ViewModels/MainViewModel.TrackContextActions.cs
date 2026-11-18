using System.Windows.Input;
using HikingGuide.Application.Maps;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed partial class MainViewModel
{
    private RelayCommand<MapEventMessage>? _cutFromContext, _hideFromContext, _addWaypointOnTrack, _addGuideOnTrack;
    public ICommand CutJoinFromContextCommand => _cutFromContext ??= new(v => _ = BeginContextCutAsync(v),
        v => CanTrackContextAction(v) && _trackEditSessionId is not null);
    public ICommand HideTrackContextCommand => _hideFromContext ??= new(v => _ = HideContextGeometryAsync(v),
        v => CanTrackContextAction(v, allowTerrain: true));
    public ICommand AddWaypointOnTrackCommand => _addWaypointOnTrack ??= new(v => _ = AddMarkerOnTrackAsync(v, false), v => CanTrackContextAction(v));
    public ICommand AddGuideOnTrackCommand => _addGuideOnTrack ??= new(v => _ = AddMarkerOnTrackAsync(v, true),
        v => CanTrackContextAction(v) && CurrentProject?.MainGuideTrackId == v?.TrackId);
    private bool CanTrackContextAction(MapEventMessage? value, bool allowTerrain = false) => !IsBusy && value is not null &&
        Map.IsTrackMapContextCurrent(value) && (allowTerrain || !Map.TerrainCoordinateEditingBlocked);
    private void RaiseTrackContextActions()
    {
        _cutFromContext?.RaiseCanExecuteChanged(); _hideFromContext?.RaiseCanExecuteChanged();
        _addWaypointOnTrack?.RaiseCanExecuteChanged(); _addGuideOnTrack?.RaiseCanExecuteChanged();
    }
    private async Task BeginContextCutAsync(MapEventMessage? value)
    {
        if (!CanTrackContextAction(value) || _trackEditSessionId is null || CurrentProject is not { } project ||
            _trackEditTrack is not { } track || value?.Latitude is not double lat || value.Longitude is not double lon) return;
        await RunOperationAsync("正在确定裁剪起点……", async token =>
        {
            var position = await Task.Run(() => new TrackContextPositionResolver().Resolve(track, new(lat, lon), value.SplitSectionId), token);
            if (!Map.IsTrackMapContextCurrent(value) || !ReferenceEquals(CurrentProject, project)) return;
            BeginTrackClickMode(TrackClickMode.SelectRange);
            _rangeScopeSectionId = value.SplitSectionId; _trackRangeStart = position; _trackRangeEnd = null; _cutJoinPending = true;
            StatusText = $"已将右击位置设为裁剪起点（距起点 {position.DistanceFromTrackStart / 1000:F2} km），请点击第二个边界点。Esc取消。";
        });
    }
    private void SetSplitSectionMapVisibility(Guid sectionId, bool visible)
    {
        if (CurrentProject is not { } project) return;
        var track = project.Tracks.FirstOrDefault(t => t.GetSplitSections().Any(s => s.Id == sectionId));
        if (track is null) return;
        MapLayerVisibility.SetSplitSectionVisible(project.Settings.Map, sectionId, visible); project.MarkDirty();
        OnPropertyChanged(nameof(WindowTitle));
        _ = RefreshContextVisibilityAsync(track.Id);
    }
    private async Task HideContextGeometryAsync(MapEventMessage? value)
    {
        if (!CanTrackContextAction(value, true) || CurrentProject is not { } project || value?.TrackId is not Guid trackId) return;
        await RunOperationAsync("正在隐藏轨迹……", async _ =>
        {
            var partId = value.SplitSectionId;
            var wasVisible = partId is Guid id ? MapLayerVisibility.IsSplitSectionVisible(project.Settings.Map, id)
                : MapLayerVisibility.IsTrackVisible(project.Settings.Map, trackId);
            void Set(bool visible)
            {
                if (partId is Guid sid) MapLayerVisibility.SetSplitSectionVisible(project.Settings.Map, sid, visible);
                else MapLayerVisibility.SetTrackVisible(project.Settings.Map, trackId, visible);
                project.MarkDirty();
            }
            _history.Execute("隐藏轨迹", () => Set(false), () => Set(wasVisible));
            RefreshProjectView(false);
            await RefreshContextVisibilityAsync(trackId);
            StatusText = partId is null ? "已隐藏该轨迹及其所属标注；可在项目树勾选重新显示，或撤销。"
                : "已隐藏当前拆分分段，其他分段保持显示；可在项目树勾选重新显示，或撤销。";
        });
    }
    private async Task RefreshContextVisibilityAsync(Guid trackId)
    {
        ClearUnifiedPointSelection(); _trackEditPick = null; _trackRangeStart = null; _trackRangeEnd = null; _trackClickMode = TrackClickMode.SelectPoint;
        if (_trackEditTrack is { } editing && editing.Id == trackId && _trackEditProject is { } project && _trackEditState.IsActive)
        {
            _trackEditState.SelectTarget(project, editing, renewSession: true);
            _trackEditContextChangeTask = Map.BeginTrackEditAsync(editing, _trackEditSessionId!.Value, revealHiddenTrack: false);
            await _trackEditContextChangeTask;
        }
        await Map.RefreshVisibilityAsync(); RaiseCommandStates();
    }
    private async Task AddMarkerOnTrackAsync(MapEventMessage? value, bool guide)
    {
        if (!CanTrackContextAction(value) || CurrentProject is not { } project || value?.Latitude is not double lat || value.Longitude is not double lon ||
            project.Tracks.FirstOrDefault(t => t.Id == value.TrackId) is not { } track) return;
        if (guide && project.MainGuideTrackId != track.Id)
        { _dialogs.ShowWarning("请先设置主攻略轨迹", "攻略节点只能添加到主攻略轨迹。请先将该轨迹设为主攻略轨迹，再添加攻略节点。"); return; }
        MapMarkerBase? added = null;
        await RunOperationAsync("正在吸附到轨迹GPS点……", async token =>
        {
            var position = await Task.Run(() => new TrackContextPositionResolver().Resolve(track, new(lat, lon), value.SplitSectionId, useGpsPoint: true), token);
            if (!Map.IsTrackMapContextCurrent(value) || !ReferenceEquals(project, CurrentProject)) return;
            if (guide)
            {
                if (project.MainGuideTrackId != track.Id) return;
                var node = new GuideNode { Name = "新攻略节点", TrackId = track.Id, OriginalCoordinate = position.SnappedCoordinate,
                    PreferredTrackPosition = position };
                _guideStructure.ApplyPosition(node, position);
                var old = project.GuideSegments.ToArray(); var oldRetired = project.RetiredGuideSegments.ToArray();
                GuideSegment[]? rebuilt = null;
                _history.Execute("在轨迹上添加攻略节点", () =>
                {
                    project.GuideNodes.Add(node); rebuilt ??= _guideStructure.RebuildSegments(project).ToArray();
                    ReplaceGuideSegments(project, rebuilt); project.NotifyGuideNodeChanged();
                }, () =>
                {
                    project.GuideNodes.Remove(node); ReplaceGuideSegments(project, old);
                    project.RetiredGuideSegments.Clear(); foreach (var part in oldRetired) project.RetiredGuideSegments.Add(part);
                    project.NotifyGuideNodeChanged();
                });
                added = node;
            }
            else
            {
                var node = new Waypoint { Name = "新标注", AssociatedTrackId = track.Id, Latitude = position.SnappedCoordinate.Latitude,
                    Longitude = position.SnappedCoordinate.Longitude, PreferredTrackPosition = position };
                _history.Execute("在轨迹上添加普通标注", () => project.Waypoints.Add(node), () => project.Waypoints.Remove(node));
                added = node;
            }
            RefreshProjectView(); await _mapRefreshTask;
            StatusText = guide ? "已在该轨迹GPS点添加攻略节点。" : "已在该轨迹GPS点添加普通标注，并关联到该轨迹。";
        });
        if (added is not null && !AutoConfirmTrackEditForAutomation && ReferenceEquals(project, CurrentProject)) await EditMarkerAsync(added);
    }
}
