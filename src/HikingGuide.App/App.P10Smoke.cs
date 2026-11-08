using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Guides;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunP10SmokeAsync(MainWindow window, MainViewModel main, string output)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!; Directory.CreateDirectory(folder);
        var checks = new Dictionary<string, bool>(); string? error = null; object? statistics = null;
        try
        {
            if (!Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("实体验收必须隔离用户收藏库和项目。");
            var source = @"F:\Hiking Guide\测试文件\环船百大纵走规划攻略.kml";
            var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var imported = await new KmlTrackImporter().ImportAsync(source); var track = imported.Tracks.Single();
            track.RecalculateDistances(); var project = new HikingProject { Name = "P10环船百大验收副本（示例计划）" }; project.AddTrack(track); project.MainGuideTrackId = track.Id;
            var structure = new GuideStructureService();
            foreach (var waypoint in imported.Waypoints)
            { var node = new GuideNode { Id = waypoint.Id, Name = waypoint.Name, OriginalCoordinate = waypoint.Coordinate, TrackId = track.Id }; structure.SnapNode(node, track); project.GuideNodes.Add(node); }
            if (project.GuideNodes.Count < 2) throw new InvalidOperationException("真实轨迹的攻略标注点不足。");
            GuideStructureService.ReplaceSegments(project, structure.RebuildSegments(project));
            project.GuideData.Settings = project.GuideData.Settings with { ElevationSourceKind = GuideElevationSourceKind.Gps };
            var fileService = new ProjectFileService(); var projectPath = Path.Combine(folder, "环船百大-P10.hikeproj"); await fileService.SaveAsync(project, projectPath, true);
            window.Width = 1550; window.Height = 1000; window.Activate();
            await main.OpenForAutomationAsync(projectPath); project = main.MapContextProject!; window.GuideWorkbench.SelectProject(project);
            await window.GuideWorkbench.CalculateAsync();
            var workbench = window.GuideWorkbench; var nodes = GuideDataReconciler.OrderedNodes(project).ToArray();
            checks["真实轨迹节点完整"] = workbench.Table.Items.Count == nodes.Length && nodes.Length > 20;
            checks["默认两方案及各自抵达列"] = workbench.Table.Columns.Count(c => c.SortMemberPath.StartsWith("Plan:")) == 2 && workbench.Table.Columns.Count(c => c.SortMemberPath.StartsWith("Arrival:")) == 2;
            checks["身份前三列冻结"] = workbench.Table.FrozenColumnCount == 3;
            checks["首行计划不可编辑"] = !await workbench.EditCellForAutomationAsync(0, "PlannedDuration", "1");
            checks["实体计划用时提交"] = await workbench.EditCellForAutomationAsync(1, "PlannedDuration", "1.25");
            await Task.Delay(100);
            checks["实体实际时间提交"] = await workbench.EditCellForAutomationAsync(1, "ActualArrival", "07:45");
            await Task.Delay(100);
            checks["实体路况提交"] = await workbench.EditCellForAutomationAsync(1, "RoadCondition", "测试石阶\n第二行，注意防滑");
            var gpsResult = project.GuideData.Derived!; var revision = gpsResult.ResultRevision;
            workbench.SetSpeedsForAutomation("4", "3", "2.2"); await Task.Delay(100);
            checks["速度仅重算时间"] = project.GuideData.Derived!.ResultRevision == revision && project.GuideData.Settings.FlatDownSpeedKmh == 4;
            foreach (var (plan, planIndex) in project.GuideData.Plans.Select((p, i) => (p, i)))
            { var boundary = Math.Min(10 + planIndex * 2, nodes.Length - 2); var secondBoundary = Math.Min(20 + planIndex * 4, nodes.Length - 2); workbench.ToggleBoundary(plan.PlanId, nodes[boundary].Id); workbench.ToggleBoundary(plan.PlanId, nodes[secondBoundary].Id); plan.DepartureTimes[nodes[0].Id] = new(23, 30); plan.DepartureTimes[nodes[boundary].Id] = new(7, 0); plan.DepartureTimes[nodes[secondBoundary].Id] = new(6, 30); }
            workbench.RefreshForAutomation(); checks["实体每日出发时间提交"] = await workbench.EditDepartureForAutomationAsync(0, "23:30"); await Task.Delay(100);
            var locationKey = "Location:" + project.GuideData.Settings.LocationConditions[0].DefinitionId;
            checks["实体地点下拉提交"] = await workbench.EditCellForAutomationAsync(1, locationKey, "✔"); await Task.Delay(100);
            for (var i = 1; i < nodes.Length; i++) { var key = new GuideSegmentKey(track.Id, nodes[i - 1].Id, nodes[i].Id); var old = project.GuideData.Manual.Segments.GetValueOrDefault(key.StorageKey) ?? new() { SegmentKey = key }; project.GuideData.Manual.Segments[key.StorageKey] = old with { PlannedDurationHours = i == 1 ? 1.25 : .5, RoadCondition = old.RoadCondition.Length > 0 ? old.RoadCondition : "验收示例路况" }; }
            var initial = GuideExportSnapshotBuilder.Build(project); checks["各方案独立日边界和午夜"] = initial.PlanTimings.Values.All(t => t.Days.Count == 3 && t.Arrivals[nodes[1].Id]?.DayOffset == 1);
            var dem = DemCatalog.Databases.First(d => d.Name.Contains("Fathom", StringComparison.OrdinalIgnoreCase));
            workbench.SetSourceForAutomation(dem.Id, false); await Task.Delay(50); await workbench.CalculateAsync();
            var demResult = project.GuideData.Derived!;
            checks["切换FathomDEM后台统计"] = project.GuideData.Settings.DemDatabaseId == dem.Id && demResult.ElevationSourceFingerprint == dem.Fingerprint && demResult.ResultRevision > revision && demResult.Nodes[^1].CumulativeAscentMeters is > 0 && demResult.CoverageRatio is > .99;
            var expected = await Task.Run(async () => await DemProvider!.SampleAsync(dem.Id, nodes[1].ExactPosition!.SnappedCoordinate));
            checks["节点使用精确吸附坐标DEM"] = expected.HasValue && demResult.Nodes[1].ElevationMeters == expected;
            var sources = new List<object> { new { Name = "GPS原始高程", Elevation = gpsResult.Nodes[1].ElevationMeters, Ascent = gpsResult.Nodes[^1].CumulativeAscentMeters }, new { Name = dem.Name, Elevation = demResult.Nodes[1].ElevationMeters, Ascent = demResult.Nodes[^1].CumulativeAscentMeters } };
            foreach (var other in DemCatalog.Databases.Where(d => d.Id != dem.Id))
            { workbench.SetSourceForAutomation(other.Id, false); await Task.Delay(50); await workbench.CalculateAsync(); var actual = project.GuideData.Derived!; var sampled = await Task.Run(async () => await DemProvider!.SampleAsync(other.Id, nodes[1].ExactPosition!.SnappedCoordinate)); checks["高程源切换:" + other.Name] = sampled.HasValue && actual.Nodes[^1].CumulativeAscentMeters is > 0 && actual.ElevationSourceFingerprint == other.Fingerprint && actual.Nodes[1].ElevationMeters == sampled; sources.Add(new { Name = other.Name, Elevation = actual.Nodes[1].ElevationMeters, Ascent = actual.Nodes[^1].CumulativeAscentMeters }); }
            workbench.SetSourceForAutomation(dem.Id, false); await Task.Delay(50); await workbench.CalculateAsync();
            var manualKey = new GuideSegmentKey(track.Id, nodes[0].Id, nodes[1].Id).StorageKey;
            checks["重分析不覆盖人工内容"] = project.GuideData.Manual.Segments[manualKey].PlannedDurationHours == 1.25 && project.GuideData.Manual.Segments[manualKey].RoadCondition.StartsWith("测试石阶") && project.GuideData.Manual.Nodes[nodes[1].Id].ActualArrival == new TimeOnly(7, 45);
            var snapshot2 = GuideExportSnapshotBuilder.Build(project); var template = Path.Combine(AppContext.BaseDirectory, "Templates", "攻略数据模板.xlsx"); var exporter = new ExcelGuideExporter();
            await exporter.ExportAsync(template, Path.Combine(folder, "环船百大-2方案.xlsx"), snapshot2, true);
            project.UpdateGuideData(d => { d.Plans.Add(new() { Order = 2, DailyStartNodeIds = [nodes[0].Id, nodes[Math.Min(15, nodes.Length - 2)].Id, nodes[Math.Min(30, nodes.Length - 2)].Id], DepartureTimes = new() { [nodes[0].Id] = new(8, 0), [nodes[Math.Min(15, nodes.Length - 2)].Id] = new(6, 30), [nodes[Math.Min(30, nodes.Length - 2)].Id] = new(7, 0) } }); GuideDataReconciler.AddCondition(d.Settings, "狗出没"); GuideDataReconciler.AddCondition(d.Settings, "摄影点"); });
            workbench.RefreshForAutomation(); var snapshot3 = GuideExportSnapshotBuilder.Build(project); await exporter.ExportAsync(template, Path.Combine(folder, "环船百大-3方案6地点.xlsx"), snapshot3, true);
            checks["第三方案动态列和六地点"] = workbench.Table.Columns.Count(c => c.SortMemberPath.StartsWith("Arrival:")) == 3 && snapshot3.Columns.Count(c => c.Location) == 6;
            await fileService.SaveAsync(project, projectPath, true); var loaded = await fileService.LoadAsync(projectPath);
            checks["重开保留三方案及人工数据"] = loaded.GuideData.Plans.Count == 3 && loaded.GuideData.Manual.Segments[manualKey].PlannedDurationHours == 1.25 && loaded.GuideData.Settings.DemDatabaseId == dem.Id && loaded.GuideData.Derived!.Nodes.Count == nodes.Length;
            var second = new HikingProject { Name = "P10第二项目隔离验收" }; var secondTrack = new Track { Name = "独立轨迹", Segments = [new() { Points = [new() { Latitude = 23, Longitude = 113 }, new() { Latitude = 23.001, Longitude = 113 }] }] }; secondTrack.RecalculateDistances(); second.AddTrack(secondTrack); var secondPath = Path.Combine(folder, "第二项目.hikeproj"); await fileService.SaveAsync(second, secondPath, true);
            await main.OpenForAutomationAsync(secondPath); checks["切项目不串设置"] = main.MapContextProject!.GuideData.Settings.FlatDownSpeedKmh is null && main.MapContextProject.GuideData.Plans.Count == 2;
            await main.OpenForAutomationAsync(projectPath); workbench.RefreshForAutomation(); checks["切回恢复内容"] = main.MapContextProject!.GuideData.Plans.Count == 3 && workbench.Table.Items.Count == nodes.Length;
            var afterBytes = await File.ReadAllBytesAsync(source);
            checks["原始KML字节不变"] = before.SequenceEqual(SHA256.HashData(afterBytes));
            await main.WaitForMapRefreshForAutomationAsync();
            await workbench.CalculateAsync(); workbench.Table.ScrollIntoView(workbench.Table.Items[4], workbench.Table.Columns[0]);
            CaptureWindowPng(window, Path.ChangeExtension(output, ".png")); await main.Map.CapturePreviewAsync(Path.ChangeExtension(output, ".map.png"));
            statistics = new { TrackPoints = track.GetPointCount(), Nodes = nodes.Length, DistanceKm = track.GetTotalLength() / 1000, Gps = gpsResult.Nodes[^1], Dem = demResult.Nodes[^1], Sources = sources,
                Segments = snapshot3.Rows.Skip(3).Take(3), PlanDays = snapshot3.PlanTimings.Values.Select(t => new { t.PlanId, t.Days }), SampleSettings = project.GuideData.Settings, EditDiagnostic = workbench.LastAutomationEditError, ExportRows = snapshot3.Rows.Count };
            await main.FlushGuideAutoSaveAsync();
        }
        catch (Exception exception) { error = exception.ToString(); }
        var success = error is null && checks.Values.All(c => c);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Success = success, Checks = checks, Error = error, Statistics = statistics }, new JsonSerializerOptions { WriteIndented = true }));
        return success;
    }
}
