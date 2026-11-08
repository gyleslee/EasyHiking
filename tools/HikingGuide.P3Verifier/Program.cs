using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Logging;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;

if (args.Length < 2)
{
    Console.Error.WriteLine("用法：HikingGuide.P3Verifier <输入KML> <输出目录>");
    return 2;
}

var inputPath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
if (!File.Exists(inputPath)) { Console.Error.WriteLine("找不到真实 KML 测试文件。"); return 3; }
Directory.CreateDirectory(outputDirectory);
var projectPath = Path.Combine(outputDirectory, "环船百大纵走_P3项目.hikeproj");
var resavePath = Path.Combine(outputDirectory, "环船百大纵走_P3再保存.hikeproj");
var jsonPath = Path.Combine(outputDirectory, "P3真实项目验证.json");
var markdownPath = Path.Combine(outputDirectory, "P3真实项目验证.md");
if (new[] { projectPath, resavePath, jsonPath, markdownPath }.Any(File.Exists))
{
    Console.Error.WriteLine("验证输出已经存在。为避免覆盖，请指定新的输出目录。");
    return 4;
}

using var logging = new SerilogLoggingService(Path.Combine(outputDirectory, "logs"), false);
var trackFiles = new TrackFileService([new GpxTrackImporter(), new KmlTrackImporter()], [new GpxTrackExporter(), new KmlTrackExporter()], logging);
var projectFiles = new ProjectFileService();
var import = await trackFiles.ImportAsync(inputPath, TrackFileFormat.Kml);
if (!import.IsSuccess) { Console.Error.WriteLine(string.Join(Environment.NewLine, import.Errors.Select(e => e.Message))); return 5; }

var project = new HikingProject { Name = "环船百大纵走规划攻略" };
foreach (var track in import.Tracks) project.AddTrack(track);
foreach (var waypoint in import.Waypoints) project.Waypoints.Add(waypoint);
var originalTrackIds = project.Tracks.Select(t => t.Id).ToArray();
var originalPointIds = project.Tracks.SelectMany(t => t.Segments).SelectMany(s => s.Points).Select(p => p.Id).ToArray();
var originalWaypointIds = project.Waypoints.Select(w => w.Id).ToArray();
var originalSignature = Signature(project);

var saveTimer = Stopwatch.StartNew();
await projectFiles.SaveAsync(project, projectPath, true);
saveTimer.Stop();
var projectSize = new FileInfo(projectPath).Length;

// 关闭所有导入流后临时移走原 KML；finally 保证原文件恢复。
GC.Collect(); GC.WaitForPendingFinalizers();
var movedPath = inputPath + $".p3-validation-{Guid.NewGuid():N}.moved";
HikingProject restored;
var openTimer = new Stopwatch();
var sourceMovedOpenSucceeded = false;
try
{
    File.Move(inputPath, movedPath);
    openTimer.Start();
    restored = await projectFiles.LoadAsync(projectPath);
    openTimer.Stop();
    sourceMovedOpenSucceeded = true;
}
finally
{
    if (File.Exists(movedPath) && !File.Exists(inputPath)) File.Move(movedPath, inputPath);
}

var resaveTimer = Stopwatch.StartNew();
await projectFiles.SaveAsync(restored, resavePath, true);
resaveTimer.Stop();
using var archive = ZipFile.OpenRead(projectPath);
var entries = archive.Entries.Select(e => e.FullName).ToArray();
var restoredPointIds = restored.Tracks.SelectMany(t => t.Segments).SelectMany(s => s.Points).Select(p => p.Id).ToArray();
var report = new
{
    InputFile = inputPath,
    ProjectFile = projectPath,
    ProjectFileSizeBytes = projectSize,
    TrackCount = restored.Tracks.Count,
    TrackSegmentCount = restored.Tracks.Sum(t => t.Segments.Count),
    TrackPointCount = restored.Tracks.Sum(t => t.GetPointCount()),
    WaypointCount = restored.Waypoints.Count,
    SaveMilliseconds = saveTimer.Elapsed.TotalMilliseconds,
    OpenMilliseconds = openTimer.Elapsed.TotalMilliseconds,
    ResaveMilliseconds = resaveTimer.Elapsed.TotalMilliseconds,
    PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
    SourceMovedOpenSucceeded = sourceMovedOpenSucceeded,
    SourceRestored = File.Exists(inputPath),
    TrackIdsPreserved = originalTrackIds.SequenceEqual(restored.Tracks.Select(t => t.Id)),
    PointIdsPreserved = originalPointIds.SequenceEqual(restoredPointIds),
    WaypointIdsPreserved = originalWaypointIds.SequenceEqual(restored.Waypoints.Select(w => w.Id)),
    CoordinatesElevationsAttributesPreserved = originalSignature == Signature(restored),
    ActiveTrackIdPreserved = project.ActiveTrackId == restored.ActiveTrackId,
    ArchiveEntries = entries
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(jsonPath, json, new UTF8Encoding(false));
var markdown = $"""
# P3真实项目验证

- Track数量：{report.TrackCount}
- TrackSegment数量：{report.TrackSegmentCount}
- TrackPoint数量：{report.TrackPointCount:N0}
- Waypoint数量：{report.WaypointCount}
- 项目文件大小：{report.ProjectFileSizeBytes:N0} 字节
- 首次保存耗时：{report.SaveMilliseconds:F3} ms
- 打开耗时：{report.OpenMilliseconds:F3} ms
- 再保存耗时：{report.ResaveMilliseconds:F3} ms
- 进程峰值工作集：{report.PeakWorkingSetBytes / 1024d / 1024d:F2} MiB
- 原KML移走后打开：{YesNo(report.SourceMovedOpenSucceeded)}
- 原KML已恢复：{YesNo(report.SourceRestored)}
- Track ID保持：{YesNo(report.TrackIdsPreserved)}
- TrackPoint ID保持：{YesNo(report.PointIdsPreserved)}
- Waypoint ID保持：{YesNo(report.WaypointIdsPreserved)}
- 坐标、海拔、OriginalAttributes保持：{YesNo(report.CoordinatesElevationsAttributesPreserved)}
- ActiveTrackId保持：{YesNo(report.ActiveTrackIdPreserved)}

## 项目内部目录

{string.Join(Environment.NewLine, report.ArchiveEntries.Select(entry => $"- `{entry}`"))}
""";
await File.WriteAllTextAsync(markdownPath, markdown, new UTF8Encoding(false));
Console.WriteLine(markdown);

return report.TrackPointCount == 125910 && report.WaypointCount == 54 && report.SourceMovedOpenSucceeded &&
       report.SourceRestored && report.TrackIdsPreserved && report.PointIdsPreserved && report.WaypointIdsPreserved &&
       report.CoordinatesElevationsAttributesPreserved && report.ActiveTrackIdPreserved ? 0 : 6;

static string Signature(HikingProject project)
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    using var stream = new MemoryStream();
    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
    {
        foreach (var track in project.Tracks)
        {
            writer.Write(track.Id.ToByteArray()); writer.Write(track.Name ?? ""); writer.Write(track.Description ?? "");
            WriteDictionary(writer, track.Metadata);
            foreach (var segment in track.Segments)
            {
                writer.Write(segment.Id.ToByteArray()); WriteDictionary(writer, segment.OriginalAttributes);
                foreach (var point in segment.Points)
                {
                    writer.Write(point.Id.ToByteArray()); writer.Write(point.Latitude); writer.Write(point.Longitude);
                    writer.Write(point.SourceElevation ?? double.NaN); writer.Write(point.DemElevation ?? double.NaN);
                    writer.Write(point.Timestamp?.ToString("O") ?? ""); writer.Write(point.DistanceFromStart);
                    WriteDictionary(writer, point.OriginalAttributes);
                }
            }
        }
        foreach (var waypoint in project.Waypoints)
        {
            writer.Write(waypoint.Id.ToByteArray()); writer.Write(waypoint.Latitude); writer.Write(waypoint.Longitude);
            writer.Write(waypoint.SourceElevation ?? double.NaN); writer.Write(waypoint.DemElevation ?? double.NaN);
            writer.Write(waypoint.Timestamp?.ToString("O") ?? ""); WriteDictionary(writer, waypoint.OriginalAttributes);
        }
    }
    return Convert.ToHexString(sha.ComputeHash(stream.ToArray()));
}

static void WriteDictionary(BinaryWriter writer, IReadOnlyDictionary<string, string?> dictionary)
{
    foreach (var pair in dictionary.OrderBy(p => p.Key, StringComparer.Ordinal)) { writer.Write(pair.Key); writer.Write(pair.Value ?? ""); }
}

static string YesNo(bool value) => value ? "通过" : "失败";
