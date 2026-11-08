using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;

if (args.Length == 3 && args[0].Equals("--inspect", StringComparison.OrdinalIgnoreCase))
{
    var input = Path.GetFullPath(args[1]);
    var inspectReportPath = Path.GetFullPath(args[2]);
    if (!File.Exists(input)) { Console.Error.WriteLine("找不到只读轨迹文件。"); return 2; }
    var watch = Stopwatch.StartNew();
    var parsed = Path.GetExtension(input).ToLowerInvariant() switch
    {
        ".kmz" => await new KmzTrackImporter().ImportAsync(input),
        ".kml" => await new KmlTrackImporter().ImportAsync(input),
        _ => throw new ArgumentException("只支持 KML/KMZ 验收输入。")
    };
    var previewProject = new HikingProject { Name = "只读导入验收" };
    var importSession = ProjectImportSession.Create(parsed, input, previewProject);
    var importService = new ProjectImportService();
    var plan = importService.Prepare(importSession, previewProject);
    importService.Commit(importSession, previewProject, confirmFarNodes: true);
    await using var inputStream = File.OpenRead(input);
    var inputHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(inputStream));
    var result = new
    {
        输入 = input, 输入SHA256 = inputHash,
        解析毫秒 = watch.Elapsed.TotalMilliseconds, 轨迹 = parsed.Tracks.Count, 轨迹段 = parsed.TrackSegmentCount,
        轨迹点 = parsed.TrackPointCount, 标注 = parsed.Waypoints.Count, 内嵌图片 = parsed.MediaAttachments.Count,
        图片Blob = parsed.MediaBlobs.Count, 网络图片 = parsed.ExternalMediaReferences.Count,
        默认选中轨迹 = plan.Tracks.Count, 提交后轨迹 = previewProject.Tracks.Count,
        提交后轨迹点 = previewProject.Tracks.Sum(track => track.GetPointCount()),
        提交后图片 = previewProject.MediaAttachments.Count, 提交后网络图片 = previewProject.ExternalMediaReferences.Count,
        网络域名 = parsed.ExternalMediaReferences.Select(item => new Uri(item.Url).Host).Distinct().ToArray(),
        警告数 = parsed.Warnings.Count, 成功 = parsed.IsSuccess
    };
    Directory.CreateDirectory(Path.GetDirectoryName(inspectReportPath)!);
    await File.WriteAllTextAsync(inspectReportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine($"轨迹 {parsed.Tracks.Count} 条、轨迹点 {parsed.TrackPointCount} 个、标注 {parsed.Waypoints.Count} 个、内嵌图片 {parsed.MediaAttachments.Count} 张、网络图片 {parsed.ExternalMediaReferences.Count} 个；提交后轨迹 {previewProject.Tracks.Count} 条。");
    return parsed.IsSuccess ? 0 : 1;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("用法：HikingGuide.P51Verifier <只读项目.hikeproj> <报告输出目录>");
    return 2;
}
var source = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
if (!File.Exists(source)) { Console.Error.WriteLine("找不到只读项目文件。"); return 2; }
Directory.CreateDirectory(root);
var runDirectory = Path.Combine(root, "p51-export-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runDirectory);
var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
var timer = Stopwatch.StartNew();
var project = await new ProjectFileService().LoadAsync(source);
var loadMs = timer.Elapsed.TotalMilliseconds;
var workingSetAfterLoad = Process.GetCurrentProcess().WorkingSet64;
var selected = new MergedExportSelection();
foreach (var track in project.Tracks) selected.TrackIds.Add(track.Id);
foreach (var waypoint in project.Waypoints) selected.MarkerIds.Add(waypoint.Id);
foreach (var guide in project.GuideNodes) selected.MarkerIds.Add(guide.Id);
var exporter = new ProjectMergedExportService();
var outputs = new List<object>();
foreach (var format in new[] { TrackFileFormat.Gpx, TrackFileFormat.Kml, TrackFileFormat.Kmz })
{
    var path = Path.Combine(runDirectory, "合并导出." + format.ToString().ToLowerInvariant());
    timer.Restart();
    await exporter.ExportAsync(project, selected, format, path, false);
    var exportMs = timer.Elapsed.TotalMilliseconds;
    timer.Restart();
    var parsed = format switch
    {
        TrackFileFormat.Gpx => await new GpxTrackImporter().ImportAsync(path),
        TrackFileFormat.Kml => await new KmlTrackImporter().ImportAsync(path),
        _ => await new KmzTrackImporter().ImportAsync(path)
    };
    outputs.Add(new
    {
        格式 = format.ToString().ToUpperInvariant(),
        文件 = path,
        字节 = new FileInfo(path).Length,
        导出毫秒 = exportMs,
        回读毫秒 = timer.Elapsed.TotalMilliseconds,
        轨迹 = parsed.Tracks.Count,
        轨迹段 = parsed.TrackSegmentCount,
        轨迹点 = parsed.TrackPointCount,
        标注 = parsed.Waypoints.Count,
        网络图片URL = parsed.ExternalMediaReferences.Count,
        项目图片 = parsed.MediaAttachments.Count,
        回读成功 = parsed.IsSuccess,
        工作集字节 = Process.GetCurrentProcess().WorkingSet64
    });
}
var report = new
{
    输入项目 = source,
    输入轨迹 = project.Tracks.Count,
    输入轨迹段 = project.Tracks.Sum(track => track.Segments.Count),
    输入轨迹点 = project.Tracks.Sum(track => track.GetPointCount()),
    输入普通标注 = project.Waypoints.Count,
    输入攻略节点 = project.GuideNodes.Count,
    输入项目图片 = project.MediaAttachments.Count,
    输入网络图片URL = project.ExternalMediaReferences.Count,
    打开项目毫秒 = loadMs,
    打开前工作集字节 = workingSetBefore,
    打开后工作集字节 = workingSetAfterLoad,
    导出 = outputs
};
var reportPath = Path.Combine(runDirectory, "合并导出验证.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine(reportPath);
return 0;
