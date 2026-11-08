using HikingGuide.Core.Models;

namespace HikingGuide.Application.Guides;

/// <summary>在后台预计算多节点转普通标注后的分段，保留节点、媒体与人工内容。</summary>
public sealed class BatchGuideWaypointConversion
{
    public BatchGuideWaypointConversionPlan Prepare(HikingProject project,
        IReadOnlyCollection<GuideNode> selected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var unique = selected.DistinctBy(node => node.Id).ToArray();
        if (unique.Length == 0) throw new InvalidOperationException("请先选择攻略节点。");
        if (unique.Any(node => !project.GuideNodes.Contains(node)))
            throw new InvalidOperationException("所选攻略节点不属于当前项目。");
        var conversions = new List<(GuideNode Node, Waypoint Waypoint)>(unique.Length);
        foreach (var node in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            conversions.Add((node, node.ToWaypoint(project)));
        }
        var scratch = new HikingProject();
        foreach (var track in project.Tracks) scratch.AddTrack(track, false);
        scratch.MainGuideTrackId = project.MainGuideTrackId;
        foreach (var node in project.GuideNodes.Where(node => !unique.Contains(node))) scratch.GuideNodes.Add(node);
        foreach (var segment in project.GuideSegments)
            scratch.GuideSegments.Add(BatchWaypointGuideConversion.CloneSegment(segment));
        foreach (var segment in project.RetiredGuideSegments)
            scratch.RetiredGuideSegments.Add(BatchWaypointGuideConversion.CloneSegment(segment));
        var rebuilt = new GuideStructureService().RebuildSegments(scratch).ToArray();
        return new BatchGuideWaypointConversionPlan(project, conversions, rebuilt,
            project.GuideSegments.ToArray(), project.RetiredGuideSegments.ToArray());
    }
}

public sealed class BatchGuideWaypointConversionPlan(
    HikingProject project,
    IReadOnlyList<(GuideNode Node, Waypoint Waypoint)> conversions,
    IReadOnlyList<GuideSegment> newSegments,
    IReadOnlyList<GuideSegment> oldSegments,
    IReadOnlyList<GuideSegment> oldRetiredSegments)
{
    private readonly (MediaAttachment Item, MediaOwnerType Previous)[] _attachments = project.MediaAttachments
        .Where(item => conversions.Any(pair => pair.Node.Id == item.OwnerId))
        .Select(item => (item, item.OwnerType)).ToArray();
    private readonly (ExternalMediaReference Item, MediaOwnerType Previous)[] _external = project.ExternalMediaReferences
        .Where(item => conversions.Any(pair => pair.Node.Id == item.OwnerId))
        .Select(item => (item, item.OwnerType)).ToArray();

    public int Count => conversions.Count;
    public IReadOnlyList<Waypoint> NewWaypoints => conversions.Select(pair => pair.Waypoint).ToArray();

    public void Apply() => project.ExecuteAtomicUpdate(p =>
    {
        if (conversions.Any(pair => !p.GuideNodes.Contains(pair.Node) ||
            p.Waypoints.Any(point => point.Id == pair.Waypoint.Id)))
            throw new InvalidOperationException("项目在后台计算期间发生变化，请重新选择节点后再试。");
        foreach (var (node, point) in conversions)
        {
            p.GuideNodes.Remove(node);
            p.Waypoints.Add(point);
        }
        foreach (var (item, _) in _attachments) item.OwnerType = MediaOwnerType.Waypoint;
        foreach (var (item, _) in _external) item.OwnerType = MediaOwnerType.Waypoint;
        GuideStructureService.ReplaceSegments(p, newSegments);
        p.NotifyGuideNodeChanged();
    });

    public void Undo() => project.ExecuteAtomicUpdate(p =>
    {
        foreach (var (node, point) in conversions)
        {
            p.Waypoints.Remove(point);
            p.GuideNodes.Add(node);
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
