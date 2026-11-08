using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.Guides;

public sealed record MarkerTrackSnapResult(Track Track, TrackPosition Position);

/// <summary>在项目完整轨迹几何上选最近吸附位置；不修改项目和地图显示副本。</summary>
public sealed class MarkerTrackSnapService
{
    public MarkerTrackSnapResult FindNearest(HikingProject project, GeoCoordinate coordinate,
        bool mainGuideTrackOnly, CancellationToken cancellationToken = default, Guid? preferredTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        coordinate.Validate();
        var tracks = mainGuideTrackOnly
            ? project.MainGuideTrack is { } main ? [main] : Array.Empty<Track>()
            : project.Tracks.ToArray();
        tracks = tracks.Where(t => MapLayerVisibility.IsTrackVisible(project.Settings.Map, t.Id)).ToArray();
        if (tracks.Length == 0)
            throw new InvalidOperationException(mainGuideTrackOnly
                ? "请先设置并显示项目的主攻略轨迹，不能吸附到隐藏轨迹。" : "当前项目没有可见的可吸附轨迹，请先显示轨迹。");

        // 优先所属轨迹；所属轨迹隐藏或没有有效几何时，再在可见轨迹中选最近位置。
        if (!mainGuideTrackOnly && preferredTrackId is Guid preferred && tracks.FirstOrDefault(t => t.Id == preferred) is { } owner)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ownerPosition = new TrackSnapService().Snap(owner, coordinate).Position;
            if (ownerPosition is not null) return new(owner, ownerPosition);
        }

        var snap = new TrackSnapService();
        MarkerTrackSnapResult? nearest = null;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = snap.Snap(track, coordinate).Position;
            if (position is null) continue;
            if (nearest is null || position.DistanceToTrack < nearest.Position.DistanceToTrack - 1e-8)
                nearest = new MarkerTrackSnapResult(track, position);
        }
        return nearest ?? throw new InvalidOperationException("项目内轨迹没有可吸附的线段。");
    }

    public IReadOnlyList<GuideSegment> PreviewGuideSegments(HikingProject project,
        GuideNode node, TrackPosition position)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(position);
        if (!project.GuideNodes.Contains(node) || project.MainGuideTrackId != position.TrackId)
            throw new InvalidOperationException("攻略节点必须吸附到本项目的主攻略轨迹。");
        var scratch = new HikingProject();
        foreach (var track in project.Tracks) scratch.AddTrack(track, false);
        scratch.MainGuideTrackId = project.MainGuideTrackId;
        foreach (var original in project.GuideNodes)
            scratch.GuideNodes.Add(ReferenceEquals(original, node) ? new GuideNode
            {
                Id = node.Id, TrackId = position.TrackId, StableOrder = node.StableOrder,
                ExactPosition = position, DistanceFromStart = position.DistanceFromTrackStart,
                IsOrphaned = false
            } : original);
        foreach (var segment in project.GuideSegments)
            scratch.GuideSegments.Add(BatchWaypointGuideConversion.CloneSegment(segment));
        foreach (var segment in project.RetiredGuideSegments)
            scratch.RetiredGuideSegments.Add(BatchWaypointGuideConversion.CloneSegment(segment));
        return new GuideStructureService().RebuildSegments(scratch).ToArray();
    }
}
