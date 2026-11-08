using System.Diagnostics;
using System.Text.Json;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Models;

if (args.Length is 5 or 6 && args[0] == "track-analysis")
{
    var longTrackPath = args[1];
    var tifRoot = args[2];
    var hgtRoot = args[3];
    var thirdRoot = args.Length == 6 ? args[4] : null;
    var longReport = args[^1];
    var workingSetAtStart = Process.GetCurrentProcess().WorkingSet64;
    var imported = await new KmlTrackImporter().ImportAsync(longTrackPath);
    var track = imported.Tracks.Single();
    track.RecalculateDistances(); // 验收副本进入项目时建立既有项目格式要求的里程缓存。
    var project = new HikingProject { Name = "P8真实轨迹验收副本" };
    project.Tracks.Add(track);
    var paths = new ApplicationDataPaths(Path.Combine(Path.GetDirectoryName(longReport)!,
        "P8LongTrack-AppData"));
    var databases = new DemDatabaseCatalog(paths);
    var tif = await databases.MountAsync("ALOS 30m", tifRoot, unitMetersConfirmed: true);
    var hgt = await databases.MountAsync("FathomDEM 30m", hgtRoot, unitMetersConfirmed: true);
    var third = thirdRoot is null ? null : await databases.MountAsync("第三套 GeoTIFF",
        thirdRoot, unitMetersConfirmed: true);
    using var longTrackProvider = new DemElevationProvider(databases);
    var coordinator = new ElevationAnalysisCoordinator(databases, longTrackProvider);
    var target = ElevationAnalysisCoordinator.ForTrack(project, track);
    var profile = new ElevationAnalysisProfile();
    var watch = Stopwatch.StartNew();
    var gps = await coordinator.AnalyzeGpsAsync(project, target, profile);
    var gpsMilliseconds = watch.ElapsedMilliseconds;
    watch.Restart();
    var a = await coordinator.AnalyzeDemAsync(project, target, tif.Database.Id, profile);
    var tifMilliseconds = watch.ElapsedMilliseconds;
    watch.Restart();
    var b = await coordinator.AnalyzeDemAsync(project, target, hgt.Database.Id, profile);
    var hgtMilliseconds = watch.ElapsedMilliseconds;
    ElevationAnalysisResult? c = null;
    long? thirdMilliseconds = null;
    if (third is not null)
    {
        watch.Restart();
        c = await coordinator.AnalyzeDemAsync(project, target, third.Database.Id, profile);
        thirdMilliseconds = watch.ElapsedMilliseconds;
    }
    watch.Restart();
    var hit = await coordinator.AnalyzeDemAsync(project, target, tif.Database.Id, profile);
    var hitMilliseconds = watch.ElapsedMilliseconds;
    var comparison = StandardElevationAnalysisV1.Compare(a, b);
    watch.Restart();
    var hoverChecksum = 0;
    for (var index = 0; index < 100_000; index++)
        hoverChecksum += ElevationChartViewport.NearestIndex(a.Series,
            (index % 1000) / 1000d * a.Summary.DistanceMeters);
    var hoverSearchMilliseconds = watch.Elapsed.TotalMilliseconds;
    watch.Restart();
    var visibleY = default(ElevationVisibleY);
    for (var index = 0; index < 1_000; index++)
    {
        var start = (index % 100) / 100d * a.Summary.DistanceMeters;
        visibleY = ElevationChartViewport.FitVisibleY(a.Series,
            ElevationChartViewport.Clamp(start, start + 1_000,
                a.Summary.DistanceMeters), visibleY);
    }
    var visibleYMilliseconds = watch.Elapsed.TotalMilliseconds;
    var projectPath = Path.ChangeExtension(longReport, ".hikeproj");
    watch.Restart();
    await new ProjectFileService().SaveAsync(project, projectPath, createBackup: false);
    var saveMilliseconds = watch.ElapsedMilliseconds;
    watch.Restart();
    var reloaded = await new ProjectFileService().LoadAsync(projectPath);
    var reloadMilliseconds = watch.ElapsedMilliseconds;
    var result = new
    {
        成功 = ReferenceEquals(a, hit) && a.Series.Count == b.Series.Count &&
            b.Series.Count == gps.Series.Count && (c is null || c.Series.Count == gps.Series.Count) &&
            reloaded.ElevationAnalyses.Count == project.ElevationAnalyses.Count &&
            reloaded.Tracks.Single().GetPointCount() == track.GetPointCount(),
        TrackPoints = track.GetPointCount(), TrackDistanceMeters = track.GetTotalLength(),
        GpsValidSourcePoints = track.Segments.Sum(part => part.Points.Count(point =>
            point.SourceElevation.HasValue)),
        GridSamples = gps.Series.Count, Gps = gps.Summary, AloS = a.Summary,
        Fathom = b.Summary, Third = c?.Summary, DemDifference = comparison,
        ThirdDifference = c is null ? null : StandardElevationAnalysisV1.Compare(a, c),
        Milliseconds = new { Gps = gpsMilliseconds, AloS = tifMilliseconds,
            Fathom = hgtMilliseconds, Third = thirdMilliseconds, CacheHit = hitMilliseconds,
            ProjectSave = saveMilliseconds, ProjectReload = reloadMilliseconds,
            HoverSearch100000 = hoverSearchMilliseconds,
            VisibleYFit1000 = visibleYMilliseconds },
        ViewportBench = new { HoverChecksum = hoverChecksum, LastVisibleY = visibleY },
        ProjectFileBytes = new FileInfo(projectPath).Length,
        Memory = new { WorkingSetAtStartBytes = workingSetAtStart,
            WorkingSetAfterBytes = Process.GetCurrentProcess().WorkingSet64,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false) },
        Databases = new[] { tif.Database, hgt.Database, third?.Database }.Where(item => item is not null).ToArray(),
        Cache = new { longTrackProvider.CacheStatus.OpenHandles,
            longTrackProvider.CacheStatus.Limit, longTrackProvider.CacheStatus.Hits,
            longTrackProvider.CacheStatus.Misses }
    };
    await File.WriteAllTextAsync(longReport, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}

if (args is ["track-info", var trackPath, var trackReport])
{
    var imported = await new KmlTrackImporter().ImportAsync(trackPath);
    var info = imported.Tracks.Select(track => new
    {
        track.Name, PointCount = track.GetPointCount(), SegmentCount = track.Segments.Count,
        DistanceMeters = track.GetTotalLength(),
        GpsValidCount = track.Segments.Sum(part => part.Points.Count(point =>
            point.SourceElevation is double value && double.IsFinite(value)))
    }).ToArray();
    await File.WriteAllTextAsync(trackReport, JsonSerializer.Serialize(info,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(info));
    return;
}

if (args is not ["scan", var root, var longitudeText, var latitudeText, var reportPath])
    throw new ArgumentException("用法：scan DEM目录 经度 纬度 报告路径");
var longitude = double.Parse(longitudeText, System.Globalization.CultureInfo.InvariantCulture);
var latitude = double.Parse(latitudeText, System.Globalization.CultureInfo.InvariantCulture);
var appdata = new ApplicationDataPaths(Path.Combine(Path.GetDirectoryName(reportPath)!,
    "P8Verifier-AppData"));
var catalog = new DemDatabaseCatalog(appdata);
var stopwatch = Stopwatch.StartNew();
var scan = await catalog.MountAsync("真实DEM测试", root, unitMetersConfirmed: true);
var scanTime = stopwatch.ElapsedMilliseconds;
using var provider = new DemElevationProvider(catalog);
var coordinate = new GeoCoordinate(latitude, longitude);
var candidates = catalog.FindCovering(scan.Database.Id, coordinate);
var sample = provider.SampleDetailed(scan.Database.Id, coordinate);
var west = provider.SampleDetailed(scan.Database.Id,
    new GeoCoordinate(latitude, longitude - 1e-7));
var east = provider.SampleDetailed(scan.Database.Id,
    new GeoCoordinate(latitude, longitude + 1e-7));
var output = new
{
    scan.Database.Name, scan.Database.RootPath, scan.Database.Status,
    scan.Database.ValidFileCount, scan.Database.InvalidFileCount,
    scan.Database.Fingerprint, 扫描毫秒 = scanTime,
    覆盖候选数 = candidates.Count, Sample = sample,
    EdgeNeighbors = new { West = west, East = east },
    Cache = new { provider.CacheStatus.OpenHandles, provider.CacheStatus.Limit,
        provider.CacheStatus.Hits, provider.CacheStatus.Misses },
    Runtime = new { DemGdalRuntime.EnsureReady().GdalVersion,
        DemGdalRuntime.EnsureReady().ProjVersion },
    Warnings = scan.Warnings.Take(10).ToArray()
};
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(output,
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(output));
