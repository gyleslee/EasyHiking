using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Application.Guides;

namespace HikingGuide.Application.TrackEditing;

public static class TrackRelatedMarkers
{
    public static IReadOnlySet<Guid> Find(HikingProject project, Track track, TrackGeometryChangeSet? change = null)
    {
        var ids = new HashSet<Guid>(); var snap = new TrackSnapService();
        var changedPointIds = change?.ChangedEdgeMappings.Keys.SelectMany(k => new[] { k.StartPointId, k.EndPointId }).ToHashSet() ?? [];
        var changedParts = track.Segments.Where(p => p.Points.Any(pt => changedPointIds.Contains(pt.Id))).Select(p => p.Id).ToHashSet();
        bool Removed(TrackPosition? position)
        {
            if (change is null) return true;
            if (position is null) return false;
            if (change.RemovedSegmentIds.Contains(position.TrackSegmentId)) return true;
            if (change.RemovedOldRanges.Any(r => position.DistanceFromTrackStart > r.StartMeters + 1e-6 &&
                    position.DistanceFromTrackStart < r.EndMeters - 1e-6)) return true;
            if (changedParts.Contains(position.TrackSegmentId) && change.RemovedOldRanges.Any(r =>
                    change.Operation == "删除此点前轨迹" && Math.Abs(position.DistanceFromTrackStart - r.StartMeters) <= 1e-6 ||
                    change.Operation == "删除此点后轨迹" && Math.Abs(position.DistanceFromTrackStart - r.EndMeters) <= 1e-6)) return true;
            if (change.RemovedOldRanges.Any(r => Math.Abs(position.DistanceFromTrackStart-r.StartMeters)<=1e-6 ||
                    Math.Abs(position.DistanceFromTrackStart-r.EndMeters)<=1e-6)) return false;
            var part = track.Segments.FirstOrDefault(s => s.Id == position.TrackSegmentId);
            return part is not null && position.StartPointIndex >= 0 && position.EndPointIndex < part.Points.Count &&
                change.ChangedEdgeMappings.ContainsKey(new(part.Points[position.StartPointIndex].Id, part.Points[position.EndPointIndex].Id));
        }
        var fingerprint = change?.OldFingerprint;
        foreach (var point in project.Waypoints.Where(w => w.AssociatedTrackId == track.Id))
        {
            var pos = point.PreferredTrackPosition;
            if (change is not null && (pos is null || pos.TrackId != track.Id || pos.TrackGeometryFingerprint != fingerprint))
                pos = snap.Snap(track, point.Coordinate).Position;
            if (Removed(pos)) ids.Add(point.Id);
        }
        foreach (var point in project.GuideNodes.Where(g => g.TrackId == track.Id))
        {
            var pos = point.ExactPosition;
            if (change is not null && !point.IsOrphaned && (pos is null || pos.TrackGeometryFingerprint != fingerprint))
                pos = snap.Snap(track, point.SnappedCoordinate ?? point.OriginalCoordinate).Position;
            if (Removed(pos)) ids.Add(point.Id);
        }
        return ids;
    }
}

/// <summary>仅由用户明确选择后执行；媒体和人工分段随撤销一起还原。</summary>
public sealed class RelatedMarkerDeletion(HikingProject project, IReadOnlySet<Guid> ids)
{
    private Action? _restore;
    public void Apply()
    {
        var waypoints = project.Waypoints.ToArray(); var nodes = project.GuideNodes.ToArray();
        var attachments = project.MediaAttachments.ToArray(); var external = project.ExternalMediaReferences.ToArray();
        var blobs = project.MediaBlobs.ToArray(); var active = project.GuideSegments.ToArray(); var retired = project.RetiredGuideSegments.ToArray();
        _restore = () =>
        {
            Restore(project.Waypoints, waypoints); Restore(project.GuideNodes, nodes);
            Restore(project.MediaAttachments, attachments); Restore(project.ExternalMediaReferences, external); Restore(project.MediaBlobs, blobs);
            Restore(project.GuideSegments, active); Restore(project.RetiredGuideSegments, retired); project.MarkDirty();
        };
        try
        {
            foreach (var w in waypoints.Where(w => ids.Contains(w.Id))) project.Waypoints.Remove(w);
            foreach (var n in nodes.Where(n => ids.Contains(n.Id))) project.GuideNodes.Remove(n);
            foreach (var a in attachments.Where(a => ids.Contains(a.OwnerId))) project.MediaAttachments.Remove(a);
            foreach (var e in external.Where(e => ids.Contains(e.OwnerId))) project.ExternalMediaReferences.Remove(e);
            foreach (var b in blobs.Where(b => attachments.Any(a => ids.Contains(a.OwnerId) && a.MediaBlobId == b.Id) &&
                         !project.MediaAttachments.Any(a => a.MediaBlobId == b.Id))) project.MediaBlobs.Remove(b);
            if (nodes.Any(n => ids.Contains(n.Id))) GuideStructureService.ReplaceSegments(project, new GuideStructureService().RebuildSegments(project));
            project.MarkDirty();
        }
        catch { _restore(); throw; }
    }
    public void Undo() => (_restore ?? throw new InvalidOperationException("标注删除尚未执行。"))();
    private static void Restore<T>(ICollection<T> list, IEnumerable<T> values) { list.Clear(); foreach (var item in values) list.Add(item); }
}
