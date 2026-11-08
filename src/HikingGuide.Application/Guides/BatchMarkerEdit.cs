using HikingGuide.Core.Models;

namespace HikingGuide.Application.Guides;

public sealed record MarkerFieldPatch(bool ApplyName = false, string? Name = null,
    bool ApplyDescription = false, string? Description = null,
    bool ApplyType = false, WaypointType Type = WaypointType.Normal, string? CustomTypeName = null,
    bool ApplyGuideState = false, bool IsGuideNode = false, bool ApplyTrack = false, Guid? TrackId = null);

/// <summary>只修改明确选中的字段，不改变坐标、身份、图片或人工攻略数据。</summary>
public sealed class BatchMarkerEdit
{
    public BatchMarkerEditPlan Prepare(HikingProject project, IReadOnlyList<MapMarkerBase> markers, MarkerFieldPatch patch,
        CancellationToken cancellationToken = default)
    {
        if (markers.Count == 0 || markers.Select(m => m.Id).Distinct().Count() != markers.Count)
            throw new InvalidOperationException("请选择不同的普通标注点或攻略节点。");
        if (!patch.ApplyName && !patch.ApplyDescription && !patch.ApplyType && !patch.ApplyGuideState && !patch.ApplyTrack)
            throw new InvalidOperationException("请勾选至少一个需要修改的字段。");
        if (patch.ApplyType && patch.Type == WaypointType.Custom && string.IsNullOrWhiteSpace(patch.CustomTypeName))
            throw new InvalidOperationException("请填写自定义类型名称。");
        if (markers.Any(m => m switch { Waypoint w => !project.Waypoints.Contains(w), GuideNode n => !project.GuideNodes.Contains(n), _ => true }))
            throw new InvalidOperationException("所选标注不属于当前项目。");
        if (patch.ApplyGuideState && patch.IsGuideNode && project.MainGuideTrack is null)
            throw new InvalidOperationException("请先设置主攻略轨迹，再设为攻略节点。");
        if (patch.ApplyTrack && patch.TrackId.HasValue && !project.Tracks.Any(t => t.Id == patch.TrackId))
            throw new InvalidOperationException("普通标注所属轨迹不存在，请重新选择。");
        cancellationToken.ThrowIfCancellationRequested();
        BatchWaypointGuideConversionPlan? promote = null;
        BatchGuideWaypointConversionPlan? demote = null;
        if (patch.ApplyGuideState && patch.IsGuideNode && markers.OfType<Waypoint>().Any())
            promote = new BatchWaypointGuideConversion().Prepare(project, markers.OfType<Waypoint>().ToArray(), cancellationToken);
        if (patch.ApplyGuideState && !patch.IsGuideNode && markers.OfType<GuideNode>().Any())
            demote = new BatchGuideWaypointConversion().Prepare(project, markers.OfType<GuideNode>().ToArray(), cancellationToken);
        var replacements = (promote?.NewNodes.Cast<MapMarkerBase>() ?? []).Concat(demote?.NewWaypoints ?? []).ToDictionary(m => m.Id);
        return new(project, markers.Select(m => replacements.GetValueOrDefault(m.Id, m)).ToArray(), patch, promote, demote);
    }
}

public sealed class BatchMarkerEditPlan
{
    private readonly HikingProject _project;
    private readonly MarkerFieldPatch _patch;
    private readonly BatchWaypointGuideConversionPlan? _promote;
    private readonly BatchGuideWaypointConversionPlan? _demote;
    private readonly Guid? _mainTrackId;
    public int FarCount(double threshold) => _promote?.FarCount(threshold) ?? 0;
    private readonly (MapMarkerBase Marker, string? Name, string? Description, WaypointType Type, string? Custom)[] _before;
    private readonly (Waypoint Point, Guid? Owner)[] _owners;
    internal BatchMarkerEditPlan(HikingProject project, IReadOnlyList<MapMarkerBase> markers, MarkerFieldPatch patch,
        BatchWaypointGuideConversionPlan? promote, BatchGuideWaypointConversionPlan? demote)
    {
        _project = project; _patch = patch;
        _promote = promote; _demote = demote; _mainTrackId = project.MainGuideTrackId;
        _before = markers.Select(m => (m, m.Name, m.Description, m.Type, m.CustomTypeName)).ToArray();
        _owners = markers.OfType<Waypoint>().Select(w => (w, w.AssociatedTrackId)).ToArray();
    }
    public void Apply() => _project.ExecuteAtomicUpdate(p =>
    {
        if (_patch.ApplyGuideState && _patch.IsGuideNode && p.MainGuideTrackId != _mainTrackId)
            throw new InvalidOperationException("主攻略轨迹已变化，请重新打开批量编辑窗口。");
        if (_patch.ApplyTrack && _patch.TrackId.HasValue && !p.Tracks.Any(t => t.Id == _patch.TrackId))
            throw new InvalidOperationException("普通标注所属轨迹已删除，请重新选择。");
        _promote?.Apply(); _demote?.Apply();
        foreach (var item in _before)
        {
            if (_patch.ApplyName) item.Marker.Name = _patch.Name;
            if (_patch.ApplyDescription) item.Marker.Description = _patch.Description;
            if (_patch.ApplyType) { item.Marker.Type = _patch.Type; item.Marker.CustomTypeName = _patch.Type == WaypointType.Custom ? _patch.CustomTypeName : null; }
        }
        if (_patch.ApplyTrack) foreach (var item in _owners) item.Point.AssociatedTrackId = _patch.TrackId;
        p.MarkDirty();
    });
    public void Undo() => _project.ExecuteAtomicUpdate(p =>
    {
        foreach (var item in _before)
        {
            if (_patch.ApplyName) item.Marker.Name = item.Name;
            if (_patch.ApplyDescription) item.Marker.Description = item.Description;
            if (_patch.ApplyType) { item.Marker.Type = item.Type; item.Marker.CustomTypeName = item.Custom; }
        }
        if (_patch.ApplyTrack) foreach (var item in _owners) item.Point.AssociatedTrackId = item.Owner;
        _demote?.Undo(); _promote?.Undo();
        p.MarkDirty();
    });
}
