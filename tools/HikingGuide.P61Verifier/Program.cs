using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using HikingGuide.Application.Maps;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;

if (args.Length != 2 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("用法：HikingGuide.P61Verifier <真实项目.hikeproj> <报告.json>");
    return 2;
}
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input)));
var timings = new Dictionary<string, double>();
var checks = new Dictionary<string, bool>();
var memory = new Dictionary<string, long>();
var workingSet = new Dictionary<string, long>();
var clock = Stopwatch.StartNew();
var project = await new ProjectFileService().LoadAsync(input);
clock.Stop(); timings["加载真实项目毫秒"] = clock.Elapsed.TotalMilliseconds;
memory["加载后托管字节"] = GC.GetTotalMemory(true);
workingSet["加载后工作集字节"] = Process.GetCurrentProcess().WorkingSet64;
var track = project.MainGuideTrack ?? throw new InvalidOperationException("项目缺少主攻略轨迹。");
var sourceLength = track.GetTotalLength();
var guides = project.GuideNodes.ToDictionary(node => node.Id, node => node.SnappedCoordinate);
var manual = project.GuideSegments.ToDictionary(segment => segment.Id, segment => segment.ManualData?.Description);
checks["真实125910点"] = track.GetPointCount() == 125910;
var arrowCache = new DirectionArrowCache();
DirectionArrow[]? arrows = null;
clock.Restart(); arrows = (DirectionArrow[])arrowCache.Get(track); clock.Stop();
timings["首次箭头采样毫秒"] = clock.Elapsed.TotalMilliseconds;
memory["箭头缓存后托管字节"] = GC.GetTotalMemory(true);
workingSet["箭头缓存后工作集字节"] = Process.GetCurrentProcess().WorkingSet64;
clock.Restart(); var cached = arrowCache.Get(track); clock.Stop();
timings["箭头缓存命中毫秒"] = clock.Elapsed.TotalMilliseconds;
checks["缓存命中同一数组"] = ReferenceEquals(arrows, cached);
var arrowsJson = new DirectionArrowGeoJsonBuilder(arrowCache).Build(project);
var arrowCount = arrows.Length;
var firstArrowSamples = arrows.Take(3).Select(arrow => new
{
    物理段 = arrow.SegmentId, 段内距离米 = arrow.DistanceFromPartStart,
    纬度 = arrow.Coordinate.Latitude, 经度 = arrow.Coordinate.Longitude,
    方位角度 = arrow.BearingDegrees
}).ToArray();
var arrowJsonBytes = System.Text.Encoding.UTF8.GetByteCount(arrowsJson);
checks["箭头数量符合当前间隔"] = arrows.Length >= track.GetTotalLength() / DirectionArrowCache.SpacingMeters - track.Segments.Count - 5 &&
    arrows.Length <= track.GetTotalLength() / DirectionArrowCache.SpacingMeters + 5;
arrows = null;
cached = null!;
arrowsJson = null!;
arrowCache.Clear();
memory["箭头缓存清除后托管字节"] = GC.GetTotalMemory(true);
workingSet["箭头缓存清除后工作集字节"] = Process.GetCurrentProcess().WorkingSet64;
var editor = new TrackEditService();
for (var index = 0; index < 3; index++)
{
    var longest = track.Segments.OrderByDescending(segment => segment.PointCount).First();
    var split = editor.PrepareSplitSegment(project, track.Id, longest.Id, longest.PointCount / 2, .5);
    split.Apply();
}
checks["拆分四个逻辑分段"] = track.GetSplitSections().Count == 4;
var sections = track.GetSplitSections();
var boundary = sections[0].EndPoint!;
var moved = editor.PrepareMovePoint(project, track.Id, boundary.Id,
    new GeoCoordinate(boundary.Latitude + .001, boundary.Longitude));
moved.Apply();
var gapped = track.GetSplitSections();
var gap = HikingGuide.Core.Services.GeoDistanceCalculator.Instance.CalculateDistance(
    gapped[0].EndPoint!.Coordinate, gapped[1].StartPoint!.Coordinate);
var lengthBeforeMerge = track.GetTotalLength();
var selected = new[] { gapped[0], gapped[2], gapped[3] };
clock.Restart(); var merge = editor.PrepareCombineSplitSections(project, track.Id,
    selected.Select(item => new SplitSectionSource(item.Id, false)).ToArray(), false); clock.Stop();
timings["三段Merge准备毫秒"] = clock.Elapsed.TotalMilliseconds;
clock.Restart(); merge.Apply(); clock.Stop(); timings["三段Merge提交毫秒"] = clock.Elapsed.TotalMilliseconds;
var merged = track.GetSplitSections().First(section => section.GeometryParts.Count == 3);
checks["Merge三个Part保留Gap"] = merged.GeometryParts.Count == 3 &&
    merged.GeometryParts[0].EndPoint!.Coordinate != merged.GeometryParts[1].StartPoint!.Coordinate &&
    Math.Abs(track.GetTotalLength() - lengthBeforeMerge) < .01;
checks["主攻略节点空间位置保持"] = project.GuideNodes.All(node => node.SnappedCoordinate == guides[node.Id]);
merge.Undo();
checks["Merge撤销恢复四段"] = track.GetSplitSections().Count == 4;
merge.Apply();
checks["Merge重做确定性"] = track.GetSplitSections().Any(section => section.Id == merged.Id);
merge.Undo();
var order = new[]
{
    new SplitSectionSource(selected[0].Id, false),
    new SplitSectionSource(selected[2].Id, true),
    new SplitSectionSource(selected[1].Id, false)
};
clock.Restart(); var connect = editor.PrepareCombineSplitSections(project, track.Id, order, true); clock.Stop();
timings["三段Connect准备毫秒"] = clock.Elapsed.TotalMilliseconds;
clock.Restart(); connect.Apply(); clock.Stop(); timings["三段Connect提交毫秒"] = clock.Elapsed.TotalMilliseconds;
var connected = track.GetSplitSections().First(section => section.GeometryParts.Count == 5);
var connectors = connected.GeometryParts.Where(part =>
    part.OriginalAttributes.GetValueOrDefault("hg:editorProvenance") == "人工直线连接").ToArray();
var connectorDetails = connectors.Select(part => new
{
    长度米 = part.Length,
    起点纬度 = part.StartPoint!.Latitude, 起点经度 = part.StartPoint.Longitude,
    终点纬度 = part.EndPoint!.Latitude, 终点经度 = part.EndPoint.Longitude
}).ToArray();
checks["Connect两条正式Connector"] = connectors.Length == 2 &&
    connectors.All(part => part.Points.All(point => point.IsInsertedByEditor &&
        point.SourceElevation is null && point.Timestamp is null));
checks["Connect长度包含Connector"] = Math.Abs(track.GetTotalLength() -
    lengthBeforeMerge - connectors.Sum(part => part.Length)) < .01;
checks["Connect主攻略节点空间位置保持"] = project.GuideNodes.All(node => node.SnappedCoordinate == guides[node.Id]);
connect.Undo();
checks["Connect撤销删除Connector"] = track.GetSplitSections().Count == 4 &&
    track.Segments.All(part => part.OriginalAttributes.GetValueOrDefault("hg:editorProvenance") != "人工直线连接");
var testProject = Path.ChangeExtension(output, ".hikeproj");
connect.Apply();
await new ProjectFileService().SaveAsync(project, testProject, false);
var reopened = await new ProjectFileService().LoadAsync(testProject);
checks["保存重开逻辑段与Connector"] = reopened.MainGuideTrack?.GetSplitSections().Count == 2 &&
    reopened.MainGuideTrack.GetSplitSections().Any(section => section.GeometryParts.Count == 5);
checks["人工攻略数据存在"] = manual.All(pair =>
    project.GuideSegments.Concat(project.RetiredGuideSegments).Any(segment =>
        segment.Id == pair.Key && segment.ManualData?.Description == pair.Value));
checks["输入项目未改变"] = originalHash == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input)));
var stressProject = await new ProjectFileService().LoadAsync(input);
var stressTrack = stressProject.MainGuideTrack!;
clock.Restart();
while (stressTrack.GetSplitSections().Count < 20)
{
    var longest = stressTrack.Segments.OrderByDescending(segment => segment.PointCount).First();
    editor.PrepareSplitSegment(stressProject, stressTrack.Id, longest.Id,
        longest.PointCount / 2, .5).Apply();
}
clock.Stop(); timings["构造20拆分分段毫秒"] = clock.Elapsed.TotalMilliseconds;
var twentySources = stressTrack.GetSplitSections()
    .Select(section => new SplitSectionSource(section.Id, false)).ToArray();
checks["真实轨迹20拆分分段"] = twentySources.Length == 20;
var stressLength = stressTrack.GetTotalLength();
clock.Restart(); var stressMerge = editor.PrepareCombineSplitSections(stressProject,
    stressTrack.Id, twentySources, false); clock.Stop();
timings["20段Merge准备毫秒"] = clock.Elapsed.TotalMilliseconds;
clock.Restart(); stressMerge.Apply(); clock.Stop(); timings["20段Merge提交毫秒"] = clock.Elapsed.TotalMilliseconds;
checks["20段Merge不增添间隙"] = stressTrack.GetSplitSections().Count == 1 &&
    Math.Abs(stressTrack.GetTotalLength() - stressLength) < .01;
stressMerge.Undo();
clock.Restart(); var stressConnect = editor.PrepareCombineSplitSections(stressProject,
    stressTrack.Id, twentySources, true); clock.Stop();
timings["20段Connect准备毫秒"] = clock.Elapsed.TotalMilliseconds;
clock.Restart(); stressConnect.Apply(); clock.Stop(); timings["20段Connect提交毫秒"] = clock.Elapsed.TotalMilliseconds;
checks["20段Connect含19条正式连接"] = stressTrack.GetSplitSections().Count == 1 &&
    stressTrack.Segments.Count(part =>
        part.OriginalAttributes.GetValueOrDefault("hg:editorProvenance") == "人工直线连接") == 19;
stressConnect.Undo();
checks["20段Connect撤销恢复"] = stressTrack.GetSplitSections().Count == 20 &&
    Math.Abs(stressTrack.GetTotalLength() - stressLength) < .01;
workingSet["20段编辑后工作集字节"] = Process.GetCurrentProcess().WorkingSet64;
checks["输入项目经压力测试仍未改变"] = originalHash ==
    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input)));
var report = new
{
    成功 = checks.Values.All(value => value), 输入项目 = input, 轨迹点 = 125910,
    原始轨迹长度米 = sourceLength, 编辑后轨迹长度米 = track.GetTotalLength(), 原Gap米 = gap,
    箭头数量 = arrowCount, 箭头GeoJson字节 = arrowJsonBytes,
    箭头首样本 = firstArrowSamples,
    人工连接 = connectorDetails, 检查 = checks, 耗时 = timings,
    托管内存 = memory, 工作集 = workingSet, 测试副本 = testProject
};
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine(JsonSerializer.Serialize(new { report.成功, report.轨迹点, report.箭头数量, 报告 = output }));
return report.成功 ? 0 : 1;
