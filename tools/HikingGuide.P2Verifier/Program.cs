using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Logging;
using HikingGuide.Infrastructure.TrackFiles;

if (args.Length < 2)
{
    Console.Error.WriteLine("用法：HikingGuide.P2Verifier <输入KML> <输出目录>");
    return 2;
}

var inputPath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine("找不到指定的真实KML测试文件。");
    return 3;
}

Directory.CreateDirectory(outputDirectory);
var exportPath = Path.Combine(outputDirectory, "环船百大纵走规划攻略_P2往返验证.kml");
var jsonReportPath = Path.Combine(outputDirectory, "P2真实轨迹验证.json");
var markdownReportPath = Path.Combine(outputDirectory, "P2真实轨迹验证.md");
if (File.Exists(exportPath) || File.Exists(jsonReportPath) || File.Exists(markdownReportPath))
{
    Console.Error.WriteLine("验证输出已经存在。为避免覆盖，请指定新的输出目录。");
    return 6;
}

using var logging = new SerilogLoggingService(Path.Combine(outputDirectory, "logs"), enableDebugLogging: false);
var service = new TrackFileService(
    [new GpxTrackImporter(), new KmlTrackImporter()],
    [new GpxTrackExporter(), new KmlTrackExporter()],
    logging);

var importTimer = Stopwatch.StartNew();
var imported = await service.ImportAsync(inputPath, TrackFileFormat.Kml);
importTimer.Stop();
if (!imported.IsSuccess)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, imported.Errors.Select(error => error.Message)));
    return 4;
}

var distanceTimer = Stopwatch.StartNew();
foreach (var track in imported.Tracks)
{
    track.RecalculateDistances();
}
distanceTimer.Stop();

var exportTimer = Stopwatch.StartNew();
await service.ExportAsync(
    TrackFileFormat.Kml,
    new TrackExportRequest
    {
        DocumentName = imported.Metadata.TryGetValue("kml:name", out var documentName)
            ? documentName
            : Path.GetFileNameWithoutExtension(inputPath),
        Description = imported.Metadata.TryGetValue("kml:description", out var description)
            ? description
            : null,
        Tracks = imported.Tracks,
        Waypoints = imported.Waypoints,
        Metadata = imported.Metadata
    },
    exportPath);
exportTimer.Stop();

var reimportTimer = Stopwatch.StartNew();
var reimported = await service.ImportAsync(exportPath, TrackFileFormat.Kml);
reimportTimer.Stop();

var boundingBox = imported.GetBoundingBox();
var namesToInspect = new[]
{
    "后山村", "帽子峰", "后山顶", "梅花顶", "黄思脑", "第一登船底顶",
    "联山农庄民宿", "上天堂山顶", "第二登船底顶"
};
var waypointChecks = namesToInspect.Select(expectedName =>
{
    var waypoint = imported.Waypoints.FirstOrDefault(item =>
        string.Equals(item.Name, expectedName, StringComparison.Ordinal) ||
        item.Name?.Contains(expectedName, StringComparison.Ordinal) == true);
    return new WaypointCheck(
        expectedName,
        waypoint is not null,
        waypoint?.Name,
        waypoint?.Latitude,
        waypoint?.Longitude,
        waypoint?.SourceElevation,
        Truncate(waypoint?.Description, 160));
}).ToArray();

var roundTrip = new RoundTripSummary(
    reimported.IsSuccess,
    imported.Tracks.Count == reimported.Tracks.Count,
    imported.TrackSegmentCount == reimported.TrackSegmentCount,
    imported.TrackPointCount == reimported.TrackPointCount,
    imported.Waypoints.Count == reimported.Waypoints.Count,
    CoordinatesAndDirectionMatch(imported, reimported));

var report = new VerificationReport(
    InputFile: inputPath,
    FileSizeBytes: new FileInfo(inputPath).Length,
    TrackCount: imported.Tracks.Count,
    TrackSegmentCount: imported.TrackSegmentCount,
    TrackPointCount: imported.TrackPointCount,
    WaypointCount: imported.Waypoints.Count,
    TotalDistanceMeters: imported.TotalDistance,
    MinLatitude: boundingBox?.MinLatitude,
    MaxLatitude: boundingBox?.MaxLatitude,
    MinLongitude: boundingBox?.MinLongitude,
    MaxLongitude: boundingBox?.MaxLongitude,
    TrackPointsWithElevation: imported.TrackPointsWithElevation,
    TrackPointsWithTimestamp: imported.TrackPointsWithTimestamp,
    ImportMilliseconds: importTimer.Elapsed.TotalMilliseconds,
    DistanceRecalculationMilliseconds: distanceTimer.Elapsed.TotalMilliseconds,
    ExportMilliseconds: exportTimer.Elapsed.TotalMilliseconds,
    ReimportMilliseconds: reimportTimer.Elapsed.TotalMilliseconds,
    ProcessPeakWorkingSetBytes: Process.GetCurrentProcess().PeakWorkingSet64,
    ExportFile: exportPath,
    ExportFileSizeBytes: new FileInfo(exportPath).Length,
    WarningCount: imported.Warnings.Count,
    RoundTrip: roundTrip,
    FirstWaypointNames: imported.Waypoints.Take(12).Select(waypoint => waypoint.Name).ToArray(),
    WaypointChecks: waypointChecks);

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
await File.WriteAllTextAsync(
    jsonReportPath,
    JsonSerializer.Serialize(report, jsonOptions),
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
await File.WriteAllTextAsync(
    markdownReportPath,
    BuildMarkdown(report),
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Console.WriteLine(BuildMarkdown(report));
return roundTrip is
{
    ReimportSucceeded: true,
    TrackCountMatches: true,
    SegmentCountMatches: true,
    PointCountMatches: true,
    WaypointCountMatches: true,
    CoordinatesAndDirectionMatch: true
} ? 0 : 5;

static bool CoordinatesAndDirectionMatch(TrackImportResult first, TrackImportResult second)
{
    if (first.Tracks.Count != second.Tracks.Count)
    {
        return false;
    }

    for (var trackIndex = 0; trackIndex < first.Tracks.Count; trackIndex++)
    {
        var left = first.Tracks[trackIndex];
        var right = second.Tracks[trackIndex];
        if (left.Segments.Count != right.Segments.Count)
        {
            return false;
        }

        for (var segmentIndex = 0; segmentIndex < left.Segments.Count; segmentIndex++)
        {
            var leftPoints = left.Segments[segmentIndex].Points;
            var rightPoints = right.Segments[segmentIndex].Points;
            if (leftPoints.Count != rightPoints.Count)
            {
                return false;
            }

            for (var pointIndex = 0; pointIndex < leftPoints.Count; pointIndex++)
            {
                if (leftPoints[pointIndex].Coordinate != rightPoints[pointIndex].Coordinate ||
                    leftPoints[pointIndex].SourceElevation != rightPoints[pointIndex].SourceElevation)
                {
                    return false;
                }
            }
        }
    }

    return true;
}

static string? Truncate(string? value, int maximumLength) =>
    value is null || value.Length <= maximumLength ? value : value[..maximumLength] + "…";

static string BuildMarkdown(VerificationReport report)
{
    var builder = new StringBuilder();
    builder.AppendLine("# P2真实轨迹验证");
    builder.AppendLine();
    builder.AppendLine($"- 输入文件：`{report.InputFile}`");
    builder.AppendLine($"- 文件大小：{report.FileSizeBytes:N0} 字节");
    builder.AppendLine($"- 轨迹数量：{report.TrackCount}");
    builder.AppendLine($"- 轨迹段数量：{report.TrackSegmentCount}");
    builder.AppendLine($"- 轨迹点数量：{report.TrackPointCount:N0}");
    builder.AppendLine($"- 标注点数量：{report.WaypointCount}");
    builder.AppendLine($"- 总距离：{report.TotalDistanceMeters / 1000:F3} km");
    builder.AppendLine($"- 纬度范围：{Format(report.MinLatitude)} ～ {Format(report.MaxLatitude)}");
    builder.AppendLine($"- 经度范围：{Format(report.MinLongitude)} ～ {Format(report.MaxLongitude)}");
    builder.AppendLine($"- 有海拔轨迹点：{report.TrackPointsWithElevation:N0}");
    builder.AppendLine($"- 有时间轨迹点：{report.TrackPointsWithTimestamp:N0}");
    builder.AppendLine($"- 导入耗时：{report.ImportMilliseconds:F3} ms");
    builder.AppendLine($"- 距离重算耗时：{report.DistanceRecalculationMilliseconds:F3} ms");
    builder.AppendLine($"- 导出耗时：{report.ExportMilliseconds:F3} ms");
    builder.AppendLine($"- 再导入耗时：{report.ReimportMilliseconds:F3} ms");
    builder.AppendLine($"- 进程峰值工作集：{report.ProcessPeakWorkingSetBytes / 1024d / 1024d:F2} MiB");
    builder.AppendLine($"- 导出文件大小：{report.ExportFileSizeBytes:N0} 字节");
    builder.AppendLine($"- 导入警告：{report.WarningCount}");
    builder.AppendLine();
    builder.AppendLine("## 往返结果");
    builder.AppendLine();
    builder.AppendLine($"- 再导入成功：{YesNo(report.RoundTrip.ReimportSucceeded)}");
    builder.AppendLine($"- 轨迹数量一致：{YesNo(report.RoundTrip.TrackCountMatches)}");
    builder.AppendLine($"- 轨迹段数量一致：{YesNo(report.RoundTrip.SegmentCountMatches)}");
    builder.AppendLine($"- 轨迹点数量一致：{YesNo(report.RoundTrip.PointCountMatches)}");
    builder.AppendLine($"- 标注点数量一致：{YesNo(report.RoundTrip.WaypointCountMatches)}");
    builder.AppendLine($"- 坐标、海拔和方向一致：{YesNo(report.RoundTrip.CoordinatesAndDirectionMatch)}");
    builder.AppendLine();
    builder.AppendLine("## 前12个标注点名称");
    builder.AppendLine();
    foreach (var name in report.FirstWaypointNames)
    {
        builder.AppendLine($"- {name ?? "（未命名）"}");
    }

    builder.AppendLine();
    builder.AppendLine("## 指定标注人工抽查");
    builder.AppendLine();
    foreach (var item in report.WaypointChecks)
    {
        builder.AppendLine(
            $"- {item.ExpectedName}：{(item.Found ? $"找到 `{item.ActualName}`，" +
                $"坐标 {Format(item.Latitude)},{Format(item.Longitude)}，" +
                $"原始海拔 {Format(item.SourceElevation)}，描述 `{item.Description}`" : "未找到")}");
    }

    return builder.ToString();
}

static string Format(double? value) =>
    value?.ToString("R", CultureInfo.InvariantCulture) ?? "无";

static string YesNo(bool value) => value ? "是" : "否";

internal sealed record VerificationReport(
    string InputFile,
    long FileSizeBytes,
    int TrackCount,
    int TrackSegmentCount,
    int TrackPointCount,
    int WaypointCount,
    double TotalDistanceMeters,
    double? MinLatitude,
    double? MaxLatitude,
    double? MinLongitude,
    double? MaxLongitude,
    int TrackPointsWithElevation,
    int TrackPointsWithTimestamp,
    double ImportMilliseconds,
    double DistanceRecalculationMilliseconds,
    double ExportMilliseconds,
    double ReimportMilliseconds,
    long ProcessPeakWorkingSetBytes,
    string ExportFile,
    long ExportFileSizeBytes,
    int WarningCount,
    RoundTripSummary RoundTrip,
    string?[] FirstWaypointNames,
    WaypointCheck[] WaypointChecks);

internal sealed record RoundTripSummary(
    bool ReimportSucceeded,
    bool TrackCountMatches,
    bool SegmentCountMatches,
    bool PointCountMatches,
    bool WaypointCountMatches,
    bool CoordinatesAndDirectionMatch);

internal sealed record WaypointCheck(
    string ExpectedName,
    bool Found,
    string? ActualName,
    double? Latitude,
    double? Longitude,
    double? SourceElevation,
    string? Description);
