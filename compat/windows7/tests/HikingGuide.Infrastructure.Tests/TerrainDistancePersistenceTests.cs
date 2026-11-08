using HikingGuide.Core.Elevation;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;

public sealed class TerrainDistancePersistenceTests
{
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine("F:\\Codex\\Temp", "HikingGuide-P9B-Tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private sealed class FailProvider : IElevationProvider
    {
        public ValueTask<double?> SampleAsync(Guid id, HikingGuide.Core.Geometry.GeoCoordinate coordinate, CancellationToken token = default)
            => throw new InvalidOperationException("GPS及缓存重分类不应读取DEM。");
    }

    private sealed class CountingProvider : IElevationProvider
    {
        public int Reads { get; private set; }
        public ValueTask<double?> SampleAsync(Guid id, HikingGuide.Core.Geometry.GeoCoordinate coordinate, CancellationToken token = default)
        { Reads++; return ValueTask.FromResult<double?>(100 + (coordinate.Latitude - 23) * 3000); }
    }

    [Fact]
    public async Task DemThresholdChangeUsesRawCacheAndFingerprintChangeInvalidatesIt()
    {
        using var directory = new TemporaryDirectory();
        var paths = new ApplicationDataPaths(directory.Path);
        var database = new DemDatabase(Guid.NewGuid(), "测试DEM", directory.Path, UnitMetersConfirmed: true,
            Status: DemDatabaseStatus.Ready, Fingerprint: "before", ValidFileCount: 1);
        var config = Path.Combine(directory.Path, "dem-databases.json");
        await File.WriteAllTextAsync(config, System.Text.Json.JsonSerializer.Serialize(new[] { database }));
        Directory.CreateDirectory(Path.Combine(directory.Path, "DemCache"));
        var entry = new RasterIndexEntry("test.hgt", "test.hgt", DemRasterFormat.Hgt,
            new HikingGuide.Core.Geometry.GeoBoundingBox(22, 24, 112, 114), "EPSG:4326", 2, 2,
            [112, 1, 0, 24, 0, -1], 30, null, "m", DateTime.UtcNow, 8);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "DemCache", $"{database.Id:N}.json"),
            System.Text.Json.JsonSerializer.Serialize(new { database.Fingerprint, Entries = new[] { entry } }));
        var catalog = new DemDatabaseCatalog(paths); await catalog.LoadAsync();
        var project = new HikingProject(); var track = new Track();
        track.Segments.Add(new() { Points = [new() { Latitude = 23, Longitude = 113 }, new() { Latitude = 23.002, Longitude = 113 }] });
        track.RecalculateDistances(); project.AddTrack(track);
        var provider = new CountingProvider();
        var service = new ElevationAnalysisCoordinator(catalog, provider);
        var target = ElevationAnalysisCoordinator.ForTrack(project, track);
        var original = await service.AnalyzeDemAsync(project, target, database.Id, new());
        var result = await service.EnsureTerrainClassificationAsync(project, target, original, new());
        var readCount = provider.Reads;
        var changed = await service.EnsureTerrainClassificationAsync(project, target, result, new(4));
        Assert.Equal(readCount, provider.Reads);
        Assert.Same(result.TerrainClassification!.RawSeries, changed.TerrainClassification!.RawSeries);
        await File.WriteAllTextAsync(config, System.Text.Json.JsonSerializer.Serialize(new[] { database with { Fingerprint = "after" } }));
        await catalog.LoadAsync();
        var stale = await service.EnsureTerrainClassificationAsync(project, target, changed, new());
        Assert.True(stale.IsStale);
        Assert.True(stale.TerrainClassification!.IsStale);
        Assert.Equal(readCount, provider.Reads);
    }

    [Fact]
    public async Task GpsClassificationRoundTripKeepsProfileRawCacheAndP8Statistics()
    {
        using var directory = new TemporaryDirectory();
        var project = new HikingProject();
        var track = new Track();
        track.Segments.Add(new() { Points = [new() { Latitude = 23, Longitude = 113, SourceElevation = 10 },
            new() { Latitude = 23.002, Longitude = 113, SourceElevation = 20 }] });
        track.RecalculateDistances(); project.AddTrack(track);
        var target = ElevationAnalysisCoordinator.ForTrack(project, track);
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(directory.Path));
        var service = new ElevationAnalysisCoordinator(catalog, new FailProvider());
        var original = await service.AnalyzeGpsAsync(project, target, new());
        var result = await service.EnsureTerrainClassificationAsync(project, target, original, new());
        var changed = await service.EnsureTerrainClassificationAsync(project, target, result, new(3));
        Assert.Same(result.TerrainClassification!.RawSeries, changed.TerrainClassification!.RawSeries);
        Assert.Equal(original.Summary, changed.Summary);
        Assert.Same(original.Series, changed.Series);
        var path = Path.Combine(directory.Path, "terrain.hikeproj");
        var files = new ProjectFileService();
        await files.SaveAsync(project, path, false);
        var reopened = await files.LoadAsync(path);
        var restored = Assert.Single(reopened.ElevationAnalyses);
        Assert.Equal(changed.TerrainClassification.Profile, restored.TerrainClassification!.Profile);
        Assert.Equal(changed.TerrainClassification.RawSeries.Count, restored.TerrainClassification.RawSeries.Count);
        Assert.Equal(changed.TerrainClassification.FlatDistanceMeters, restored.TerrainClassification.FlatDistanceMeters);
        Assert.Equal(original.Summary.AscentMeters, restored.Summary.AscentMeters);
        Assert.Equal(10, reopened.Tracks[0].Segments[0].Points[0].SourceElevation);
    }

    [Fact]
    public async Task ChangedGeometryRejectsOldRawCache()
    {
        using var directory = new TemporaryDirectory();
        var project = new HikingProject(); var track = new Track();
        track.Segments.Add(new() { Points = [new() { Latitude = 23, Longitude = 113, SourceElevation = 10 }, new() { Latitude = 23.002, Longitude = 113, SourceElevation = 20 }] });
        track.RecalculateDistances(); project.AddTrack(track);
        var service = new ElevationAnalysisCoordinator(new DemDatabaseCatalog(new ApplicationDataPaths(directory.Path)), new FailProvider());
        var target = ElevationAnalysisCoordinator.ForTrack(project, track);
        var result = await service.AnalyzeGpsAsync(project, target, new());
        result = await service.EnsureTerrainClassificationAsync(project, target, result, new());
        track.Segments[0].Points[1].Latitude += 0.001;
        var stale = await service.EnsureTerrainClassificationAsync(project, target, result, new(3));
        Assert.True(stale.IsStale);
        Assert.True(stale.TerrainClassification!.IsStale);
    }
}
