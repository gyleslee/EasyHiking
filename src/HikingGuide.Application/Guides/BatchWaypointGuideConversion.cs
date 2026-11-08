using HikingGuide.Core.Models;

namespace HikingGuide.Application.Guides;

/// <summary>在后台预计算吸附和分段，在界面线程一次提交；不接触原始轨迹文件。</summary>
public sealed class BatchWaypointGuideConversion
{
    private readonly GuideStructureService _guideService = new();

    public BatchWaypointGuideConversionPlan Prepare(HikingProject project,
        IReadOnlyCollection<Waypoint> selected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var main = project.MainGuideTrack ?? throw new InvalidOperationException("请先设置主攻略轨迹。");
        var unique = selected.DistinctBy(point => point.Id).ToArray();
        if (unique.Length == 0) throw new InvalidOperationException("请先选择普通标注。");
        if (unique.Any(point => !project.Waypoints.Contains(point)))
            throw new InvalidOperationException("所选标注不属于当前项目。");

        var conversions = new List<(Waypoint Waypoint, GuideNode Node)>(unique.Length);
        foreach (var (point, index) in unique.Select((point, index) => (point, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = point.ToGuideNode();
            node.SourceTrackName = point.AssociatedTrackId is Guid sourceId
                ? project.Tracks.FirstOrDefault(track => track.Id == sourceId)?.Name ?? "（未命名轨迹）" : null;
            node.TrackId = main.Id;
            node.StableOrder = project.GuideNodes.Count + index;
            var position = _guideService.SnapNodeToPosition(main, node.OriginalCoordinate, point.PreferredTrackPosition);
            _guideService.ApplyPosition(node, position);
            conversions.Add((point, node));
        }

        // 分段重建会修改已有分段对象；使用脱离实时项目的副本，避免后台线程写入项目。
        var scratch = new HikingProject();
        foreach (var track in project.Tracks) scratch.AddTrack(track, false);
        scratch.MainGuideTrackId = main.Id;
        foreach (var node in project.GuideNodes) scratch.GuideNodes.Add(node);
        foreach (var (_, node) in conversions) scratch.GuideNodes.Add(node);
        foreach (var segment in project.GuideSegments) scratch.GuideSegments.Add(CloneSegment(segment));
        foreach (var segment in project.RetiredGuideSegments) scratch.RetiredGuideSegments.Add(CloneSegment(segment));
        var rebuilt = _guideService.RebuildSegments(scratch).ToArray();
        return new BatchWaypointGuideConversionPlan(project, main.Id, conversions, rebuilt,
            project.GuideSegments.ToArray(), project.RetiredGuideSegments.ToArray());
    }

    internal static GuideSegment CloneSegment(GuideSegment segment) => new()
    {
        Id = segment.Id, StartNodeId = segment.StartNodeId, EndNodeId = segment.EndNodeId,
        StartDistance = segment.StartDistance, EndDistance = segment.EndDistance,
        TrackId = segment.TrackId, CustomName = segment.CustomName,
        ManualData = segment.ManualData?.DeepCopy()
    };
}

public sealed class BatchWaypointGuideConversionPlan(
    HikingProject project, Guid mainTrackId,
    IReadOnlyList<(Waypoint Waypoint, GuideNode Node)> conversions,
    IReadOnlyList<GuideSegment> newSegments,
    IReadOnlyList<GuideSegment> oldSegments,
    IReadOnlyList<GuideSegment> oldRetiredSegments)
{
    private readonly (MediaAttachment Item, MediaOwnerType Previous)[] _attachments = project.MediaAttachments
        .Where(item => conversions.Any(pair => pair.Waypoint.Id == item.OwnerId))
        .Select(item => (item, item.OwnerType)).ToArray();
    private readonly (ExternalMediaReference Item, MediaOwnerType Previous)[] _external = project.ExternalMediaReferences
        .Where(item => conversions.Any(pair => pair.Waypoint.Id == item.OwnerId))
        .Select(item => (item, item.OwnerType)).ToArray();

    public int Count => conversions.Count;
    public int FarCount(double warningDistanceMeters) =>
        conversions.Count(pair => pair.Node.DistanceToTrack > warningDistanceMeters);
    public IReadOnlyList<GuideNode> NewNodes => conversions.Select(pair => pair.Node).ToArray();

    public void Apply() => project.ExecuteAtomicUpdate(p =>
    {
        if (p.MainGuideTrackId != mainTrackId ||
            conversions.Any(pair => !p.Waypoints.Contains(pair.Waypoint) ||
                p.GuideNodes.Any(node => node.Id == pair.Node.Id)))
            throw new InvalidOperationException("项目在后台计算期间发生变化，请重新选择标注后再试。");
        foreach (var (point, node) in conversions)
        {
            p.Waypoints.Remove(point);
            p.GuideNodes.Add(node);
        }
        foreach (var (item, _) in _attachments) item.OwnerType = MediaOwnerType.GuideNode;
        foreach (var (item, _) in _external) item.OwnerType = MediaOwnerType.GuideNode;
        GuideStructureService.ReplaceSegments(p, newSegments);
        p.NotifyGuideNodeChanged();
    });

    public void Undo() => project.ExecuteAtomicUpdate(p =>
    {
        foreach (var (point, node) in conversions)
        {
            p.GuideNodes.Remove(node);
            p.Waypoints.Add(point);
        }
        foreach (var (item, previous) in _attachments) item.OwnerType = previous;
        foreach (var (item, previous) in _external) item.OwnerType = previous;
        p.GuideSegments.Clear();
        foreach (var segment in oldSegments) p.GuideSegments.Add(segment);
        p.RetiredGuideSegments.Clear();
        foreach (var segment in oldRetiredSegments) p.RetiredGuideSegments.Add(segment);
        p.NotifyGuideNodeChanged();
    });
}
