using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HikingGuide.Application.Routing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.Routing;

if (args is ["--validate-rd5", var javaForValidation, var jarForValidation,
    var fileForValidation, var validationReport])
{
    var watch = Stopwatch.StartNew();
    await new BRouterRd5Validator(javaForValidation, jarForValidation)
        .ValidateAsync(fileForValidation);
    var outcome = new { 成功 = true, 文件 = Path.GetFileName(fileForValidation),
        校验毫秒 = watch.ElapsedMilliseconds };
    await File.WriteAllTextAsync(validationReport, JsonSerializer.Serialize(outcome));
    Console.WriteLine(JsonSerializer.Serialize(outcome));
    return;
}
if (args is ["--import-real", var importRuntime, var importSource,
    var importRoot, var importReport])
{
    var importJava = Path.Combine(importRuntime, "java", "bin", "java.exe");
    var jar = Path.Combine(importRuntime, "brouter-1.7.10-all.jar");
    var sourceFiles = new[] { "E110_N20.rd5", "E115_N20.rd5" }
        .Select(name => Path.Combine(importSource, name)).ToArray();
    var hashesBefore = sourceFiles.Select(path => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    var store = new RoutingDataStore(importRoot);
    await using var engine = new BRouterProcessService(importJava, jar,
        store.CurrentSegmentsDirectory, Path.Combine(importRuntime, "profiles2"));
    var validator = new BRouterRd5Validator(importJava, jar);
    var watch = Stopwatch.StartNew();
    var imported = await store.ImportAsync(sourceFiles.Select(path =>
        new RoutingImportRequest(path, RoutingImportAction.Add)).ToArray(),
        validator, token => engine.RestartAsync(token));
    var importProvider = new BRouterProvider(engine, () => imported.Manifest.DatasetId.ToString("N"));
    var a = await importProvider.RouteFootShortestAsync(
        new GeoCoordinate(23.1382, 113.3270), new GeoCoordinate(23.1386, 113.3280));
    var b = await importProvider.RouteFootShortestAsync(
        new GeoCoordinate(24.4800, 118.0800), new GeoCoordinate(24.4810, 118.0820));
    var hashesAfter = sourceFiles.Select(path => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    var result = new { 成功 = hashesBefore.SequenceEqual(hashesAfter),
        导入文件数 = sourceFiles.Length, imported.Added, imported.Replaced, imported.Kept,
        imported.Manifest.TileCount, imported.Manifest.DatasetId,
        A区域点数 = a.Geometry.Count, B区域点数 = b.Geometry.Count,
        源哈希不变 = hashesBefore.SequenceEqual(hashesAfter), 总耗时毫秒 = watch.ElapsedMilliseconds };
    await File.WriteAllTextAsync(importReport, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}
if (args is ["--import-twenty-real", var twentyRuntime, var twentySource,
    var twentyRoot, var twentyReport])
{
    var files = Directory.EnumerateFiles(twentySource, "*.rd5")
        .OrderBy(Path.GetFileName, StringComparer.Ordinal).Take(20).ToArray();
    if (files.Length != 20 || !files.Any(file => Path.GetFileName(file) == "E110_N20.rd5"))
        throw new InvalidOperationException("真实20瓦片测试源不完整。 ");
    static string FileHash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    var before = files.Select(FileHash).ToArray();
    var store = new RoutingDataStore(twentyRoot);
    var twentyJava = Path.Combine(twentyRuntime, "java", "bin", "java.exe");
    var twentyJar = Path.Combine(twentyRuntime, "brouter-1.7.10-all.jar");
    await using var engine = new BRouterProcessService(twentyJava, twentyJar,
        store.CurrentSegmentsDirectory, Path.Combine(twentyRuntime, "profiles2"));
    var watch = Stopwatch.StartNew();
    var imported = await store.ImportAsync(files.Select(file =>
        new RoutingImportRequest(file, RoutingImportAction.Add)).ToArray(),
        new BRouterRd5Validator(twentyJava, twentyJar), token => engine.RestartAsync(token));
    var route = await new BRouterProvider(engine, () => imported.Manifest.DatasetId.ToString("N"))
        .RouteFootShortestAsync(new GeoCoordinate(23.1382, 113.3270),
            new GeoCoordinate(23.1386, 113.3280));
    var unchanged = before.SequenceEqual(files.Select(FileHash));
    var result = new { 成功 = unchanged && route.Geometry.Count > 1 &&
        imported.Manifest.TileCount == 20, 导入瓦片数 = imported.Manifest.TileCount,
        总字节 = imported.Manifest.TotalBytes, 源哈希不变 = unchanged,
        立即路由点数 = route.Geometry.Count, 耗时毫秒 = watch.ElapsedMilliseconds };
    await File.WriteAllTextAsync(twentyReport, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}
if (args is ["--migrate-real", var migrationRuntime, var migrationLegacyRoot,
    var migrationTargetRoot, var migrationReport])
{
    var store = new RoutingDataStore(migrationTargetRoot);
    var javaPath = Path.Combine(migrationRuntime, "java", "bin", "java.exe");
    await using var engine = new BRouterProcessService(javaPath,
        Path.Combine(migrationRuntime, "brouter-1.7.10-all.jar"),
        store.CurrentSegmentsDirectory, Path.Combine(migrationRuntime, "profiles2"));
    var watch = Stopwatch.StartNew();
    var changed = await store.MigrateLegacyAsync(migrationLegacyRoot,
        token => engine.RestartAsync(token));
    var manifest = store.CurrentManifest ?? throw new InvalidDataException("迁移后清单缺失。 ");
    var route = await new BRouterProvider(engine, () => manifest.DatasetId.ToString("N"))
        .RouteFootShortestAsync(new GeoCoordinate(23.1382, 113.3270),
            new GeoCoordinate(23.1386, 113.3280));
    var result = new { 成功 = changed && manifest.TileCount == 67 && route.Geometry.Count > 1,
        已迁移 = changed, manifest.TileCount, manifest.TotalBytes,
        旧测试数据集目录已移除 = !Directory.Exists(Path.Combine(migrationLegacyRoot,
            "segments4-current")), 立即路由点数 = route.Geometry.Count,
        耗时毫秒 = watch.ElapsedMilliseconds };
    await File.WriteAllTextAsync(migrationReport, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}

if (args is ["--download-one", var downloadRoot, var downloadReport])
{
    var watch = Stopwatch.StartNew();
    var store = new RoutingDataStore(downloadRoot);
    var tile = BRouterTile.ParseFileName("E110_N20.rd5");
    var dataset = await store.ReplaceAsync([tile], RoutingTileSource.WorldManualDownload, null,
        _ => Task.CompletedTask);
    var result = new { 成功 = true, dataset.TileCount, dataset.TotalBytes,
        耗时毫秒 = watch.ElapsedMilliseconds, dataset.Tiles.Single().Sha256,
        旧目录不存在 = !Directory.Exists(Path.Combine(downloadRoot, "old")) };
    await File.WriteAllTextAsync(downloadReport, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}
if (args is ["--download-china", var manifestFile, var downloadRootChina, var downloadReportChina])
{
    var source = ChinaTileManifest.Parse(await File.ReadAllBytesAsync(manifestFile));
    var watch = Stopwatch.StartNew();
    var store = new RoutingDataStore(downloadRootChina);
    var bundledRuntime = Path.GetFullPath(Path.Combine("src", "HikingGuide.App", "BRouterRuntime"));
    await using var smokeProcess = new BRouterProcessService(
        Path.Combine(bundledRuntime, "java", "bin", "java.exe"),
        Path.Combine(bundledRuntime, "brouter-1.7.10-all.jar"),
        store.CurrentSegmentsDirectory, Path.Combine(bundledRuntime, "profiles2"));
    store.ProgressChanged += (_, progress) => Console.WriteLine(
        $"BRouter数据更新：{progress.Completed} / {progress.Total}；{progress.DownloadedBytes / 1024d / 1024d:F1} MB；{progress.CurrentTile}");
    var dataset = await store.ReplaceAsync(source.Tiles, RoutingTileSource.ChinaDownload,
        source.SourceSha256, async token =>
        {
            await smokeProcess.RestartAsync(token);
            await new BRouterProvider(smokeProcess, () => "china-smoke").RouteFootShortestAsync(
                new GeoCoordinate(23.1382, 113.3270),
                new GeoCoordinate(23.1386, 113.3280), token);
        });
    var result = new { 成功 = true, source.SourceLineCount, dataset.TileCount,
        dataset.TotalBytes, 耗时毫秒 = watch.ElapsedMilliseconds,
        平均每秒字节 = dataset.TotalBytes / Math.Max(1d, watch.Elapsed.TotalSeconds),
        旧目录不存在 = !Directory.Exists(Path.Combine(downloadRootChina, "old")) };
    await File.WriteAllTextAsync(downloadReportChina, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}
if (args is ["--catalog", var catalogReport])
{
    var tiles = await new WorldTileCatalog().GetAvailableAsync();
    var result = new { 成功 = tiles.Count > 1000, 官方可选瓦片数 = tiles.Count,
        中国烟雾瓦片可选 = tiles.Contains(BRouterTile.ParseFileName("E110_N20.rd5")) };
    await File.WriteAllTextAsync(catalogReport, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}
if (args is ["--manual-two", var manualRoot, var manualReport])
{
    var available = await new WorldTileCatalog().GetAvailableAsync();
    var selection = new WorldTileSelection(available);
    selection.Toggle(new GeoCoordinate(23, 113));
    selection.SelectIntersecting(new GeoBoundingBox(20, 25, 115, 120));
    if (selection.Selected.Count != 2)
        throw new InvalidOperationException("手动选择没有得到预期的两个官方瓦片。");
    var store = new RoutingDataStore(manualRoot);
    var watch = Stopwatch.StartNew();
    var dataset = await store.ReplaceAsync(selection.Selected.ToArray(),
        RoutingTileSource.WorldManualDownload, null, _ => Task.CompletedTask);
    var result = new { 成功 = true, dataset.TileCount, dataset.TotalBytes,
        耗时毫秒 = watch.ElapsedMilliseconds,
        当前文件 = Directory.EnumerateFiles(store.CurrentSegmentsDirectory, "*.rd5")
            .Select(Path.GetFileName).OrderBy(name => name).ToArray(),
        旧目录不存在 = !Directory.Exists(Path.Combine(manualRoot, "old")) };
    await File.WriteAllTextAsync(manualReport, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}
if (args is ["--benchmark", var benchmarkRoot, var benchmarkSegments,
    var benchmarkJava, var benchmarkReport])
{
    await using var engine = new BRouterProcessService(benchmarkJava,
        Path.Combine(benchmarkRoot, "brouter-1.7.10-all.jar"), benchmarkSegments,
        Path.Combine(benchmarkRoot, "profiles2"));
    var startWatch = Stopwatch.StartNew();
    await engine.StartAsync();
    var startup = startWatch.ElapsedMilliseconds;
    var router = new BRouterProvider(engine, () => "china-67");
    var origin = new GeoCoordinate(23.1382, 113.3270);
    var pairs = new[]
    {
        (名称: "短", 终点: new GeoCoordinate(23.1386, 113.3280)),
        (名称: "中", 终点: new GeoCoordinate(23.1398, 113.3310)),
        (名称: "长", 终点: new GeoCoordinate(23.1600, 113.4000))
    };
    var twoPoint = new List<object>();
    foreach (var pair in pairs)
    {
        var watch = Stopwatch.StartNew();
        var route = await router.RouteFootShortestAsync(origin, pair.终点);
        var meters = 0d;
        for (var index = 1; index < route.Geometry.Count; index++)
            meters += HikingGuide.Core.Services.GeoDistanceCalculator.Instance.CalculateDistance(
                route.Geometry[index - 1], route.Geometry[index]);
        twoPoint.Add(new { pair.名称, 起点 = origin, pair.终点, 米 = meters,
            点数 = route.Geometry.Count, 耗时毫秒 = watch.ElapsedMilliseconds, route.Profile });
    }
    var plan = new RoutePlanService(Guid.NewGuid(), RoutePlanningMode.BRouter, router);
    var legs = new List<object>();
    for (var index = 0; index < 10; index++)
    {
        var watch = Stopwatch.StartNew();
        var leg = await plan.AddPointAsync(new GeoCoordinate(23.1382 + index * 0.0004,
            113.3270 + index * 0.0010));
        if (leg is not null) legs.Add(new { 顺序 = index, 耗时毫秒 = watch.ElapsedMilliseconds,
            米 = leg.DistanceMeters, 点数 = leg.Geometry.Count });
    }
    var performanceReport = new { 成功 = true, BRouter启动毫秒 = startup, 两点路线 = twoPoint,
        控制点数 = plan.Plan.ControlPoints.Count, 分段 = legs };
    await File.WriteAllTextAsync(benchmarkReport, JsonSerializer.Serialize(performanceReport,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(performanceReport));
    return;
}
if (args is ["--missing-tile", var missingRuntime, var missingSegments,
    var missingJava, var missingReport])
{
    await using var engine = new BRouterProcessService(missingJava,
        Path.Combine(missingRuntime, "brouter-1.7.10-all.jar"), missingSegments,
        Path.Combine(missingRuntime, "profiles2"));
    var missingProvider = new BRouterProvider(engine, () => "missing-tile-test");
    string message;
    try
    {
        await missingProvider.RouteFootShortestAsync(new GeoCoordinate(23.2, 118.2),
            new GeoCoordinate(23.21, 118.21));
        throw new InvalidOperationException("缺瓦片测试意外返回路线。");
    }
    catch (InvalidOperationException exception) when (
        exception.Message != "缺瓦片测试意外返回路线。")
    {
        message = exception.Message;
    }
    var result = new { 成功 = message.Contains("路由数据", StringComparison.Ordinal) ||
        message.Contains("BRouter路由失败", StringComparison.Ordinal), 错误提示 = message };
    await File.WriteAllTextAsync(missingReport, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}

var runtime = args.Length > 0 ? args[0] : throw new ArgumentException("请提供 BRouter 测试运行目录。");
var reportPath = args.Length > 1 ? args[1] : throw new ArgumentException("请提供验证报告路径。");
var java = args.Length > 2 ? args[2] : throw new ArgumentException("请提供 Java 可执行文件路径。");
var total = Stopwatch.StartNew();
await using var process = new BRouterProcessService(java,
    Path.Combine(runtime, "brouter-1.7.10-all.jar"),
    Path.Combine(runtime, "segments4"), Path.Combine(runtime, "profiles2"));
var start = Stopwatch.StartNew();
await process.StartAsync();
var startupMs = start.ElapsedMilliseconds;
var provider = new BRouterProvider(process, () => "P7-real-E110_N20");
var project = new HikingProject { Name = "P7真实BRouter验证副本" };
var points = new[]
{
    new GeoCoordinate(23.1382, 113.3270),
    new GeoCoordinate(23.1386, 113.3280),
    new GeoCoordinate(23.1390, 113.3290),
    new GeoCoordinate(23.1394, 113.3300),
    new GeoCoordinate(23.1398, 113.3310)
};
var mixed = new RoutePlanService(project.Id, RoutePlanningMode.Mixed, provider);
var legReports = new List<object>();
for (var index = 0; index < points.Length; index++)
{
    if (index == 3) mixed.SetNextLegMode(RouteLegMode.Manual);
    if (index == 4) mixed.SetNextLegMode(RouteLegMode.BRouter);
    var watch = Stopwatch.StartNew();
    var existing = mixed.Plan.Legs.Select(Hash).ToArray();
    var leg = await mixed.AddPointAsync(points[index]);
    if (!existing.SequenceEqual(mixed.Plan.Legs.Take(existing.Length).Select(Hash)))
        throw new InvalidOperationException("新增规划点改变了旧分段几何。");
    if (leg is not null)
        legReports.Add(new { Mode = leg.Mode.ToString(), leg.DistanceMeters,
            PointCount = leg.Geometry.Count, Hash = Hash(leg), ElapsedMs = watch.ElapsedMilliseconds,
            leg.BRouterRequestMetadata?.Profile });
}
var callsBeforeRedo = mixed.Plan.Legs.Count;
var lastHash = Hash(mixed.Plan.Legs[^1]);
mixed.UndoLastPoint();
mixed.RedoLastPoint();
if (Hash(mixed.Plan.Legs[^1]) != lastHash || mixed.Plan.Legs.Count != callsBeforeRedo)
    throw new InvalidOperationException("撤销重做未恢复原始几何。");
var track = mixed.BuildTrack("P7真实混合规划轨迹");
project.AddTrack(track, false);
var projectPath = Path.ChangeExtension(reportPath, ".hikeproj");
await new ProjectFileService().SaveAsync(project, projectPath, false);
var reopened = await new ProjectFileService().LoadAsync(projectPath);
if (reopened.Tracks.Single().Segments.Count != 4 || reopened.MainGuideTrackId is not null)
    throw new InvalidOperationException("规划轨迹保存重开校验失败。");
var report = new { 成功 = true, BRouter版本 = "1.7.10", Java = java,
    BRouter启动毫秒 = startupMs, BRouter端口 = process.Port, 总耗时毫秒 = total.ElapsedMilliseconds,
    分段 = legReports, Track点数 = track.GetPointCount(), Track米 = track.GetTotalLength(),
    项目副本 = projectPath };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
await File.WriteAllTextAsync(reportPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));

static string Hash(RouteLeg leg) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join(";", leg.Geometry.Select(point =>
            $"{point.Latitude:R},{point.Longitude:R}")))));
