using HikingGuide.Application.Guides;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.Logging;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;

if (args.Length > 1 && args[0] == "--import-batch")
{
    using var logger = new SilentLogger();
    var trackFiles = new TrackFileService(
        [new GpxTrackImporter(), new KmlTrackImporter(), new KmzTrackImporter()],
        [new GpxTrackExporter(), new KmlTrackExporter()], logger);
    var project = new HikingProject { Name = "真实文件只读导入验证" };
    var result = await new ProjectImportBatch(trackFiles).ReadAsync(args.Skip(1).ToArray(), project);
    if (!result.IsSuccess)
    {
        foreach (var error in result.Errors) Console.WriteLine($"错误：{error.Message}");
        return 1;
    }
    foreach (var track in result.Tracks)
        Console.WriteLine($"轨迹：{track.Name}；原始文件：{Path.GetFileName(track.SourceFile)}；颜色：{track.DisplayColor}；标注：{result.Waypoints.Count(point => point.AssociatedTrackId == track.Id)}；点数：{track.GetPointCount()}");
    Console.WriteLine($"未关联标注：{result.Waypoints.Count(point => point.AssociatedTrackId is null)}");
    var session = ProjectImportSession.Create(result, "真实文件", project);
    new ProjectImportService().Commit(session, project, true);
    Console.WriteLine($"项目内：轨迹 {project.Tracks.Count}；标注 {project.Waypoints.Count}；未关联 {project.Waypoints.Count(point => point.AssociatedTrackId is null)}");
    return 0;
}

if (args.Length == 0)
{
    Console.WriteLine("用法：HikingGuide.P52Verifier <项目1.hikeproj> [项目2.hikeproj ...]");
    return 1;
}

var loader = new ProjectFileService();
var loaded = new List<HikingGuide.Core.Models.HikingProject>();
foreach (var path in args)
{
    Console.WriteLine($"项目文件：{Path.GetFileName(path)}");
    var project = await loader.LoadAsync(path);
    loaded.Add(project);
    Console.WriteLine($"项目：{project.Name}；轨迹 {project.Tracks.Count}；普通标注 {project.Waypoints.Count}；攻略节点 {project.GuideNodes.Count}；分段 {project.GuideSegments.Count}");
    Console.WriteLine($"主攻略轨迹：{project.MainGuideTrack?.Name ?? "无"}");
    foreach (var track in project.Tracks)
    {
        var ordinary = project.Waypoints.Count(point => point.AssociatedTrackId == track.Id);
        var guides = project.GuideNodes.Count(node => node.TrackId == track.Id && !node.IsOrphaned);
        Console.WriteLine($"  {(track.Id == project.MainGuideTrackId ? "★" : "·")} {track.Name ?? "（未命名轨迹）"}：{track.GetPointCount():N0} 点，普通标注 {ordinary}，攻略节点 {guides}");
    }
    Console.WriteLine($"未关联标注：{project.Waypoints.Count(point => point.AssociatedTrackId is null)}；未关联攻略节点：{project.GuideNodes.Count(node => node.IsOrphaned || project.MainGuideTrackId is null)}");
    if (project.Tracks.Count > 1)
    {
        var ordinary = project.Tracks.First(track => track.Id != project.MainGuideTrackId);
        var deletion = new TrackManagementService().PrepareDeletion(project, ordinary.Id);
        deletion.Execute();
        Console.WriteLine($"普通轨迹删除：{ordinary.Name}；未关联标注 {project.Waypoints.Count(point => point.AssociatedTrackId is null)}");
        deletion.Undo();
        Console.WriteLine($"撤销后轨迹恢复：{project.Tracks.Any(track => track.Id == ordinary.Id)}");
    }
    if (project.MainGuideTrack is { } main)
    {
        var deletion = new TrackManagementService().PrepareDeletion(project, main.Id);
        deletion.Execute();
        Console.WriteLine($"主轨迹删除：攻略节点 {project.GuideNodes.Count}；失联 {project.GuideNodes.Count(node => node.IsOrphaned)}；未关联攻略节点 {project.GuideNodes.Count(node => node.IsOrphaned || project.MainGuideTrackId is null)}");
        deletion.Undo();
        Console.WriteLine($"撤销后主轨迹恢复：{project.MainGuideTrackId == main.Id}");
    }
}
if (loaded.Count >= 3)
{
    var sample = loaded[0];
    var main = sample.Tracks.First();
    var ordinaryA = loaded[1].Tracks.First();
    var ordinaryB = loaded[2].Tracks.First();
    sample.AddTrack(ordinaryA); sample.AddTrack(ordinaryB);
    sample.MainGuideTrackId = main.Id;
    foreach (var waypoint in loaded[1].Waypoints.Take(2))
    {
        waypoint.AssociatedTrackId = ordinaryA.Id;
        sample.Waypoints.Add(waypoint);
    }
    var sourceMarkers = loaded[2].Waypoints.Take(5).ToArray();
    foreach (var waypoint in sourceMarkers)
    {
        waypoint.AssociatedTrackId = ordinaryB.Id;
        sample.Waypoints.Add(waypoint);
    }
    Console.WriteLine("--- 三个真实项目的内存合并模拟；不写入任何用户项目 ---");
    var guideService = new GuideStructureService();
    foreach (var waypoint in sourceMarkers.Take(3))
    {
        var node = waypoint.ToGuideNode();
        node.TrackId = main.Id;
        guideService.SnapNode(node, main);
        sample.Waypoints.Remove(waypoint);
        sample.GuideNodes.Add(node);
        Console.WriteLine($"转换：{waypoint.Name}；原树：{ordinaryB.Name}/普通标注；目标：{main.Name}/攻略节点；吸附距起点 {node.DistanceFromStart / 1000:F2} km，距轨迹 {node.DistanceToTrack:F1} m；ID保持 {node.Id == waypoint.Id}");
    }
    GuideStructureService.ReplaceSegments(sample, guideService.RebuildSegments(sample));
    Console.WriteLine($"真实样本树：★ {main.Name}（攻略节点 {sample.GuideNodes.Count}，普通标注 {sample.Waypoints.Count(point => point.AssociatedTrackId == main.Id)}）；{ordinaryA.Name}（普通标注 {sample.Waypoints.Count(point => point.AssociatedTrackId == ordinaryA.Id)}）；{ordinaryB.Name}（普通标注 {sample.Waypoints.Count(point => point.AssociatedTrackId == ordinaryB.Id)}）；未关联标注 {sample.Waypoints.Count(point => point.AssociatedTrackId is null)}");
    var ordinaryDeletion = new TrackManagementService().PrepareDeletion(sample, ordinaryB.Id);
    ordinaryDeletion.Execute();
    Console.WriteLine($"普通轨迹删除：原普通标注 {ordinaryDeletion.Impact.DirectWaypoints}；删除后未关联 {sample.Waypoints.Count(point => point.AssociatedTrackId is null)}");
    ordinaryDeletion.Undo();
    Console.WriteLine($"撤销普通轨迹删除：原轨迹归属恢复 {sample.Waypoints.Count(point => point.AssociatedTrackId == ordinaryB.Id)}");
    var mainDeletion = new TrackManagementService().PrepareDeletion(sample, main.Id);
    mainDeletion.Execute();
    Console.WriteLine($"主轨迹删除：攻略节点 {sample.GuideNodes.Count}；失联 {sample.GuideNodes.Count(node => node.IsOrphaned)}；未关联攻略节点 {sample.GuideNodes.Count(node => node.TrackId is null)}");
    mainDeletion.Undo();
    Console.WriteLine($"撤销主轨迹删除：主轨迹 {sample.MainGuideTrackId == main.Id}；有效攻略节点 {sample.GuideNodes.Count(node => !node.IsOrphaned && node.TrackId == main.Id)}");
}
return 0;

file sealed class SilentLogger : ILoggingService
{
    public void Debug(string message) { }
    public void Information(string message) { }
    public void Warning(string message) { }
    public void Error(Exception exception, string message) { }
    public void Dispose() { }
}
