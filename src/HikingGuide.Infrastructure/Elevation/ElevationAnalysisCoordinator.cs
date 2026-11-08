using HikingGuide.Core.Elevation;
using HikingGuide.Core.Models;
using HikingGuide.Application.Guides;

namespace HikingGuide.Infrastructure.Elevation;

public sealed class ElevationAnalysisCoordinator
{
    private readonly DemDatabaseCatalog _catalog;
    private readonly IElevationProvider _provider;
    private readonly StandardElevationAnalysisV1 _engine = new();

    public async Task<ElevationAnalysisResult> EnsureTerrainClassificationAsync(HikingProject project,
        ElevationTargetDescriptor target, ElevationAnalysisResult result,
        LocusTerrainDistanceProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        if (result.ProjectId != project.Id || result.TargetId != target.TargetId || result.IsStale)
            throw new InvalidOperationException("里程分类对象已失效，请重新分析。");
        var geometry = await Task.Run(() => CurrentFingerprint(project, target), cancellationToken);
        var fingerprint = result.Source.SourceType == ElevationSourceType.Dem ? _catalog.Get(result.Source.DemDatabaseId!.Value).Fingerprint :
            await Task.Run(() => CurrentGpsFingerprint(project, target), cancellationToken);
        if (geometry != result.GeometryFingerprint || fingerprint != result.Source.SourceFingerprint)
            return result with { TerrainClassification = result.TerrainClassification is { } old ? old with { IsStale = true } : null, IsStale = true };
        if (result.TerrainClassification is { } hit && hit.IsCurrent(geometry!, fingerprint!) && hit.Profile == profile) return result;
        IReadOnlyList<TerrainElevationSample>? raw = null;
        lock (project)
            raw = project.ElevationAnalyses.Where(r => r.TargetType == result.TargetType && r.TargetId == result.TargetId &&
                r.GeometryFingerprint == geometry && r.Source.SourceType == result.Source.SourceType && r.Source.DemDatabaseId == result.Source.DemDatabaseId &&
                r.Source.SourceFingerprint == fingerprint && r.TerrainClassification is { } t && t.IsCurrent(geometry!, fingerprint!) &&
                t.Profile.SampleSpacingMeters == profile.SampleSpacingMeters).Select(r => r.TerrainClassification!.RawSeries).FirstOrDefault();
        if (raw is null)
        {
            var snapshot = await Task.Run(() => Snapshot(project, target), cancellationToken);
            raw = await Task.Run(async () =>
            {
                if (result.Source.SourceType == ElevationSourceType.Dem)
                    return await UniformTerrainSampler.SampleDemAsync(snapshot.GeometryParts, result.Source.DemDatabaseId!.Value, _provider, profile.SampleSpacingMeters, cancellationToken);
                return _engine.BuildGrid(snapshot, new ElevationAnalysisProfile(profile.SampleSpacingMeters, 0, profile.SlopeWindowMeters))
                    .Select(p => new TerrainElevationSample(p.PartIndex, p.DistanceMeters, p.Coordinate, p.GpsElevationMeters)).ToArray();
            }, cancellationToken);
        }
        var classification = await Task.Run(() => LocusTerrainDistanceClassifier.Classify(raw, profile, geometry!, fingerprint!, cancellationToken), cancellationToken);
        var currentGeometry = await Task.Run(() => CurrentFingerprint(project, target), cancellationToken);
        var currentSource = result.Source.SourceType == ElevationSourceType.Dem ? _catalog.Get(result.Source.DemDatabaseId!.Value).Fingerprint :
            await Task.Run(() => CurrentGpsFingerprint(project, target), cancellationToken);
        var updated = result with { TerrainClassification = classification with { IsStale = currentGeometry != geometry || currentSource != fingerprint } };
        if (updated.TerrainClassification.IsStale) return updated with { IsStale = true };
        project.ExecuteAtomicUpdate(p =>
        {
            var index = p.ElevationAnalyses.IndexOf(result);
            if (index >= 0) p.ElevationAnalyses[index] = updated;
        });
        return updated;
    }

    public ElevationAnalysisCoordinator(DemDatabaseCatalog catalog, IElevationProvider provider)
    {
        _catalog = catalog;
        _provider = provider;
    }

    public static ElevationTargetDescriptor ForTrack(HikingProject project, Track track) =>
        new(project.Id, ElevationTargetType.Track, track.Id, track.Id,
            track.Name ?? "未命名轨迹", track.Segments);

    public static ElevationTargetDescriptor ForSplitSection(HikingProject project,
        SplitSection section) =>
        new(project.Id, ElevationTargetType.SplitSection, section.Id, section.TrackId,
            section.Name, section.GeometryParts);

    public static ElevationTargetDescriptor ForGuideSegment(HikingProject project,
        GuideSegment segment)
    {
        if (segment.TrackId is not Guid trackId)
            throw new InvalidOperationException("攻略分段尚未关联轨迹，无法分析高程。");
        var track = project.Tracks.FirstOrDefault(item => item.Id == trackId) ??
            throw new InvalidOperationException("攻略分段关联的轨迹不存在。");
        var start = project.GuideNodes.FirstOrDefault(node => node.Id == segment.StartNodeId);
        var end = project.GuideNodes.FirstOrDefault(node => node.Id == segment.EndNodeId);
        if (start?.ExactPosition is not { } from || end?.ExactPosition is not { } to ||
            start.IsOrphaned || end.IsOrphaned)
            throw new InvalidOperationException("攻略分段端点没有有效的精确吸附位置。");
        var pieces = new GuideStructureService().ExtractRange(track, from, to);
        var parts = new List<TrackSegment>();
        for (var index = 0; index < pieces.Count; index++)
        {
            var sourceIndex = from.SegmentIndex + index;
            var source = track.Segments[sourceIndex];
            var originals = source.Points.GroupBy(point => point.Coordinate)
                .ToDictionary(group => group.Key, group => group.First());
            var points = new List<TrackPoint>();
            foreach (var coordinate in pieces[index])
            {
                if (originals.TryGetValue(coordinate, out var original))
                    points.Add(new TrackPoint
                    {
                        Latitude = coordinate.Latitude, Longitude = coordinate.Longitude,
                        SourceElevation = original.SourceElevation,
                        IsCoordinateEdited = original.IsCoordinateEdited
                    });
                else
                {
                    var position = coordinate == from.SnappedCoordinate ? from :
                        coordinate == to.SnappedCoordinate ? to : null;
                    var left = position is null ? null : source.Points.ElementAtOrDefault(position.StartPointIndex);
                    var right = position is null ? null : source.Points.ElementAtOrDefault(position.EndPointIndex);
                    var gps = left is { SourceElevation: double a, IsCoordinateEdited: false } &&
                        right is { SourceElevation: double b, IsCoordinateEdited: false }
                            ? a + (b - a) * position!.Fraction : (double?)null;
                    points.Add(new TrackPoint { Latitude = coordinate.Latitude,
                        Longitude = coordinate.Longitude, SourceElevation = gps });
                }
            }
            parts.Add(new TrackSegment { Id = source.Id, Points = points });
        }
        return new(project.Id, ElevationTargetType.GuideSegment, segment.Id, trackId,
            segment.CustomName ?? $"{start.Name ?? "未命名节点"}→{end.Name ?? "未命名节点"}", parts);
    }

    public async Task<ElevationAnalysisResult> AnalyzeGpsAsync(HikingProject project,
        ElevationTargetDescriptor target, ElevationAnalysisProfile profile,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        var snapshot = await Task.Run(() => Snapshot(project, target), cancellationToken);
        var fingerprint = await Task.Run(() => _engine.GpsFingerprint(snapshot), cancellationToken);
        var source = new ElevationSourceDescriptor(ElevationSourceType.Gps, null,
            "GPS海拔", fingerprint);
        return await AnalyzeAsync(project, target, snapshot, source, profile, progress, cancellationToken, forceRefresh);
    }

    public async Task<ElevationAnalysisResult> AnalyzeDemAsync(HikingProject project,
        ElevationTargetDescriptor target, Guid databaseId,
        ElevationAnalysisProfile profile,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        var database = _catalog.Get(databaseId);
        if (database.Status == DemDatabaseStatus.Disconnected ||
            !Directory.Exists(database.RootPath))
            throw new InvalidOperationException("DEM高程数据库未连接，无法重新分析。");
        if (database.ValidFileCount == 0)
            throw new InvalidOperationException("当前DEM高程数据库没有有效栅格文件。");
        var snapshot = await Task.Run(() => Snapshot(project, target), cancellationToken);
        var source = new ElevationSourceDescriptor(ElevationSourceType.Dem, databaseId,
            database.Name, database.Fingerprint, database.VerticalDatumName);
        var geometryFingerprint = await Task.Run(() => _engine.GeometryFingerprint(snapshot),
            cancellationToken);
        if (!forceRefresh && FindCached(project, snapshot, source, profile,
            geometryFingerprint) is { } cached) return cached;
        var anyCoverage = await Task.Run(() => _engine.BuildGrid(snapshot, profile)
            .Any(point => _catalog.FindCovering(databaseId, point.Coordinate).Count > 0),
            cancellationToken);
        if (!anyCoverage)
            throw new InvalidOperationException(
                "当前选择的DEM高程数据库不覆盖此轨迹所在区域，无法进行DEM高程分析。");
        return await AnalyzeAsync(project, target, snapshot, source, profile, progress, cancellationToken, forceRefresh);
    }

    private async Task<ElevationAnalysisResult> AnalyzeAsync(HikingProject project,
        ElevationTargetDescriptor liveTarget, ElevationTargetDescriptor snapshot,
        ElevationSourceDescriptor source,
        ElevationAnalysisProfile profile, IProgress<(int Done, int Total)>? progress,
        CancellationToken cancellationToken, bool forceRefresh)
    {
        if (project.Id != snapshot.ProjectId || project.Tracks.All(track => track.Id != snapshot.TrackId))
            throw new InvalidOperationException("分析对象已不属于当前项目。");
        var geometryFingerprint = await Task.Run(() => _engine.GeometryFingerprint(snapshot),
            cancellationToken);
        if (!forceRefresh && FindCached(project, snapshot, source, profile,
            geometryFingerprint) is { } cached) return cached;
        var result = await Task.Run(async () =>
            await _engine.AnalyzeAsync(snapshot, source, profile,
                source.SourceType == ElevationSourceType.Dem ? _provider : null,
                progress, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // 迟到结果只能写回原项目，且不能冒充已变化的几何或DEM来源。
        if (CurrentFingerprint(project, liveTarget) != geometryFingerprint ||
            source.SourceType == ElevationSourceType.Gps &&
            CurrentGpsFingerprint(project, liveTarget) != source.SourceFingerprint ||
            source.SourceType == ElevationSourceType.Dem &&
            _catalog.Get(source.DemDatabaseId!.Value).Fingerprint != source.SourceFingerprint)
            return result with { IsStale = true };
        project.ExecuteAtomicUpdate(current => current.ElevationAnalyses.Add(result));
        return result;
    }

    private static ElevationAnalysisResult? FindCached(HikingProject project,
        ElevationTargetDescriptor target, ElevationSourceDescriptor source,
        ElevationAnalysisProfile profile, string geometryFingerprint)
    {
        lock (project)
            return project.ElevationAnalyses.Where(result =>
                result.ProjectId == project.Id && result.TargetType == target.TargetType &&
                result.TargetId == target.TargetId &&
                result.GeometryFingerprint == geometryFingerprint &&
                result.Source.SourceType == source.SourceType &&
                result.Source.DemDatabaseId == source.DemDatabaseId &&
                result.Source.SourceFingerprint == source.SourceFingerprint &&
                result.Profile == profile).OrderByDescending(result => result.CreatedAt)
                .FirstOrDefault();
    }

    private static ElevationTargetDescriptor Snapshot(HikingProject project,
        ElevationTargetDescriptor target)
    {
        lock (project)
        {
            return target with
            {
                GeometryParts = target.GeometryParts.Select(part => new TrackSegment
                {
                    Id = part.Id,
                    Points = part.Points.Select(point => new TrackPoint
                    {
                        Latitude = point.Latitude, Longitude = point.Longitude,
                        SourceElevation = point.SourceElevation,
                        IsCoordinateEdited = point.IsCoordinateEdited
                    }).ToList()
                }).ToArray()
            };
        }
    }

    private string? CurrentFingerprint(HikingProject project,
        ElevationTargetDescriptor target)
    {
        lock (project)
        {
            var track = project.Tracks.FirstOrDefault(item => item.Id == target.TrackId);
            if (track is null) return null;
            IReadOnlyList<TrackSegment>? parts = target.TargetType switch
            {
                ElevationTargetType.Track => track.Segments,
                ElevationTargetType.SplitSection => track.GetSplitSections()
                    .FirstOrDefault(section => section.Id == target.TargetId)?.GeometryParts,
                ElevationTargetType.GuideSegment => project.GuideSegments
                    .FirstOrDefault(segment => segment.Id == target.TargetId) is { } segment
                    ? ForGuideSegment(project, segment).GeometryParts : null,
                _ => target.GeometryParts
            };
            return parts is null ? null : _engine.GeometryFingerprint(target with
            { GeometryParts = parts });
        }
    }

    private string? CurrentGpsFingerprint(HikingProject project,
        ElevationTargetDescriptor target)
    {
        lock (project)
        {
            var track = project.Tracks.FirstOrDefault(item => item.Id == target.TrackId);
            if (track is null) return null;
            IReadOnlyList<TrackSegment>? parts = target.TargetType switch
            {
                ElevationTargetType.Track => track.Segments,
                ElevationTargetType.SplitSection => track.GetSplitSections()
                    .FirstOrDefault(section => section.Id == target.TargetId)?.GeometryParts,
                ElevationTargetType.GuideSegment => project.GuideSegments
                    .FirstOrDefault(segment => segment.Id == target.TargetId) is { } segment
                    ? ForGuideSegment(project, segment).GeometryParts : null,
                _ => null
            };
            return parts is null ? null : _engine.GpsFingerprint(target with
            { GeometryParts = parts });
        }
    }
}
