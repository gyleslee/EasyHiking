using HikingGuide.Core.Models;

namespace HikingGuide.Application.Guides;

public sealed record TrackDeletionImpact(bool IsMainGuideTrack, int DirectWaypoints,
    int SourceGuideNodes, int GuideNodesAttachedToMain, int GuideSegments,
    int ManualDataItems, int MediaItems, bool WasSelected);

/// <summary>轨迹删除的预检查及可撤销事务；源 GPX/KML/KMZ 永不触碰。</summary>
public sealed class TrackManagementService
{
    public TrackDeletionImpact AnalyzeDeletion(HikingProject project, Guid trackId)
    {
        if (project.Tracks.All(track => track.Id != trackId))
            throw new ArgumentException("轨迹不属于本项目。", nameof(trackId));
        var isMain = project.MainGuideTrackId == trackId;
        var attached = project.GuideNodes.Where(node => node.TrackId == trackId).ToArray();
        return new TrackDeletionImpact(isMain,
            project.Waypoints.Count(point => point.AssociatedTrackId == trackId),
            project.GuideNodes.Count(node => node.SourceTrackId == trackId),
            attached.Length, project.GuideSegments.Count(segment => segment.TrackId == trackId),
            attached.Count(node => node.ManualData is not null) +
            project.Waypoints.Count(point => point.AssociatedTrackId == trackId && point.ManualData is not null),
            project.MediaAttachments.Count(item => attached.Any(node => node.Id == item.OwnerId) ||
                project.Waypoints.Any(point => point.AssociatedTrackId == trackId && point.Id == item.OwnerId)) +
            project.ExternalMediaReferences.Count(item => attached.Any(node => node.Id == item.OwnerId) ||
                project.Waypoints.Any(point => point.AssociatedTrackId == trackId && point.Id == item.OwnerId)),
            project.ActiveTrackId == trackId);
    }

    public TrackDeletionTransaction PrepareDeletion(HikingProject project, Guid trackId)
    {
        var track = project.Tracks.FirstOrDefault(item => item.Id == trackId)
            ?? throw new ArgumentException("轨迹不属于本项目。", nameof(trackId));
        return new TrackDeletionTransaction(project, track, project.Tracks.IndexOf(track), AnalyzeDeletion(project, trackId));
    }
}

public sealed class TrackDeletionTransaction
{
    private readonly HikingProject _project;
    private readonly Track _track;
    private readonly int _index;
    private readonly Guid? _oldSelected;
    private readonly Guid? _oldMain;
    private readonly (Waypoint Point, Guid? Owner)[] _waypoints;
    private readonly (GuideNode Node, Guid? Source, Guid? TrackId, bool Orphaned,
        string? SourceName,
        HikingGuide.Core.Geometry.GeoCoordinate? Snapped, HikingGuide.Core.Models.TrackPosition? Position,
        double? Distance, double? Offset, int? SegmentIndex, double? Fraction, string? Fingerprint)[] _nodes;
    private readonly GuideSegment[] _segments;
    private readonly GuideSegment[] _retired;

    internal TrackDeletionTransaction(HikingProject project, Track track, int index, TrackDeletionImpact impact)
    {
        _project = project; _track = track; _index = index; Impact = impact;
        _oldSelected = project.ActiveTrackId; _oldMain = project.MainGuideTrackId;
        _waypoints = project.Waypoints.Where(point => point.AssociatedTrackId == track.Id)
            .Select(point => (point, point.AssociatedTrackId)).ToArray();
        _nodes = project.GuideNodes.Where(node => node.TrackId == track.Id || node.SourceTrackId == track.Id)
            .Select(node => (node, node.SourceTrackId, node.TrackId, node.IsOrphaned, node.SourceTrackName,
                node.SnappedCoordinate, node.ExactPosition, node.DistanceFromStart, node.DistanceToTrack,
                node.TrackSegmentIndex, node.TrackPosition, node.TrackGeometryFingerprint)).ToArray();
        _segments = project.GuideSegments.ToArray();
        _retired = project.RetiredGuideSegments.ToArray();
    }

    public TrackDeletionImpact Impact { get; }

    public void Execute() => _project.ExecuteAtomicUpdate(project =>
    {
        if (!project.Tracks.Contains(_track)) throw new InvalidOperationException("轨迹已删除。");
        foreach (var (point, _) in _waypoints) point.AssociatedTrackId = null;
        foreach (var entry in _nodes)
        {
            if (entry.Node.SourceTrackId == _track.Id)
            {
                entry.Node.SourceTrackName ??= _track.Name ?? "（未命名轨迹）";
                entry.Node.SourceTrackId = null;
            }
            if (entry.Node.TrackId != _track.Id) continue;
            entry.Node.TrackId = null;
            entry.Node.SnappedCoordinate = null;
            entry.Node.ExactPosition = null;
            entry.Node.DistanceFromStart = null;
            entry.Node.DistanceToTrack = null;
            entry.Node.TrackSegmentIndex = null;
            entry.Node.TrackPosition = null;
            entry.Node.TrackGeometryFingerprint = null;
            entry.Node.IsOrphaned = true;
        }
        if (Impact.IsMainGuideTrack)
        {
            project.MainGuideTrackId = null;
            GuideStructureService.ReplaceSegments(project, []);
        }
        project.Tracks.Remove(_track);
        project.MarkDirty();
    });

    public void Undo() => _project.ExecuteAtomicUpdate(project =>
    {
        if (project.Tracks.Any(track => track.Id == _track.Id)) throw new InvalidOperationException("轨迹已经存在。");
        project.Tracks.Insert(Math.Min(_index, project.Tracks.Count), _track);
        project.MainGuideTrackId = _oldMain;
        project.ActiveTrackId = _oldSelected;
        foreach (var (point, owner) in _waypoints) point.AssociatedTrackId = owner;
        foreach (var entry in _nodes)
        {
            entry.Node.SourceTrackId = entry.Source;
            entry.Node.SourceTrackName = entry.SourceName;
            entry.Node.TrackId = entry.TrackId;
            entry.Node.IsOrphaned = entry.Orphaned;
            entry.Node.SnappedCoordinate = entry.Snapped;
            entry.Node.ExactPosition = entry.Position;
            entry.Node.DistanceFromStart = entry.Distance;
            entry.Node.DistanceToTrack = entry.Offset;
            entry.Node.TrackSegmentIndex = entry.SegmentIndex;
            entry.Node.TrackPosition = entry.Fraction;
            entry.Node.TrackGeometryFingerprint = entry.Fingerprint;
        }
        project.GuideSegments.Clear();
        foreach (var segment in _segments) project.GuideSegments.Add(segment);
        project.RetiredGuideSegments.Clear();
        foreach (var segment in _retired) project.RetiredGuideSegments.Add(segment);
        project.MarkDirty();
    });
}
