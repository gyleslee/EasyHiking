using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Application.Guides;

namespace HikingGuide.Application.TrackEditing;

public sealed record TrackMergeSource(Guid TrackId, bool Reversed, Guid? SectionId = null);
public sealed record StraightConnector(Guid FromTrackId, Guid ToTrackId, double LengthMeters);
public sealed record TrackMergeRemoval(Track Track, bool WholeTrack,
    IReadOnlyList<SplitSection> Sections, TrackEditPlan? PartialDeletion);

/// <summary>混合几何按指定顺序组成新轨迹；可安全移除所选来源，保留未选几何和人工内容。</summary>
public sealed class TrackMergeService
{
    private readonly IGeoDistanceService _distance = GeoDistanceCalculator.Instance;

    public TrackMergePlan Prepare(HikingProject project, IReadOnlyList<TrackMergeSource> sources,
        string newName, bool connectByStraightLines, CancellationToken cancellationToken = default,
        bool deleteOriginalTracks = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (sources.Count < 2 || sources.Select(item => (item.TrackId, item.SectionId)).Distinct().Count() != sources.Count)
            throw new InvalidOperationException("请选择至少两个不同的轨迹或拆分分段，并确定顺序。");
        if (sources.GroupBy(item => item.TrackId).Any(group => group.Count() > 1 && group.Any(item => item.SectionId is null)))
            throw new InvalidOperationException("同一条轨迹不能同时选择整条轨迹及其拆分分段，请取消重复范围。");
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("请输入新轨迹名称。");
        var selected = sources.Select(source =>
            (Source: source, Track: project.Tracks.FirstOrDefault(track => track.Id == source.TrackId)
                ?? throw new InvalidOperationException("所选轨迹不属于当前项目。"))).ToArray();
        var geometries = selected.Select(item => (item.Source, item.Track,
            Parts: item.Source.SectionId is Guid id
                ? item.Track.GetSplitSections().FirstOrDefault(section => section.Id == id)?.GeometryParts
                    ?? throw new InvalidOperationException("所选拆分分段已不存在，请重新选择。")
                : (IReadOnlyList<TrackSegment>)item.Track.Segments)).ToArray();
        if (selected.Any(item => item.Track.Id == project.MainGuideTrackId))
            throw new InvalidOperationException("主攻略轨迹及其拆分分段不可合并或连接。请先转为普通轨迹（攻略节点同步转为普通标注），编辑完成后再设为主攻略轨迹。");
        if (geometries.Any(item => item.Parts.Count == 0 ||
            item.Parts.Any(segment => segment.Points.Count < 2)))
            throw new InvalidOperationException("所选轨迹包含无效轨迹段，无法创建新轨迹。");
        var fingerprint = new TrackSnapService();
        var expected = selected.DistinctBy(item => item.Track.Id).Select(item => (item.Track, item.Track.GeometryRevision,
            Fingerprint: fingerprint.GetGeometryFingerprint(item.Track))).ToArray();
        var result = new Track
        {
            Name = newName.Trim(), SourceFormat = TrackSourceFormat.Unknown,
            Metadata = new Dictionary<string, string?> { ["hg:editorOperation"] =
                connectByStraightLines ? "直线连接轨迹" : "合并轨迹" }
        };
        var connectors = new List<StraightConnector>();
        GeoCoordinate? previousEnd = null;
        Guid? previousTrack = null;
        var mergedSectionId = Guid.NewGuid();
        var mergedSectionName = connectByStraightLines ? "连接分段 1" : "合并分段 1";
        foreach (var (source, track, parts) in geometries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = source.Reversed ? parts.AsEnumerable().Reverse() : parts;
            var first = source.Reversed ? parts[^1].EndPoint!.Coordinate : parts[0].StartPoint!.Coordinate;
            if (connectByStraightLines && previousEnd is { } from)
            {
                var length = _distance.CalculateDistance(from, first);
                connectors.Add(new StraightConnector(previousTrack!.Value, track.Id, length));
                result.Segments.Add(new TrackSegment
                {
                    Name = "人工直线连接",
                    SplitSectionId = mergedSectionId,
                    SplitSectionName = mergedSectionName,
                    OriginalAttributes = new Dictionary<string, string?>
                    { ["hg:editorProvenance"] = "人工直线连接" },
                    Points = [NewConnectorPoint(from), NewConnectorPoint(first)]
                });
            }
            foreach (var segment in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceSectionId = segment.SplitSectionId ?? segment.Id;
                var copy = new TrackSegment
                {
                    Name = segment.Name,
                    SplitSectionId = mergedSectionId,
                    SplitSectionName = mergedSectionName,
                    SplitSectionDescription = segment.SplitSectionDescription,
                    RouteProvenance = segment.RouteProvenance,
                    OriginalAttributes = new Dictionary<string, string?>(segment.OriginalAttributes)
                    { ["hg:sourceTrackId"] = track.Id.ToString("D"),
                      ["hg:sourceSplitSectionId"] = sourceSectionId.ToString("D") }
                };
                var points = source.Reversed ? segment.Points.AsEnumerable().Reverse() : segment.Points;
                foreach (var point in points)
                    copy.Points.Add(new TrackPoint
                    {
                        Latitude = point.Latitude, Longitude = point.Longitude,
                        SourceElevation = point.SourceElevation, DemElevation = point.DemElevation,
                        Timestamp = point.Timestamp, IsCoordinateEdited = point.IsCoordinateEdited,
                        IsInsertedByEditor = point.IsInsertedByEditor,
                        OriginalAttributes = new Dictionary<string, string?>(point.OriginalAttributes)
                    });
                result.Segments.Add(copy);
            }
            previousEnd = source.Reversed ? parts[0].StartPoint!.Coordinate : parts[^1].EndPoint!.Coordinate;
            previousTrack = track.Id;
        }
        result.RecalculateDistances();
        var removals = new List<TrackMergeRemoval>();
        if (deleteOriginalTracks)
            foreach (var group in geometries.GroupBy(item => item.Track.Id))
            {
                var track = group.First().Track;
                var sectionIds = group.Where(item => item.Source.SectionId.HasValue)
                    .Select(item => item.Source.SectionId!.Value).ToHashSet();
                var sections = track.GetSplitSections();
                var whole = group.Any(item => item.Source.SectionId is null) || sectionIds.Count == sections.Count;
                removals.Add(new(track, whole, whole ? sections : sections.Where(section => sectionIds.Contains(section.Id)).ToArray(),
                    whole ? null : new TrackEditService().PrepareDeleteSplitSections(project, track.Id, sectionIds, cancellationToken)));
            }
        return new TrackMergePlan(project, expected, result, connectors,
            connectByStraightLines, project.IsAnalysisResultStale, removals);
    }

    private static TrackPoint NewConnectorPoint(GeoCoordinate coordinate) => new()
    {
        Latitude = coordinate.Latitude, Longitude = coordinate.Longitude,
        IsCoordinateEdited = true, IsInsertedByEditor = true
    };
}

public sealed class TrackMergePlan(
    HikingProject project,
    IReadOnlyList<(Track Track, long Revision, string Fingerprint)> sources,
    Track newTrack, IReadOnlyList<StraightConnector> connectors,
    bool isStraightConnection, bool analysisWasStale, IReadOnlyList<TrackMergeRemoval>? removals = null)
{
    private readonly List<Action> _undoRemovals = [];
    private readonly string _newFingerprint = new TrackSnapService().GetGeometryFingerprint(newTrack);
    private readonly TrackMergeMarkerTransfer _markerTransfer = new(project, removals ?? []);
    public Track NewTrack => newTrack;
    public IReadOnlyList<StraightConnector> Connectors => connectors;
    public bool IsStraightConnection => isStraightConnection;
    public bool DeletesOriginalTracks => removals?.Count > 0;
    public IReadOnlyList<TrackMergeRemoval> Removals => removals ?? [];
    public IReadOnlyList<Track> SourceTracks => sources.Select(item => item.Track).ToArray();
    public IReadOnlyList<string> Warnings => isStraightConnection
        ? ["这些连接是地图上的直线几何，不代表存在道路或可通行路线；未使用 BRouter。"]
        : [];

    public void Apply() => project.ExecuteAtomicUpdate(p =>
    {
        var fingerprint = new TrackSnapService();
        if (p.Tracks.Any(track => track.Id == newTrack.Id) ||
            sources.Any(item => item.Track.Id == p.MainGuideTrackId) ||
            sources.Any(item => !p.Tracks.Contains(item.Track) ||
                item.Track.GeometryRevision != item.Revision ||
                fingerprint.GetGeometryFingerprint(item.Track) != item.Fingerprint))
            throw new InvalidOperationException("源轨迹在后台计算期间发生变化，请重新创建合并轨迹。");
        p.AddTrack(newTrack, false);
        _undoRemovals.Clear();
        try
        {
            foreach (var removal in Removals)
            {
                if (removal.WholeTrack)
                {
                    // 按执行时状态构造删除事务，保证多个父轨迹撤销时恢复有效的主攻略引用。
                    var deletion = new TrackManagementService().PrepareDeletion(p, removal.Track.Id);
                    deletion.Execute();
                    _undoRemovals.Add(deletion.Undo);
                }
                else
                {
                    removal.PartialDeletion!.Apply();
                    _undoRemovals.Add(removal.PartialDeletion.Undo);
                }
            }
            _markerTransfer.Apply(newTrack);
        }
        catch
        {
            _markerTransfer.Undo();
            foreach (var undo in _undoRemovals.AsEnumerable().Reverse()) undo();
            p.Tracks.Remove(newTrack);
            p.IsAnalysisResultStale = analysisWasStale;
            throw;
        }
        p.IsAnalysisResultStale = true;
    });

    public void Undo() => project.ExecuteAtomicUpdate(p =>
    {
        if (!p.Tracks.Contains(newTrack) || newTrack.GeometryRevision != 0 ||
            new TrackSnapService().GetGeometryFingerprint(newTrack) != _newFingerprint ||
            Removals.Any(removal => removal.WholeTrack
                ? p.Tracks.Any(track => track.Id == removal.Track.Id)
                : !p.Tracks.Contains(removal.Track) ||
                  removal.Track.GeometryRevision != sources.First(item => item.Track.Id == removal.Track.Id).Revision + 1 ||
                  new TrackSnapService().GetGeometryFingerprint(removal.Track) != removal.PartialDeletion!.ChangeSet.NewFingerprint))
            throw new InvalidOperationException("合并后的轨迹或保留分段已发生变化，不能直接撤销此操作。");
        _markerTransfer.Undo();
        foreach (var undo in _undoRemovals.AsEnumerable().Reverse()) undo();
        p.Tracks.Remove(newTrack);
        p.IsAnalysisResultStale = analysisWasStale;
    });
}
