using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;

public sealed class ElevationPersistenceTests
{
    [Fact]
    public async Task ProjectRoundTrip_PreservesAnalysisWithoutDemFiles()
    {
        var directory = Path.Combine("F:\\Codex\\Temp", "HikingGuide-P8-Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = new HikingProject { Name = "离线分析结果" };
            var track = new Track { Name = "测试轨迹" };
            track.Segments.Add(new TrackSegment { Points =
            [
                new TrackPoint { Latitude = 23, Longitude = 113, SourceElevation = 10 },
                new TrackPoint { Latitude = 23.001, Longitude = 113, SourceElevation = 20 }
            ] });
            track.RecalculateDistances();
            project.AddTrack(track);
            var target = ElevationAnalysisCoordinator.ForTrack(project, track);
            var engine = new StandardElevationAnalysisV1();
            var result = await engine.AnalyzeAsync(target,
                new ElevationSourceDescriptor(ElevationSourceType.Gps, null, "GPS",
                    engine.GpsFingerprint(target)), new ElevationAnalysisProfile());
            project.ElevationAnalyses.Add(result);
            var file = Path.Combine(directory, "test.hikeproj");
            var service = new ProjectFileService();
            await service.SaveAsync(project, file, true);
            var loaded = await service.LoadAsync(file);
            var restored = Assert.Single(loaded.ElevationAnalyses);
            Assert.Equal(result.CacheKey, restored.CacheKey);
            Assert.Equal(result.Series.Count, restored.Series.Count);
            Assert.Equal(result.Summary.AscentMeters, restored.Summary.AscentMeters);
            Assert.Equal(project.Tracks[0].Segments[0].Points[0].SourceElevation,
                loaded.Tracks[0].Segments[0].Points[0].SourceElevation);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task GpsCache_InvalidatesWhenSourceElevationChanges_AndUndoReusesOriginal()
    {
        var project = new HikingProject { Name = "GPS缓存" };
        var track = new Track { Name = "测试" };
        track.Segments.Add(new TrackSegment { Points =
        [
            new TrackPoint { Latitude = 23, Longitude = 113, SourceElevation = 10 },
            new TrackPoint { Latitude = 23.001, Longitude = 113, SourceElevation = 20 }
        ] });
        track.RecalculateDistances();
        project.AddTrack(track);
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(
            "F:\\Codex\\Temp", "HikingGuide-P8-Tests", Guid.NewGuid().ToString("N"))));
        using var provider = new DemElevationProvider(catalog);
        var coordinator = new ElevationAnalysisCoordinator(catalog, provider);
        var target = ElevationAnalysisCoordinator.ForTrack(project, track);
        var profile = new ElevationAnalysisProfile();
        var first = await coordinator.AnalyzeGpsAsync(project, target, profile);
        Assert.Same(first, await coordinator.AnalyzeGpsAsync(project, target, profile));
        track.Segments[0].Points[1].SourceElevation = 30;
        var changed = await coordinator.AnalyzeGpsAsync(project, target, profile);
        Assert.NotEqual(first.Id, changed.Id);
        track.Segments[0].Points[1].SourceElevation = 20;
        Assert.Same(first, await coordinator.AnalyzeGpsAsync(project, target, profile));
    }

    [Fact]
    public async Task ForceRefresh_BypassesCacheButKeepsOriginalSource()
    {
        var project = new HikingProject { Name = "强制分析" };
        var track = new Track { Name = "测试" };
        track.Segments.Add(new TrackSegment { Points =
        [
            new TrackPoint { Latitude = 23, Longitude = 113, SourceElevation = 100 },
            new TrackPoint { Latitude = 23.001, Longitude = 113, SourceElevation = 120 }
        ] });
        track.RecalculateDistances();
        project.AddTrack(track);
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(
            "F:\\Codex\\Temp", "HikingGuide-P81-Tests", Guid.NewGuid().ToString("N"))));
        using var provider = new DemElevationProvider(catalog);
        var coordinator = new ElevationAnalysisCoordinator(catalog, provider);
        var target = ElevationAnalysisCoordinator.ForTrack(project, track);
        var profile = new ElevationAnalysisProfile();
        var first = await coordinator.AnalyzeGpsAsync(project, target, profile);
        Assert.Same(first, await coordinator.AnalyzeGpsAsync(project, target, profile));
        var forced = await coordinator.AnalyzeGpsAsync(project, target, profile,
            forceRefresh: true);
        Assert.NotEqual(first.Id, forced.Id);
        Assert.Equal(first.Summary.AscentMeters, forced.Summary.AscentMeters);
        Assert.Equal(100, track.Segments[0].Points[0].SourceElevation);
    }

    [Fact]
    public async Task OldProfileCache_DoesNotSatisfyNewDefaults()
    {
        var project = new HikingProject();
        var track = new Track();
        track.Segments.Add(new TrackSegment { Points =
        [
            new TrackPoint { Latitude = 23, Longitude = 113, SourceElevation = 100 },
            new TrackPoint { Latitude = 23.001, Longitude = 113, SourceElevation = 120 }
        ] });
        track.RecalculateDistances();
        project.AddTrack(track);
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(
            "F:\\Codex\\Temp", "HikingGuide-P81-Tests", Guid.NewGuid().ToString("N"))));
        using var provider = new DemElevationProvider(catalog);
        var coordinator = new ElevationAnalysisCoordinator(catalog, provider);
        var target = ElevationAnalysisCoordinator.ForTrack(project, track);
        var old = await coordinator.AnalyzeGpsAsync(project, target,
            new ElevationAnalysisProfile(10, 30, 30, "StandardElevationAnalysisV1"));
        var current = await coordinator.AnalyzeGpsAsync(project, target,
            new ElevationAnalysisProfile());
        Assert.NotEqual(old.Id, current.Id);
        Assert.Equal("StandardElevationAnalysisV2", current.AnalysisProfileVersion);
    }

    [Fact]
    public async Task GuideSegmentAcrossPhysicalGap_UsesTwoParts_AndPreservesManualText()
    {
        var project = new HikingProject { Name = "攻略分段 Gap" };
        var track = new Track { Name = "主轨迹" };
        var first = new TrackSegment { Points =
        [
            new TrackPoint { Latitude = 23, Longitude = 113, SourceElevation = 100 },
            new TrackPoint { Latitude = 23.0001, Longitude = 113, SourceElevation = 110 }
        ] };
        var second = new TrackSegment { Points =
        [
            new TrackPoint { Latitude = 24, Longitude = 114, SourceElevation = 900 },
            new TrackPoint { Latitude = 24.0001, Longitude = 114, SourceElevation = 910 }
        ] };
        track.Segments.Add(first);
        track.Segments.Add(second);
        track.RecalculateDistances();
        project.AddTrack(track);
        project.MainGuideTrackId = track.Id;
        var start = new GuideNode { Name = "起点", TrackId = track.Id,
            OriginalCoordinate = first.Points[0].Coordinate,
            SnappedCoordinate = first.Points[0].Coordinate,
            ExactPosition = new TrackPosition { TrackId = track.Id,
                TrackSegmentId = first.Id, SegmentIndex = 0, StartPointIndex = 0,
                EndPointIndex = 0, SnappedCoordinate = first.Points[0].Coordinate } };
        var end = new GuideNode { Name = "终点", TrackId = track.Id,
            OriginalCoordinate = second.Points[1].Coordinate,
            SnappedCoordinate = second.Points[1].Coordinate,
            ExactPosition = new TrackPosition { TrackId = track.Id,
                TrackSegmentId = second.Id, SegmentIndex = 1, StartPointIndex = 1,
                EndPointIndex = 1, SnappedCoordinate = second.Points[1].Coordinate,
                DistanceFromTrackStart = track.GetTotalLength() } };
        project.GuideNodes.Add(start);
        project.GuideNodes.Add(end);
        var guideSegment = new GuideSegment { TrackId = track.Id,
            StartNodeId = start.Id, EndNodeId = end.Id,
            ManualData = new SegmentManualData { Description = "人工攻略不得删除" } };
        project.GuideSegments.Add(guideSegment);
        var target = ElevationAnalysisCoordinator.ForGuideSegment(project, guideSegment);
        Assert.Equal(2, target.GeometryParts.Count);
        var result = await new StandardElevationAnalysisV1().AnalyzeAsync(target,
            new ElevationSourceDescriptor(ElevationSourceType.Gps, null, "GPS", "test"),
            new ElevationAnalysisProfile(5, 0, 30));
        Assert.Equal(20, result.Summary.AscentMeters!.Value, 2);
        Assert.Equal(first.Length + second.Length, result.Summary.DistanceMeters, 2);
        Assert.Equal("人工攻略不得删除", guideSegment.ManualData!.Description);
    }
}
