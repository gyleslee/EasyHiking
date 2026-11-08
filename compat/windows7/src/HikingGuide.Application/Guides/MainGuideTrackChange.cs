using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Core.Guides;
using System.Text.Json;

namespace HikingGuide.Application.Guides;

/// <summary>主攻略身份与其所属标注身份一起转换，预计算不修改实时项目。</summary>
public sealed class MainGuideTrackChange
{
    private readonly HikingProject _project;
    private readonly Guid? _oldMain, _newMain;
    private readonly GuideNode[] _beforeNodes, _afterNodes;
    private readonly Waypoint[] _beforePoints, _afterPoints;
    private readonly GuideSegment[] _beforeSegments, _beforeRetired, _afterSegments, _afterRetired;
    private readonly (MediaAttachment Item, MediaOwnerType Before, MediaOwnerType After)[] _attachments;
    private readonly (ExternalMediaReference Item, MediaOwnerType Before, MediaOwnerType After)[] _external;
    private readonly (Track Track, long Revision, string Fingerprint)[] _geometry;
    private readonly GuideWorkbenchData _beforeData, _afterData;
    private static GuideWorkbenchData CopyData(GuideWorkbenchData data) => JsonSerializer.Deserialize<GuideWorkbenchData>(JsonSerializer.Serialize(data))!;
    private static string ManualFingerprint(GuideWorkbenchData data) => JsonSerializer.Serialize(new { data.Manual, data.Plans });
    public int ConvertedToWaypointCount { get; }
    public int ConvertedToGuideCount { get; }
    public int FarCount(double threshold) => _afterNodes.Count(n => !_beforeNodes.Contains(n) && n.DistanceToTrack > threshold);

    private MainGuideTrackChange(HikingProject project, Guid? newMain, CancellationToken token)
    {
        _project = project; _oldMain = project.MainGuideTrackId; _newMain = newMain;
        _beforeNodes = project.GuideNodes.ToArray(); _beforePoints = project.Waypoints.ToArray();
        _beforeSegments = project.GuideSegments.ToArray(); _beforeRetired = project.RetiredGuideSegments.ToArray();
        var demoted = _beforeNodes.Where(n => _oldMain.HasValue && n.TrackId == _oldMain && _oldMain != _newMain).ToArray();
        var promoted = _beforePoints.Where(w => _newMain.HasValue && w.AssociatedTrackId == _newMain && _oldMain != _newMain).ToArray();
        ConvertedToWaypointCount = demoted.Length; ConvertedToGuideCount = promoted.Length;
        var points = _beforePoints.Except(promoted).ToList();
        foreach (var node in demoted)
        {
            token.ThrowIfCancellationRequested();
            var point = node.ToWaypoint();
            // 当前所属主线优先于历史来源，保证点跟随要编辑的轨迹。
            point.AssociatedTrackId = _oldMain;
            // 从原始位置重新吸附编辑后的几何，不复用旧轨迹位置引用。
            points.Add(point);
        }
        var nodes = _beforeNodes.Except(demoted).ToList();
        var structure = new GuideStructureService();
        var target = project.Tracks.FirstOrDefault(t => t.Id == _newMain);
        foreach (var point in promoted)
        {
            token.ThrowIfCancellationRequested();
            var node = point.ToGuideNode(); node.TrackId = _newMain;
            node.SourceTrackId = _newMain; node.SourceTrackName = target!.Name;
            node.StableOrder = nodes.Count;
            structure.ApplyPosition(node, structure.SnapNodeToPosition(target, node.OriginalCoordinate, point.PreferredTrackPosition));
            nodes.Add(node);
        }
        _afterNodes = nodes.ToArray(); _afterPoints = points.ToArray();
        var scratch = new HikingProject();
        foreach (var track in project.Tracks) scratch.AddTrack(track, false);
        scratch.MainGuideTrackId = newMain;
        foreach (var node in _afterNodes) scratch.GuideNodes.Add(node);
        foreach (var segment in _beforeSegments) scratch.GuideSegments.Add(BatchWaypointGuideConversion.CloneSegment(segment));
        foreach (var segment in _beforeRetired) scratch.RetiredGuideSegments.Add(BatchWaypointGuideConversion.CloneSegment(segment));
        GuideStructureService.ReplaceSegments(scratch, structure.RebuildSegments(scratch));
        _afterSegments = scratch.GuideSegments.ToArray(); _afterRetired = scratch.RetiredGuideSegments.ToArray();
        _beforeData = CopyData(project.GuideData); _afterData = CopyData(_beforeData);
        if (_oldMain != _newMain)
            GuideDataReconciler.Reconcile(_afterData, newMain ?? _oldMain ?? Guid.Empty,
                newMain is null ? [] : GuideDataReconciler.OrderedNodes(scratch).Select(n => n.Id).ToArray());
        var demotedIds = demoted.Select(n => n.Id).ToHashSet(); var promotedIds = promoted.Select(w => w.Id).ToHashSet();
        MediaOwnerType Owner(Guid id, MediaOwnerType before) => demotedIds.Contains(id) ? MediaOwnerType.Waypoint : promotedIds.Contains(id) ? MediaOwnerType.GuideNode : before;
        _attachments = project.MediaAttachments.Select(a => (a, a.OwnerType, Owner(a.OwnerId, a.OwnerType))).ToArray();
        _external = project.ExternalMediaReferences.Select(a => (a, a.OwnerType, Owner(a.OwnerId, a.OwnerType))).ToArray();
        var snap = new TrackSnapService();
        _geometry = project.Tracks.Where(t => t.Id == _oldMain || t.Id == _newMain)
            .Select(t => (t, t.GeometryRevision, snap.GetGeometryFingerprint(t))).ToArray();
    }

    public static MainGuideTrackChange Prepare(HikingProject project, Guid? newMain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        if (newMain.HasValue && project.Tracks.All(t => t.Id != newMain)) throw new InvalidOperationException("主攻略轨迹不属于本项目。");
        return new(project, newMain, cancellationToken);
    }
    public void Execute() => SetState(false);
    public void Undo() => SetState(true);
    private void SetState(bool undo) => _project.ExecuteAtomicUpdate(p =>
    {
        var expectedNodes = undo ? _afterNodes : _beforeNodes;
        var expectedPoints = undo ? _afterPoints : _beforePoints;
        var snap = new TrackSnapService();
        if (ManualFingerprint(p.GuideData) != ManualFingerprint(undo ? _afterData : _beforeData))
            throw new InvalidOperationException("人工攻略或行程方案在身份转换后已修改，请先保留这些修改，不能直接撤销或重做身份转换。");
        if (p.MainGuideTrackId != (undo ? _newMain : _oldMain) || !p.GuideNodes.SequenceEqual(expectedNodes) ||
            !p.Waypoints.SequenceEqual(expectedPoints) || _geometry.Any(g => !p.Tracks.Contains(g.Track) ||
                g.Track.GeometryRevision != g.Revision || snap.GetGeometryFingerprint(g.Track) != g.Fingerprint))
            throw new InvalidOperationException("轨迹或标注在计算后发生变化，请重新转换轨迹身份。");
        p.MainGuideTrackId = undo ? _oldMain : _newMain;
        p.GuideNodes.Clear(); foreach (var n in undo ? _beforeNodes : _afterNodes) p.GuideNodes.Add(n);
        p.Waypoints.Clear(); foreach (var w in undo ? _beforePoints : _afterPoints) p.Waypoints.Add(w);
        p.GuideSegments.Clear(); foreach (var s in undo ? _beforeSegments : _afterSegments) p.GuideSegments.Add(s);
        p.RetiredGuideSegments.Clear(); foreach (var s in undo ? _beforeRetired : _afterRetired) p.RetiredGuideSegments.Add(s);
        foreach (var a in _attachments) a.Item.OwnerType = undo ? a.Before : a.After;
        foreach (var a in _external) a.Item.OwnerType = undo ? a.Before : a.After;
        var restoredData = CopyData(undo ? _beforeData : _afterData);
        // 不覆盖后来调整的计算参数，也不把旧派生统计冒充当前结果。
        p.GuideData.Manual = restoredData.Manual; p.GuideData.Plans = restoredData.Plans;
        p.NotifyGuideNodeChanged();
    });
}
