using System.Security.Cryptography;
using System.Text;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;

namespace HikingGuide.Infrastructure.Guides;

/// <summary>独立于窗口的攻略计算。只消费原始轨迹及既有P8/P9算法，结果发布前重新校验输入。</summary>
public sealed class GuideDataCoordinator(DemDatabaseCatalog catalog, IElevationProvider provider)
{
    private readonly StandardElevationAnalysisV1 _engine = new();
    public const string Version = "P10-P8V2-P9Approved-v2";
    private sealed record Input(string Fingerprint, string Geometry, string SourceFingerprint,
        GuideCalculationSettings Settings, ElevationTargetDescriptor Track,
        GuideNode[] Nodes, ElevationTargetDescriptor[] Ranges, long Revision);

    public string Fingerprint(HikingProject project)
    {
        lock (project)
        {
            var track = project.MainGuideTrack ?? throw new InvalidOperationException("项目尚未设置攻略轨迹。");
            GuideExportSnapshotBuilder.ValidateTrack(track);
            var nodes = GuideDataReconciler.OrderedNodes(project);
            if (nodes.Count < 2) throw new InvalidOperationException("至少需要两个有效攻略节点。");
            var settings = project.GuideData.Settings;
            var target = ElevationAnalysisCoordinator.ForTrack(project, track);
            var geometry = _engine.GeometryFingerprint(target);
            var source = settings.ElevationSourceKind == GuideElevationSourceKind.Gps ? _engine.GpsFingerprint(target) :
                settings.DemDatabaseId is Guid id ? catalog.Get(id).Fingerprint : throw new InvalidOperationException("请选择攻略计算使用的DEM数据库。");
            return Identity(track.Id, geometry, source, settings, nodes);
        }
    }
    private static string Identity(Guid trackId, string geometry, string source, GuideCalculationSettings settings, IReadOnlyList<GuideNode> nodes) =>
        Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(Encoding.UTF8.GetBytes(string.Join('|', trackId, geometry,
            settings.ElevationSourceKind, settings.DemDatabaseId, source, Version,
            GuideTimingService.SteepUphillThresholdPercent.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string.Join(';', nodes.Select(n => FormattableString.Invariant($"{n.Id:D}:{n.ExactPosition!.SegmentIndex}:{n.ExactPosition.StartPointIndex}:{n.ExactPosition.EndPointIndex}:{n.ExactPosition.Fraction:R}:{n.ExactPosition.DistanceFromTrackStart:R}:{n.ExactPosition.SnappedCoordinate.Latitude:R}:{n.ExactPosition.SnappedCoordinate.Longitude:R}")))))));

    private Input Capture(HikingProject project)
    {
        lock (project)
        {
            var track = project.MainGuideTrack ?? throw new InvalidOperationException("项目尚未设置攻略轨迹。");
            GuideExportSnapshotBuilder.ValidateTrack(track);
            var nodes = GuideDataReconciler.OrderedNodes(project).ToArray();
            if (nodes.Length < 2) throw new InvalidOperationException("至少需要两个有效攻略节点。");
            var settings = project.GuideData.Settings; settings.Validate();
            var target = ElevationAnalysisCoordinator.ForTrack(project, track);
            var geometry = _engine.GeometryFingerprint(target);
            var source = settings.ElevationSourceKind == GuideElevationSourceKind.Gps ? _engine.GpsFingerprint(target) :
                settings.DemDatabaseId is Guid id ? catalog.Get(id).Fingerprint : throw new InvalidOperationException("请选择攻略计算使用的DEM数据库。");
            var fingerprint = Identity(track.Id, geometry, source, settings, nodes);
            // 将所有几何复制到计算快照，后台不能枚举可变的GUI模型。
            var copy = new HikingProject();
            var cloned = new Track { Id = track.Id, Name = track.Name, Segments = target.GeometryParts.Select(p => new TrackSegment
                { Id = p.Id, Points = p.Points.Select(t => new TrackPoint { Latitude = t.Latitude, Longitude = t.Longitude,
                    SourceElevation = t.SourceElevation, IsCoordinateEdited = t.IsCoordinateEdited }).ToList() }).ToList() };
            copy.Tracks.Add(cloned);
            var snapshots = nodes.Select(n => new GuideNode { Id = n.Id, Name = n.Name, TrackId = track.Id, ExactPosition = n.ExactPosition,
                SnappedCoordinate = n.ExactPosition!.SnappedCoordinate }).ToArray();
            foreach (var node in snapshots) copy.GuideNodes.Add(node);
            var firstPart = cloned.Segments.First(p => p.Points.Count > 0);
            var firstIndex = cloned.Segments.IndexOf(firstPart);
            var origin = new GuideNode { TrackId = cloned.Id, ExactPosition = new TrackPosition { TrackId = cloned.Id,
                TrackSegmentId = firstPart.Id, SegmentIndex = firstIndex, SnappedCoordinate = firstPart.Points[0].Coordinate } };
            copy.GuideNodes.Add(origin);
            var ranges = snapshots.Select((n, i) => ElevationAnalysisCoordinator.ForGuideSegment(copy,
                new GuideSegment { TrackId = cloned.Id, StartNodeId = i == 0 ? origin.Id : snapshots[i - 1].Id, EndNodeId = n.Id })).ToArray();
            var immutableTarget = new ElevationTargetDescriptor(project.Id, target.TargetType, target.TargetId, target.TrackId, target.DisplayName, cloned.Segments);
            return new(fingerprint, geometry, source, settings, immutableTarget, snapshots,
                ranges.Select(r => r with { ProjectId = project.Id }).ToArray(), (project.GuideData.Derived?.ResultRevision ?? 0) + 1);
        }
    }

    public async Task<GuideDataCalculationResult> CalculateAsync(HikingProject project,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default, bool force = false)
    {
        var input = await Task.Run(() => Capture(project), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (project)
            if (!force && project.GuideData.Derived is { } cached && cached.InputFingerprint == input.Fingerprint)
            {
                if (cached.IsStale) { var current = cached with { IsStale = false }; project.UpdateGuideData(d => d.Derived = current); return current; }
                return cached;
            }
        var result = await Task.Run(async () =>
        {
            var profile = new LocusTerrainDistanceProfile();
            var elevationProfile = new ElevationAnalysisProfile(profile.SampleSpacingMeters, profile.SmoothingDistanceMeters, profile.SlopeWindowMeters);
            var gps = input.Settings.ElevationSourceKind == GuideElevationSourceKind.Gps;
            if (!gps && catalog.Get(input.Settings.DemDatabaseId!.Value).Status == DemDatabaseStatus.Disconnected)
                throw new InvalidOperationException("DEM数据库未连接；可查看历史统计，不能重新计算。");
            var source = new ElevationSourceDescriptor(gps ? ElevationSourceType.Gps : ElevationSourceType.Dem,
                gps ? null : input.Settings.DemDatabaseId, gps ? "GPS原始高程" : catalog.Get(input.Settings.DemDatabaseId!.Value).Name, input.SourceFingerprint);
            var raw = gps ? _engine.BuildGrid(input.Track, new(profile.SampleSpacingMeters, 0, profile.SlopeWindowMeters))
                .Select(p => new TerrainElevationSample(p.PartIndex, p.DistanceMeters, p.Coordinate, p.GpsElevationMeters)).ToArray() :
                await UniformTerrainSampler.SampleDemAsync(input.Track.GeometryParts, input.Settings.DemDatabaseId!.Value, provider, profile.SampleSpacingMeters, cancellationToken);
            var terrain = LocusTerrainDistanceClassifier.Classify(raw, profile, input.Geometry, input.SourceFingerprint, cancellationToken);
            var output = new List<GuideNodeDerivedData>(); double? ascent = 0, descent = 0;
            for (var i = 0; i < input.Nodes.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = input.Nodes[i]; var position = node.ExactPosition!;
                var analysis = await _engine.AnalyzeAsync(input.Ranges[i], source, elevationProfile, provider, cancellationToken: cancellationToken);
                // P8 Summary保留部分覆盖信息，但累计量只能在完整覆盖时作为最终结果。
                var complete = analysis.Summary.Availability == ElevationAvailability.Complete;
                double? up = complete ? analysis.Summary.AscentMeters : null, down = complete ? analysis.Summary.DescentMeters : null;
                ascent = ascent.HasValue && up.HasValue ? ascent + up : null;
                descent = descent.HasValue && down.HasValue ? descent + down : null;
                var height = gps ? GpsAt(input.Track, position) : await provider.SampleAsync(input.Settings.DemDatabaseId!.Value, position.SnappedCoordinate, cancellationToken);
                if (height is double h && !double.IsFinite(h)) height = null;
                GuideSegmentStatistics? incoming = null;
                if (i > 0)
                {
                    var start = input.Nodes[i - 1].ExactPosition!.DistanceFromTrackStart;
                    var distance = position.DistanceFromTrackStart - start;
                    var split = GuideTimingService.SplitTerrain(terrain, start, position.DistanceFromTrackStart);
                    var terrainComplete = split.UnclassifiedMeters <= 1e-6;
                    incoming = new(new(input.Track.TrackId, input.Nodes[i - 1].Id, node.Id), distance, up, down,
                        terrainComplete ? split.UphillMeters : null, terrainComplete ? split.DownhillMeters : null,
                        terrainComplete ? split.FlatMeters : null, terrainComplete ? split.GentleUphillMeters : null,
                        terrainComplete ? split.SteepUphillMeters : null, split.UnclassifiedMeters, complete && terrainComplete);
                }
                output.Add(new(node.Id, position.DistanceFromTrackStart, height, ascent, descent, incoming));
                progress?.Report((i + 1, input.Nodes.Length));
            }
            var coverage = raw.Count == 0 ? 0 : (double)raw.Count(p => p.ElevationMeters is double h && double.IsFinite(h)) / raw.Count;
            return new GuideDataCalculationResult(input.Fingerprint, input.Geometry, input.SourceFingerprint, Version,
                input.Revision, DateTimeOffset.UtcNow, output, CoverageRatio: coverage);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = await Task.Run(() =>
        {
            lock (project)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Fingerprint(project) != input.Fingerprint) return false;
                project.UpdateGuideData(data => data.Derived = result);
                return true;
            }
        }, cancellationToken);
        if (!accepted) return result with { IsStale = true };
        return result;
    }

    private static double? GpsAt(ElevationTargetDescriptor target, TrackPosition p)
    {
        var points = target.GeometryParts[p.SegmentIndex].Points;
        var a = points[p.StartPointIndex]; var b = points[p.EndPointIndex];
        if (p.Fraction <= 1e-9) return a.IsCoordinateEdited ? null : a.SourceElevation;
        if (p.Fraction >= 1 - 1e-9) return b.IsCoordinateEdited ? null : b.SourceElevation;
        return a is { IsCoordinateEdited: false, SourceElevation: double x } && b is { IsCoordinateEdited: false, SourceElevation: double y }
            ? x + (y - x) * p.Fraction : null;
    }
}
