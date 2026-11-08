using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法：HikingGuide.P5Verifier <真实KML文件> <空输出目录>");
    return 2;
}
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!File.Exists(source)) { Console.Error.WriteLine("找不到 KML 文件。"); return 3; }
Directory.CreateDirectory(output);
var reportFile = Path.Combine(output, "P5真实轨迹验证.json");
var projectFile = Path.Combine(output, "环船百大纵走_P5攻略节点.hikeproj");
if (File.Exists(reportFile) || File.Exists(projectFile)) { Console.Error.WriteLine("输出文件已存在，请使用空目录。"); return 4; }

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
static string GeometryHash(Track track)
{
    var snap = new TrackSnapService();
    return snap.GetGeometryFingerprint(track);
}
var sourceHashBefore = Hash(await File.ReadAllBytesAsync(source));
var parseTimer = Stopwatch.StartNew();
var parsed = await new KmlTrackImporter().ImportAsync(source);
foreach (var importedTrack in parsed.Tracks) importedTrack.RecalculateDistances();
parseTimer.Stop();
if (!parsed.IsSuccess || parsed.Tracks.Count != 1 || parsed.Waypoints.Count != 54)
    throw new InvalidOperationException("真实 KML 的解析结果不符合预期。");
var track = parsed.Tracks[0];
var originalHash = GeometryHash(track);
var memoryBefore = GC.GetTotalMemory(true);
var snapper = new TrackSnapService();
var buildTimer = Stopwatch.StartNew();
_ = snapper.Snap(track, parsed.Waypoints[0].Coordinate);
buildTimer.Stop();
var samples = new List<double>();
var offsets = new List<double>();
foreach (var waypoint in parsed.Waypoints)
{
    var timer = Stopwatch.StartNew();
    var snap = snapper.Snap(track, waypoint.Coordinate);
    timer.Stop();
    samples.Add(timer.Elapsed.TotalMilliseconds);
    offsets.Add(snap.Position!.DistanceToTrack);
}
var memoryAfter = GC.GetTotalMemory(true);
snapper.Clear();
var memoryAfterRelease = GC.GetTotalMemory(true);
var sortedSamples = samples.Order().ToArray();
var sortedOffsets = offsets.Order().ToArray();
var requiredNames = new[] { "起点：后山村", "帽子峰", "后山顶", "梅花顶", "黄思脑", "第一登船底顶", "联山农庄民宿", "上天堂山顶", "第二登船底顶" };
var project = new HikingProject { Name = "环船百大纵走 P5 验证" };
var session = ProjectImportSession.Create(parsed, source, project);
foreach (var choice in session.WaypointSelections)
{
    if (!requiredNames.Contains(choice.Waypoint.Name)) continue;
    choice.IsGuideNode = true;
    choice.Waypoint.Type = choice.Waypoint.Name switch
    {
        "起点：后山村" => WaypointType.Start,
        "联山农庄民宿" => WaypointType.Accommodation,
        _ => WaypointType.Summit
    };
    choice.Waypoint.ManualData = new ManualGuideData { Notes = "P5 验证人工内容" };
}
var excludedIds = session.WaypointSelections.Where(choice => !choice.IsGuideNode).Take(3).Select(choice => choice.Waypoint.Id).ToHashSet();
var excludedNames = session.WaypointSelections.Where(choice => excludedIds.Contains(choice.Waypoint.Id)).Select(choice => choice.Waypoint.Name).ToArray();
foreach (var choice in session.WaypointSelections.Where(choice => excludedIds.Contains(choice.Waypoint.Id)))
    choice.Import = false;
if (session.WaypointSelections.Count(s => s.IsGuideNode) != requiredNames.Length)
    throw new InvalidOperationException("真实 KML 缺少指定的攻略节点名称。");
var importService = new ProjectImportService();
var prepareTimer = Stopwatch.StartNew();
var plan = importService.Prepare(session, project);
prepareTimer.Stop();
importService.Apply(plan, project, true);
var firstSegment = project.GuideSegments.FirstOrDefault();
if (firstSegment is not null) firstSegment.ManualData = new SegmentManualData { RoadCondition = "P5 验证路况" };
var fileService = new ProjectFileService();
var saveTimer = Stopwatch.StartNew();
await fileService.SaveAsync(project, projectFile, false);
saveTimer.Stop();
var reopened = await fileService.LoadAsync(projectFile);
var orderedNodes = reopened.GuideNodes.OrderBy(node => node.ExactPosition!.DistanceFromTrackStart).ToArray();
var guideGeometry = new GuideStructureService();
var shortTimer = Stopwatch.StartNew();
var shortPieces = guideGeometry.ExtractRange(reopened.Tracks.Single(), orderedNodes[0].ExactPosition!, orderedNodes[1].ExactPosition!);
shortTimer.Stop();
var longTimer = Stopwatch.StartNew();
var longPieces = guideGeometry.ExtractRange(reopened.Tracks.Single(), orderedNodes[0].ExactPosition!, orderedNodes[^1].ExactPosition!);
longTimer.Stop();
var sourceHashAfter = Hash(await File.ReadAllBytesAsync(source));
var savedHash = GeometryHash(reopened.Tracks.Single());
var snapRows = reopened.GuideNodes.OrderBy(n => n.ExactPosition?.DistanceFromTrackStart).Select((node, i) => new
{
    顺序 = i + 1,
    名称 = node.Name,
    类型 = node.Type.ToString(),
    原始坐标 = new { 纬度 = node.OriginalCoordinate.Latitude, 经度 = node.OriginalCoordinate.Longitude },
    吸附坐标 = new { 纬度 = node.SnappedCoordinate?.Latitude, 经度 = node.SnappedCoordinate?.Longitude },
    距轨迹米 = node.DistanceToTrack,
    累计公里 = node.DistanceFromStart / 1000,
    轨迹段 = node.ExactPosition?.SegmentIndex,
    线段起点索引 = node.ExactPosition?.StartPointIndex,
    线段比例 = node.ExactPosition?.Fraction
}).ToArray();
var report = new
{
    输入文件 = source,
    输入SHA256 = sourceHashBefore,
    输入未改写 = sourceHashBefore == sourceHashAfter,
    轨迹数量 = parsed.Tracks.Count,
    轨迹段数量 = track.Segments.Count,
    原始点数 = track.GetPointCount(),
    标注点数量 = parsed.Waypoints.Count,
    导入攻略节点 = reopened.GuideNodes.Count,
    导入普通标注 = reopened.Waypoints.Count,
    未导入标注 = excludedNames.Length,
    未导入名称 = excludedNames,
    攻略分段 = reopened.GuideSegments.Count,
    原始几何未变 = originalHash == savedHash,
    人工节点备注保持 = reopened.GuideNodes.All(n => n.ManualData?.Notes == "P5 验证人工内容"),
    人工分段路况保持 = firstSegment is null || reopened.GuideSegments.Any(s => s.Id == firstSegment.Id && s.ManualData?.RoadCondition == "P5 验证路况"),
    耗时毫秒 = new { KML解析 = parseTimer.Elapsed.TotalMilliseconds, 索引构建及首次吸附 = buildTimer.Elapsed.TotalMilliseconds,
        全部54点吸附 = samples.Sum(), 单点平均 = samples.Average(), 单点P95 = sortedSamples[(int)Math.Ceiling(sortedSamples.Length * .95) - 1], 单点最大 = sortedSamples[^1],
        导入准备 = prepareTimer.Elapsed.TotalMilliseconds, 保存项目 = saveTimer.Elapsed.TotalMilliseconds,
        短分段提取 = shortTimer.Elapsed.TotalMilliseconds, 长分段提取 = longTimer.Elapsed.TotalMilliseconds,
        短分段点数 = shortPieces.Sum(piece => piece.Count), 长分段点数 = longPieces.Sum(piece => piece.Count) },
    内存字节 = new { 索引前 = memoryBefore, 索引后 = memoryAfter, 索引释放后 = memoryAfterRelease },
    全部54点距轨迹米 = new { 最小 = sortedOffsets[0], 中位数 = sortedOffsets[sortedOffsets.Length / 2], 最大 = sortedOffsets[^1],
        不超过5 = offsets.Count(d => d <= 5), 不超过10 = offsets.Count(d => d <= 10), 不超过25 = offsets.Count(d => d <= 25),
        不超过50 = offsets.Count(d => d <= 50), 超过50 = offsets.Count(d => d > 50) },
    攻略节点 = snapRows,
    输出项目 = projectFile
};
await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions
{ WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8);
Console.WriteLine($"P5 真实核心验证已完成：{reportFile}");
return 0;
