using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;
using HikingGuide.Application.TrackFiles;

if (args.Length != 4)
{
    Console.Error.WriteLine("用法：HikingGuide.P6Verifier <125910点项目> <17732点项目> <30737点项目> <输出目录>");
    return 2;
}
var paths = args.Take(3).Select(Path.GetFullPath).ToArray();
var output = Path.GetFullPath(args[3]);
if (paths.Any(path => !File.Exists(path)))
{
    Console.Error.WriteLine("验收项目不存在。" );
    return 3;
}
Directory.CreateDirectory(output);
var sourceHashes = paths.ToDictionary(path => path, path =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
var store = new ProjectFileService();
var editor = new TrackEditService();
var snap = new TrackSnapService();
var timings = new Dictionary<string, double>();
var memory = new Dictionary<string, long>();
var checks = new Dictionary<string, bool>();
static double Measure(Dictionary<string, double> timings, string name, Action action)
{
    var timer = Stopwatch.StartNew(); action(); timer.Stop();
    timings[name] = timer.Elapsed.TotalMilliseconds;
    return timer.Elapsed.TotalMilliseconds;
}
static TrackPosition PositionAt(Track track, int segmentIndex, int pointIndex, double fraction)
{
    var segment = track.Segments[segmentIndex];
    var a = segment.Points[pointIndex]; var b = segment.Points[pointIndex + 1];
    var length = GeoDistanceCalculator.Instance.CalculateDistance(a.Coordinate, b.Coordinate);
    var coordinate = new GeoCoordinate(a.Latitude + (b.Latitude - a.Latitude) * fraction,
        a.Longitude + (b.Longitude - a.Longitude) * fraction);
    return new TrackPosition
    {
        TrackId = track.Id, TrackSegmentId = segment.Id, SegmentIndex = segmentIndex,
        StartPointIndex = pointIndex, EndPointIndex = pointIndex + 1, Fraction = fraction,
        SnappedCoordinate = coordinate, DistanceToTrack = 0,
        DistanceFromTrackStart = a.DistanceFromStart + length * fraction,
        DistanceFromSegmentStart = a.DistanceFromStart - segment.Points[0].DistanceFromStart + length * fraction,
        TrackGeometryFingerprint = new TrackSnapService().GetGeometryFingerprint(track)
    };
}
var timer = Stopwatch.StartNew();
var initial = await store.LoadAsync(paths[0]);
timer.Stop(); timings["打开125910点项目毫秒"] = timer.Elapsed.TotalMilliseconds;
memory["项目打开后字节"] = GC.GetTotalMemory(true);
var original = initial.MainGuideTrack ?? throw new InvalidOperationException("真实项目缺少主攻略轨迹。");
checks["真实项目规模"] = original.GetPointCount() == 125910 && initial.GuideNodes.Count == 9 &&
    initial.GuideSegments.Count == 8;
var baseline = snap.GetGeometryFingerprint(original);
var edited = initial;
var track = original;
var moveEntries = new List<object>();
foreach (var (index, ordinal) in new[] { 1000, 60000, 120000 }.Select((value, i) => (value, i)))
{
    var point = track.Segments[0].Points[index];
    var old = point.Coordinate;
    var next = new GeoCoordinate(old.Latitude + 0.000001 * (ordinal + 1), old.Longitude);
    TrackEditPlan? plan = null;
    Measure(timings, $"移动点{ordinal + 1}准备毫秒", () =>
        plan = editor.PrepareMovePoint(edited, track.Id, point.Id, next));
    Measure(timings, $"移动点{ordinal + 1}提交毫秒", () => plan!.Apply());
    moveEntries.Add(new { 点ID = point.Id, 旧坐标 = old, 新坐标 = next,
        Revision = track.GeometryRevision });
}
checks["三处移动仅增加三次Revision"] = track.GeometryRevision == 3 &&
    track.Segments[0].Points[1000].IsCoordinateEdited &&
    track.Segments[0].Points[60000].IsCoordinateEdited &&
    track.Segments[0].Points[120000].IsCoordinateEdited;
memory["三次移动后字节"] = GC.GetTotalMemory(true);

var insertion = editor.PrepareInsertPoint(edited, track.Id, track.Segments[0].Id,
    20000, new GeoCoordinate(track.Segments[0].Points[20000].Latitude + 0.00001,
        track.Segments[0].Points[20000].Longitude + 0.00001));
var insertedId = Guid.Empty;
Measure(timings, "插入点提交毫秒", () => insertion.Apply());
insertedId = track.Segments[0].Points[20001].Id;
var inserted = track.Segments[0].Points[20001];
checks["新增点来源状态"] = inserted.IsInsertedByEditor && inserted.IsCoordinateEdited &&
    inserted.SourceElevation is null && inserted.Timestamp is null;
insertion.Undo(); insertion.Apply();
var deletion = editor.PrepareDeletePoint(edited, track.Id, insertedId);
Measure(timings, "删除点提交毫秒", () => deletion.Apply());
checks["插入撤销重做删除"] = track.GetPointCount() == 125910 &&
    track.Segments[0].Points.All(point => point.Id != insertedId);

var beforeSplit = track.GetTotalLength();
var nodeCoordinates = edited.GuideNodes.ToDictionary(node => node.Id, node => node.SnappedCoordinate);
TrackEditPlan? split = null;
Measure(timings, "拆分准备毫秒", () => split = editor.PrepareSplitSegment(edited,
    track.Id, track.Segments[0].Id, 50000, 0.5));
Measure(timings, "拆分提交毫秒", () => split!.Apply());
checks["拆分长度与节点位置"] = track.Segments.Count == 2 &&
    Math.Abs(track.GetTotalLength() - beforeSplit) < 0.1 &&
    edited.GuideNodes.All(node => node.SnappedCoordinate == nodeCoordinates[node.Id]);
split!.Undo();
memory["拆分撤销后字节"] = GC.GetTotalMemory(true);

var smallProject = await store.LoadAsync(paths[0]);
var smallTrack = smallProject.MainGuideTrack!;
var smallStart = PositionAt(smallTrack, 0, 2000, 0);
var smallEnd = PositionAt(smallTrack, 0, 2020, 1);
TrackEditPlan? smallDelete = null;
Measure(timings, "短区间删除准备毫秒", () => smallDelete = editor.PrepareDeleteRange(smallProject,
    new TrackRange(smallTrack.Id, smallStart, smallEnd)));
Measure(timings, "短区间删除提交毫秒", () => smallDelete!.Apply());
checks["删除区间形成断点"] = smallTrack.Segments.Count == 2 &&
    smallTrack.Segments[0].EndPoint!.Coordinate != smallTrack.Segments[1].StartPoint!.Coordinate;
memory["区间删除后字节"] = GC.GetTotalMemory(true);

var orphanProject = await store.LoadAsync(paths[0]);
var orphanTrack = orphanProject.MainGuideTrack!;
var victim = orphanProject.GuideNodes.First(node => node.ExactPosition is { StartPointIndex: > 20 });
var victimIndex = victim.ExactPosition!.StartPointIndex;
var unaffected = orphanProject.GuideNodes.Where(node => node.Id != victim.Id)
    .ToDictionary(node => node.Id, node => node.SnappedCoordinate);
var orphanRange = new TrackRange(orphanTrack.Id,
    PositionAt(orphanTrack, 0, victimIndex - 10, 0),
    PositionAt(orphanTrack, 0, victimIndex + 10, 1));
var orphanPlan = editor.PrepareDeleteRange(orphanProject, orphanRange);
var victimBefore = new { 名称 = victim.Name, 失联 = victim.IsOrphaned,
    精确位置存在 = victim.ExactPosition is not null,
    原坐标 = victim.OriginalCoordinate,
    其他节点数 = unaffected.Count };
orphanPlan.Apply();
var victimAfter = new { 名称 = victim.Name, 失联 = victim.IsOrphaned,
    精确位置存在 = victim.ExactPosition is not null,
    原坐标 = victim.OriginalCoordinate,
    其他节点坐标保持数 = orphanProject.GuideNodes.Where(node => node.Id != victim.Id)
        .Count(node => node.SnappedCoordinate == unaffected[node.Id]) };
checks["删除节点所在范围仅令其失联"] = victim.IsOrphaned && victim.ExactPosition is null &&
    orphanProject.GuideNodes.Where(node => node.Id != victim.Id)
        .All(node => node.SnappedCoordinate == unaffected[node.Id]);
orphanPlan.Undo();
var victimUndone = !victim.IsOrphaned && victim.ExactPosition is not null;
checks["失联撤销可恢复"] = !victim.IsOrphaned && victim.ExactPosition is not null;

var cropProject = await store.LoadAsync(paths[0]);
var cropTrack = cropProject.MainGuideTrack!;
var crop = editor.PrepareCropRange(cropProject, new TrackRange(cropTrack.Id,
    PositionAt(cropTrack, 0, 5000, 0), PositionAt(cropTrack, 0, 100000, 1)));
Measure(timings, "长区间裁剪提交毫秒", () => crop.Apply());
checks["裁剪保留节点并失联范围外节点"] = cropProject.GuideNodes.Count == 9 &&
    cropProject.GuideNodes.Any(node => node.IsOrphaned);

var reverseProject = await store.LoadAsync(paths[0]);
var reverseTrack = reverseProject.MainGuideTrack!;
var length = reverseTrack.GetTotalLength();
var guideBefore = reverseProject.GuideNodes.OrderBy(node => node.DistanceFromStart).ToArray();
var beforeGuide = guideBefore.ToDictionary(node => node.Id,
    node => (Distance: node.DistanceFromStart!.Value, Coordinate: node.SnappedCoordinate));
var segmentIds = reverseProject.GuideSegments.Select(segment => segment.Id).ToArray();
TrackEditPlan? reverse = null;
Measure(timings, "整轨反转准备毫秒", () => reverse = editor.PrepareReverseTrack(reverseProject, reverseTrack.Id));
Measure(timings, "整轨反转提交毫秒", () => reverse!.Apply());
var reverseRows = reverseProject.GuideNodes.OrderBy(node => node.DistanceFromStart)
    .Select(node => new { 节点 = node.Name ?? "未命名节点",
        旧里程公里 = beforeGuide[node.Id].Distance / 1000,
        新里程公里 = node.DistanceFromStart!.Value / 1000,
        空间位置保持 = node.SnappedCoordinate == beforeGuide[node.Id].Coordinate }).ToArray();
checks["九节点反转里程映射"] = reverseRows.Length == 9 &&
    reverseProject.GuideNodes.All(node => Math.Abs(node.DistanceFromStart!.Value -
        (length - beforeGuide[node.Id].Distance)) < 0.1 &&
        node.SnappedCoordinate == beforeGuide[node.Id].Coordinate);
checks["反转分段方向安全归档"] = reverseProject.RetiredGuideSegments.Count(segment =>
    segmentIds.Contains(segment.Id)) == 8 &&
    reverseProject.GuideSegments.All(segment => !segmentIds.Contains(segment.Id));
reverse!.Undo();
checks["反转撤销恢复八分段"] = reverseProject.GuideSegments.Select(segment => segment.Id)
    .SequenceEqual(segmentIds) && reverseProject.GuideNodes.All(node =>
    Math.Abs(node.DistanceFromStart!.Value - beforeGuide[node.Id].Distance) < 0.1);

var a = await store.LoadAsync(paths[1]);
var b = await store.LoadAsync(paths[2]);
var trackA = a.Tracks.FirstOrDefault(track => track.GetPointCount() == 17732)
    ?? a.Tracks.OrderByDescending(track => track.GetPointCount()).First();
var trackB = b.Tracks.FirstOrDefault(track => track.GetPointCount() == 30737)
    ?? b.Tracks.OrderByDescending(track => track.GetPointCount()).First();
var combined = new HikingProject { Name = "P6多轨迹验收副本" };
combined.AddTrack(trackA, false);
combined.AddTrack(trackB, false);
var source = new[] { new TrackMergeSource(trackA.Id, false),
    new TrackMergeSource(trackB.Id, false) };
TrackMergePlan? merged = null;
Measure(timings, "17732加30737合并准备毫秒", () => merged = new TrackMergeService().Prepare(
    combined, source, "合并轨迹", false));
merged!.Apply();
checks["多轨迹合并无隐式连接"] = combined.Tracks.Count == 3 &&
    merged.NewTrack.GetPointCount() == trackA.GetPointCount() + trackB.GetPointCount() &&
    merged.NewTrack.Segments.Count == 2 && combined.MainGuideTrackId is null;
merged.Undo();
var connected = new TrackMergeService().Prepare(combined, source, "连接轨迹", true);
connected.Apply();
checks["多轨迹连接显式Connector"] = connected.Connectors.Count == 1 &&
    connected.NewTrack.Segments.Count == 3 &&
    connected.NewTrack.Segments[1].Name == "人工直线连接";
var connectorLengths = connected.Connectors.Select(item => item.LengthMeters).ToArray();

var editedCopy = Path.Combine(output, "P6测试项目.hikeproj");
Measure(timings, "保存编辑项目毫秒", () => store.SaveAsync(edited, editedCopy, false).GetAwaiter().GetResult());
edited = null!;
var reopened = await store.LoadAsync(editedCopy);
checks["保存重开几何和Revision"] = reopened.MainGuideTrack!.GeometryRevision == track.GeometryRevision &&
    snap.GetGeometryFingerprint(reopened.MainGuideTrack) == snap.GetGeometryFingerprint(track) &&
    reopened.MainGuideTrack.GetPointCount() == 125910 &&
    reopened.GuideNodes.Count == 9 && reopened.GuideSegments.Count == 8;
var selection = new MergedExportSelection();
selection.TrackIds.Add(reopened.MainGuideTrack!.Id);
var exports = new Dictionary<string, object>();
foreach (var format in new[] { TrackFileFormat.Gpx, TrackFileFormat.Kml, TrackFileFormat.Kmz })
{
    var path = Path.Combine(output, "P6编辑后轨迹." + format.ToString().ToLowerInvariant());
    var exportTimer = Stopwatch.StartNew();
    await new ProjectMergedExportService().ExportAsync(reopened, selection, format, path, false);
    exportTimer.Stop();
    var parsed = format switch
    {
        TrackFileFormat.Gpx => await new GpxTrackImporter().ImportAsync(path),
        TrackFileFormat.Kml => await new KmlTrackImporter().ImportAsync(path),
        _ => await new KmzTrackImporter().ImportAsync(path)
    };
    checks[$"{format}导出重导编辑后几何"] = parsed.Tracks.Count == 1 &&
        parsed.Tracks[0].GetPointCount() == 125910 &&
        parsed.Tracks[0].Segments[0].Points[1000].Coordinate ==
        reopened.MainGuideTrack.Segments[0].Points[1000].Coordinate;
    exports[format.ToString()] = new { 文件 = path, 大小字节 = new FileInfo(path).Length,
        导出毫秒 = exportTimer.Elapsed.TotalMilliseconds, 重导点数 = parsed.TrackPointCount };
}
checks["输入项目SHA256未变化"] = paths.All(path =>
    sourceHashes[path] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
memory["验收结束字节"] = GC.GetTotalMemory(true);
var report = new
{
    成功 = checks.Values.All(value => value),
    输入文件 = paths,
    输入SHA256 = sourceHashes,
    项目版本 = reopened.ProjectVersion,
    基线指纹 = baseline,
    检查 = checks,
    三次移动 = moveEntries,
    删除范围节点 = new { 删除前 = victimBefore, 删除后 = victimAfter,
        撤销后恢复 = victimUndone },
    反转九节点 = reverseRows,
    Connector长度米 = connectorLengths,
    性能毫秒 = timings,
    内存字节 = memory,
    导出 = exports,
    编辑项目 = editedCopy
};
var reportPath = Path.Combine(output, "P6真实项目验收.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine(reportPath);
return report.成功 ? 0 : 1;
