using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Tests;

public sealed class ElevationGuideMarkersTests
{
    private sealed class Distance : IGeoDistanceService
    { public double CalculateDistance(GeoCoordinate a, GeoCoordinate b) => Math.Abs(b.Longitude - a.Longitude); }
    private static readonly ElevationSourceDescriptor Gps = new(ElevationSourceType.Gps, null, "GPS", "gps");
    private static (HikingProject Project, Track Track, ElevationTargetDescriptor Target) Setup()
    {
        var track = new Track { Segments = [Part(0, 100), Part(100, 900)] };
        track.RecalculateDistances(new Distance());
        var project = new HikingProject(); project.Tracks.Add(track);
        return (project, track, new(project.Id, ElevationTargetType.Track, track.Id, track.Id, "轨迹", track.Segments));
    }
    private static TrackSegment Part(double x, double height) => new() { Points = [new() { Latitude = 0, Longitude = x, SourceElevation = height }, new() { Latitude = 0, Longitude = x + 10, SourceElevation = height + 10 }] };
    private static GuideNode Node(HikingProject project, Track track, int part, double distance)
    {
        var node = new GuideNode { TrackId = track.Id, SourceElevation = -999, ExactPosition = new() { TrackId = track.Id, TrackSegmentId = track.Segments[part].Id, DistanceFromTrackStart = distance, SnappedCoordinate = new(0, part * 100 + distance - part * 10) } };
        project.GuideNodes.Add(node); return node;
    }
    private static Task<ElevationAnalysisResult> Analyze(ElevationTargetDescriptor target) => new StandardElevationAnalysisV1(new Distance()).AnalyzeAsync(target, Gps, new(10, 0, 10));

    [Fact] public async Task InterpolatesCurve_NotNodeElevation()
    { var (p,t,target) = Setup(); var n = Node(p,t,0,5); var m = Assert.Single(ElevationGuideMarkers.Build(p,target,await Analyze(target))); Assert.Equal(105,m.ElevationMeters); Assert.Equal(5,m.DistanceMeters); Assert.Equal(n.ExactPosition!.SnappedCoordinate,m.Coordinate); }
    [Fact] public async Task UsesDisplayedSourceElevation()
    { var (p,t,target) = Setup(); Node(p,t,0,5); var r = await Analyze(target); r = r with { Series = r.Series.Select(s => s with { ProcessedElevationMeters = 500 }).ToArray() }; Assert.Equal(500,Assert.Single(ElevationGuideMarkers.Build(p,target,r)).ElevationMeters); }
    [Fact] public async Task GapAtSameChainage_UsesCorrectPhysicalPart()
    { var (p,t,target) = Setup(); Node(p,t,0,10); Node(p,t,1,10); var markers = ElevationGuideMarkers.Build(p,target,await Analyze(target)); Assert.Equal(new double[] {110,900},markers.Select(m=>m.ElevationMeters)); }
    [Fact] public async Task MissingHeight_DoesNotBridgeGap()
    { var (p,t,target) = Setup(); Node(p,t,0,5); var r = await Analyze(target); r = r with { Series = r.Series.Select(s => s.PartIndex == 0 && s.DistanceMeters == 10 ? s with { ProcessedElevationMeters = null } : s).ToArray() }; Assert.Empty(ElevationGuideMarkers.Build(p,target,r)); }
    [Fact] public async Task SplitSection_UsesLocalDistanceAndExcludesOtherPart()
    { var (p,t,target) = Setup(); Node(p,t,0,5); var n = Node(p,t,1,15); n.ExactPosition = n.ExactPosition! with { DistanceFromTrackStart = t.Segments[0].Length + 5 }; target = target with { TargetId = Guid.NewGuid(), TargetType = ElevationTargetType.SplitSection, GeometryParts = [t.Segments[1]] }; var m = Assert.Single(ElevationGuideMarkers.Build(p,target,await Analyze(target))); Assert.Equal(5,m.DistanceMeters); Assert.Equal(905,m.ElevationMeters); }
    [Fact] public async Task GuideSegment_UsesStartOffsetAndClippedParts()
    { var (p,t,target) = Setup(); var start = Node(p,t,0,5); var end = Node(p,t,0,10); Node(p,t,1,15); var segment = new GuideSegment { TrackId = t.Id, StartNodeId = start.Id, EndNodeId = end.Id }; p.GuideSegments.Add(segment); var part = new TrackSegment { Id = t.Segments[0].Id, Points = [new() { Latitude = 0, Longitude = 5, SourceElevation = 105 }, new() { Latitude = 0, Longitude = 10, SourceElevation = 110 }] }; target = target with { TargetId = segment.Id, TargetType = ElevationTargetType.GuideSegment, GeometryParts = [part] }; var markers = ElevationGuideMarkers.Build(p,target,await Analyze(target)); Assert.Equal(new double[] {0,5},markers.Select(m=>m.DistanceMeters)); }
    [Fact] public async Task ForeignAndOrphanNodes_AreExcluded()
    { var (p,t,target) = Setup(); Node(p,t,0,5).IsOrphaned = true; Node(p,t,0,5).TrackId = Guid.NewGuid(); var n = Node(p,t,0,5); n.ExactPosition = n.ExactPosition! with {TrackId = Guid.NewGuid()}; Assert.Empty(ElevationGuideMarkers.Build(p,target,await Analyze(target))); }
    [Fact] public async Task ForeignProjectResult_IsExcluded()
    { var (p,t,target) = Setup(); Node(p,t,0,5); Assert.Empty(ElevationGuideMarkers.Build(p,target,(await Analyze(target)) with { ProjectId = Guid.NewGuid() })); }
}
