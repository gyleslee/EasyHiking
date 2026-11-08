using System.Security.Cryptography;
using System.Text.Json;
using HikingGuide.Infrastructure.Calibration;
using HikingGuide.Infrastructure.TrackFiles;
using HikingGuide.Core.Calibration;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Projects;

if (args is ["approved-regression", var approvedRoot])
{
    await ApprovedRegression.RunAsync(approvedRoot);
    return;
}

if (args is ["boundary-validation", var boundaryRoot])
{
    await BoundaryValidation.RunAsync(boundaryRoot);
    return;
}

if (args is ["search", var searchRoot])
{
    await CandidateSearch.RunAsync(searchRoot);
    return;
}

if (args is ["align", var alignmentRoot, var alignmentOutput])
{
    alignmentRoot = Path.GetFullPath(alignmentRoot);
    alignmentOutput = Path.GetFullPath(alignmentOutput);
    if (alignmentOutput.Equals(alignmentRoot, StringComparison.OrdinalIgnoreCase) || alignmentOutput.StartsWith(alignmentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("输出目录不能位于参考目录中。");
    Directory.CreateDirectory(alignmentOutput);
    var alignments = new List<object>();
    var csv = new List<string> { "Route,Row,Position,Status,Candidates,RawProjections,StartMeters,EndMeters,GeometryDistanceMeters,DistanceErrorMeters,EligibleForFit,Reason" };
    foreach (var route in new[] { "甘孜格聂中线2025", "环船百大纵走规划攻略", "张掖祁连山冷龙岭-门源2025" })
    {
        var path = Path.Combine(alignmentRoot, route + ".kml");
        var excel = Path.Combine(alignmentRoot, route + ".xlsx");
        var before = Hash(path);
        var excelBefore = Hash(excel);
        var imported = await new KmlTrackImporter().ImportAsync(path);
        var reference = LocusReferenceExtractor.Read(excel);
        var profile = new ReferenceAlignmentProfile();
        var aligned = ReferenceAlignment.Align(imported.Tracks.Single(), imported.Waypoints.ToArray(), reference.Segments, profile);
        alignments.Add(new { Route = route, KmlSha256 = before, ExcelSha256 = excelBefore, Profile = profile, Segments = aligned });
        foreach (var row in aligned)
            csv.Add(string.Join(',', Csv(route), row.Reference.RowNumber, Csv(row.Reference.Position), row.Status,
                row.Candidates.Count, row.RawProjectionCount, Numeric(row.StartDistanceMeters), Numeric(row.EndDistanceMeters),
                Numeric(row.GeometryDistanceMeters), Numeric(row.DistanceErrorMeters), row.EligibleForFit, Csv(row.Reason)));
        Console.WriteLine($"{route}：可拟合 {aligned.Count(r => r.EligibleForFit)}/{aligned.Count}；" +
            string.Join("，", aligned.GroupBy(r => r.Status).Select(g => g.Key + "=" + g.Count())));
        if (before != Hash(path) || excelBefore != Hash(excel)) throw new IOException("对齐期间原文件发生变化。");
    }
    await File.WriteAllTextAsync(Path.Combine(alignmentOutput, "alignment-report.json"), JsonSerializer.Serialize(alignments, new JsonSerializerOptions { WriteIndented = true }));
    await File.WriteAllLinesAsync(Path.Combine(alignmentOutput, "alignment-report.csv"), csv, new System.Text.UTF8Encoding(true));
    return;
}

if (args is ["dem-sample", var sampleRoot, var catalogRoot, var samplesOutput])
{
    sampleRoot = Path.GetFullPath(sampleRoot);
    samplesOutput = Path.GetFullPath(samplesOutput);
    if (samplesOutput.Equals(sampleRoot, StringComparison.OrdinalIgnoreCase) ||
        samplesOutput.StartsWith(sampleRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("输出目录不能位于用户参考文件目录中。");
    Directory.CreateDirectory(samplesOutput);
    var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(catalogRoot));
    await catalog.LoadAsync(); // 只读当前实际挂载配置，不重新挂载或重写用户配置。
    if (catalog.Databases.Count != 3) throw new InvalidOperationException("P9校准要求核对当前实际挂载的三套DEM。当前数量不为三。");
    using var provider = new DemElevationProvider(catalog);
    var profile = new CalibrationSamplingProfile();
    var coverage = new List<object>();
    var coverageCsv = new List<string> { "Route,DemDatabaseId,DemName,SampleCount,ValidSamples,NoDataSamples,CoveragePercent,EligibleFullRoutePair,CalibrationGpsElevationUsed" };
    var databaseDetails = catalog.Databases.Select(d => new { Database = d,
        Formats = catalog.GetEntries(d.Id).Select(e => e.Format.ToString()).Distinct().ToArray(),
        Coverage = catalog.GetEntries(d.Id).Select(e => e.Wgs84Coverage).ToArray() });
    await File.WriteAllTextAsync(Path.Combine(samplesOutput, "dem-databases-audit.json"),
        JsonSerializer.Serialize(databaseDetails, new JsonSerializerOptions { WriteIndented = true }));
    foreach (var route in new[] { "甘孜格聂中线2025", "环船百大纵走规划攻略", "张掖祁连山冷龙岭-门源2025" })
    {
        var path = Path.Combine(sampleRoot, route + ".kml");
        var hash = Hash(path);
        var imported = await new KmlTrackImporter().ImportAsync(path);
        if (imported.Tracks.Count != 1) throw new InvalidDataException("参考路线必须人工核对多轨迹归属，不能自动拼接。");
        var track = imported.Tracks.Single();
        foreach (var database in catalog.Databases)
        {
            Console.WriteLine($"DEM采样：{route} × {database.Name}");
            var series = await new CalibrationDemSampler().SampleAsync(track.Segments.ToArray(),
                database.Id, provider, profile);
            var valid = series.Count(s => s.DemElevationMeters.HasValue);
            var ratio = series.Count == 0 ? 0 : (double)valid / series.Count;
            var file = route + "-" + database.Id + "-dem-series.json";
            await File.WriteAllTextAsync(Path.Combine(samplesOutput, file), JsonSerializer.Serialize(new
            {
                Route = route, KmlSha256 = hash, database.Id, database.Name, database.Fingerprint,
                CalibrationGpsElevationUsed = false, Profile = profile, Series = series
            }));
            coverage.Add(new { Route = route, database.Id, database.Name, database.Fingerprint,
                SampleCount = series.Count, ValidSamples = valid, NoDataSamples = series.Count - valid,
                CoverageRatio = ratio, EligibleFullRoutePair = ratio >= profile.MinimumFullCoverageRatio,
                CalibrationGpsElevationUsed = false, SeriesFile = file });
            coverageCsv.Add(string.Join(',', Csv(route), database.Id, Csv(database.Name), series.Count,
                valid, series.Count - valid, (ratio * 100).ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                ratio >= profile.MinimumFullCoverageRatio, false));
            Console.WriteLine($"有效 {valid}/{series.Count}，覆盖率 {ratio:P2}。");
        }
        if (hash != Hash(path)) throw new IOException("DEM采样期间参考轨迹发生变化。");
    }
    await File.WriteAllTextAsync(Path.Combine(samplesOutput, "coverage-matrix.json"),
        JsonSerializer.Serialize(coverage, new JsonSerializerOptions { WriteIndented = true }));
    await File.WriteAllLinesAsync(Path.Combine(samplesOutput, "coverage-matrix.csv"), coverageCsv, new System.Text.UTF8Encoding(true));
    return;
}

if (args is not ["reference-audit", var inputRoot, var outputRoot])
    throw new ArgumentException("用法：reference-audit 参考目录 输出目录；dem-sample 参考目录 应用数据目录 输出目录；align 参考目录 输出目录；search 校准输出目录；boundary-validation 校准输出目录");
inputRoot = Path.GetFullPath(inputRoot);
outputRoot = Path.GetFullPath(outputRoot);
if (outputRoot.Equals(inputRoot, StringComparison.OrdinalIgnoreCase) ||
    outputRoot.StartsWith(inputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("输出目录不能位于用户参考文件目录中。");
Directory.CreateDirectory(outputRoot);
var names = new[] { "甘孜格聂中线2025", "环船百大纵走规划攻略", "张掖祁连山冷龙岭-门源2025" };
var results = new List<object>();
foreach (var name in names)
{
    var kmlPath = Path.Combine(inputRoot, name + ".kml");
    var workbookPath = Path.Combine(inputRoot, name + ".xlsx");
    var beforeKml = Hash(kmlPath);
    var beforeWorkbook = Hash(workbookPath);
    var imported = await new KmlTrackImporter().ImportAsync(kmlPath);
    var audit = LocusReferenceExtractor.Read(workbookPath);
    var unchanged = beforeKml == Hash(kmlPath) && beforeWorkbook == Hash(workbookPath);
    if (!unchanged) throw new IOException("读取期间参考文件发生改变，请重新核对。");
    results.Add(new
    {
        Route = name, KmlPath = kmlPath, ExcelPath = workbookPath,
        KmlSha256 = beforeKml, ExcelSha256 = beforeWorkbook,
        SourceFilesUnchanged = unchanged,
        CalibrationGpsElevationUsed = false,
        Tracks = imported.Tracks.Select(t => new { t.Name, PointCount = t.GetPointCount(),
            Parts = t.Segments.Count, DistanceMeters = t.GetTotalLength(),
            SourceElevationCountAuditOnly = t.Segments.Sum(p => p.Points.Count(v => v.SourceElevation.HasValue)) }),
        ReferenceRows = audit.Segments.Count,
        CompleteDistanceTargetRows = audit.Segments.Count(s => s.HasCompleteDistanceTargets),
        Reference = audit
    });
    Console.WriteLine($"已核对：{name}；参考行 {audit.Segments.Count}，完整目标 {audit.Segments.Count(s => s.HasCompleteDistanceTargets)}。");
}
var report = new { Stage = "P9-A参考数据审计（未完成校准）", CreatedAt = DateTimeOffset.UtcNow,
    CalibrationGpsElevationUsed = false, FormalAlgorithmChanged = false, Routes = results };
await File.WriteAllTextAsync(Path.Combine(outputRoot, "reference-dataset.json"),
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"只读审计结果：{Path.Combine(outputRoot, "reference-dataset.json")}");

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
static string Numeric(double? value) => value?.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) ?? "";
