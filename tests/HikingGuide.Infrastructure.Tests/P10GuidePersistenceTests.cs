using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Guides;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;

public sealed class P10GuidePersistenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(@"F:\Codex\Temp", "HikingGuide.P10.Tests", Guid.NewGuid().ToString("N"));
    public P10GuidePersistenceTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);
    private string Output(string name) => Path.Combine(_folder, name);

    [Fact] public async Task P101StartRoadAndUnlimitedDayNotesSurviveRestartAndReconcile()
    {
        var project = new HikingProject(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var text = "多行备注\n" + new string('注', 70000) + "🏕结束";
        project.GuideData.Manual.Nodes[a] = new() { GuideNodeId = a, StartRoadCondition = "首行路况\n保留" };
        var plan = project.GuideData.Plans[0]; plan.DailyStartNodeIds.Add(a); plan.DailyNotes[a] = text; plan.DepartureTimes[a] = new(6, 37);
        var path = Output("P101备注.hikeproj"); var service = new ProjectFileService(); await service.SaveAsync(project, path, true);
        var loaded = await service.LoadAsync(path);
        Assert.Equal(text, loaded.GuideData.Plans[0].DailyNotes[a]); Assert.Equal(new TimeOnly(6, 37), loaded.GuideData.Plans[0].DepartureTimes[a]);
        GuideDataReconciler.Reconcile(loaded.GuideData, Guid.NewGuid(), [b]);
        Assert.Equal(text, loaded.GuideData.Plans[0].DailyNotes[a]); Assert.Equal("首行路况\n保留", loaded.GuideData.Manual.Nodes[a].StartRoadCondition);
    }

    [Fact] public async Task ModularProjectRoundTripKeepsManualPlansSourcesConditionsAndDerivedSnapshot()
    {
        var project = new HikingProject { Name = "P10持久化测试" }; var track = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid(); var key = new GuideSegmentKey(track, a, b);
        var data = project.GuideData; data.Settings = data.Settings with { DemDatabaseId = Guid.NewGuid(), FlatDownSpeedKmh = 4, GentleUpSpeedKmh = 3, SteepUpSpeedKmh = 2 };
        var custom = GuideDataReconciler.AddCondition(data.Settings, "狗出没"); data.Manual.Nodes[a] = new() { GuideNodeId = a, ActualArrival = new(17, 45), LocationConditions = new() { [custom.DefinitionId] = LocationConditionValue.Unknown } };
        data.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, PlannedDurationHours = 1.5, RoadCondition = "多行中文\n" + new string('路', 40000) };
        data.Plans.Add(new() { Order = 2, DailyStartNodeIds = [a], DepartureTimes = new() { [a] = new(23, 30) } });
        data.Derived = new("input", "geometry", "source", "20260930-v1", 7, DateTimeOffset.UtcNow,
            [new(a, 0, 10, 0, 0, null), new(b, 1000, 100, 90, 0, new(key, 1000, 90, 0, 1000, 0, 0, 750, 250, 0, true))]);
        var path = Output("攻略项目.hikeproj"); var service = new ProjectFileService(); await service.SaveAsync(project, path, true); var loaded = await service.LoadAsync(path);
        Assert.Equal(3, loaded.GuideData.Plans.Count); Assert.Equal(data.Plans[2].PlanId, loaded.GuideData.Plans[2].PlanId); Assert.Equal(new TimeOnly(23, 30), loaded.GuideData.Plans[2].DepartureTimes[a]);
        Assert.Equal(data.Settings.DemDatabaseId, loaded.GuideData.Settings.DemDatabaseId); Assert.Equal(4, loaded.GuideData.Settings.FlatDownSpeedKmh);
        Assert.Equal(data.Manual.Segments[key.StorageKey], loaded.GuideData.Manual.Segments[key.StorageKey]); Assert.Equal(new TimeOnly(17, 45), loaded.GuideData.Manual.Nodes[a].ActualArrival);
        Assert.Equal(LocationConditionValue.Unknown, loaded.GuideData.Manual.Nodes[a].LocationConditions[custom.DefinitionId]); Assert.Equal(7, loaded.GuideData.Derived!.ResultRevision);
        Assert.Equal(250, loaded.GuideData.Derived.Nodes[1].IncomingSegment!.SteepUphillDistanceMeters); Assert.False(loaded.IsDirty);
        using var archive = ZipFile.OpenRead(path); Assert.NotNull(archive.GetEntry("guide-data/settings.json")); Assert.NotNull(archive.GetEntry("guide-data/manual.json")); Assert.NotNull(archive.GetEntry("guide-data/plans.json")); Assert.NotNull(archive.GetEntry("guide-data/derived.json"));
    }
    [Fact] public async Task LegacyPackageWithoutGuideModuleGetsBlankIndependentDefaults()
    {
        var service = new ProjectFileService(); var path = Output("旧项目.hikeproj"); await service.SaveAsync(new() { Name = "旧项目" }, path, true);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update)) foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("guide-data/")).ToArray()) entry.Delete();
        var loaded = await service.LoadAsync(path); Assert.Equal(2, loaded.GuideData.Plans.Count); Assert.Null(loaded.GuideData.Settings.FlatDownSpeedKmh); Assert.Null(loaded.GuideData.Derived); Assert.False(loaded.IsDirty);
    }
    [Fact] public async Task PartialGuideModuleRejectedInsteadOfSilentlyLosingManualData()
    {
        var service = new ProjectFileService(); var path = Output("缺失模块.hikeproj"); await service.SaveAsync(new(), path, true);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update)) archive.GetEntry("guide-data/manual.json")!.Delete();
        await Assert.ThrowsAsync<HikingGuide.Application.Projects.ProjectFileException>(() => service.LoadAsync(path));
    }
    [Fact] public async Task FutureGuideSchemaIsRejectedRatherThanDowngraded()
    {
        var service = new ProjectFileService(); var path = Output("未来版本.hikeproj"); await service.SaveAsync(new(), path, true);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("guide-data/settings.json")!; JsonNode json; using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!;
            json["schemaVersion"] = 999; entry.Delete(); using var writer = new StreamWriter(archive.CreateEntry("guide-data/settings.json").Open()); await writer.WriteAsync(json.ToJsonString());
        }
        await Assert.ThrowsAsync<HikingGuide.Application.Projects.ProjectFileException>(() => service.LoadAsync(path));
    }
    [Fact] public async Task InvalidGuideSettingsCannotOverwriteExistingProject()
    {
        var service = new ProjectFileService(); var project = new HikingProject(); var path = Output("安全保存.hikeproj"); await service.SaveAsync(project, path, true); var oldHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        project.GuideData.Settings = project.GuideData.Settings with { FlatDownSpeedKmh = 0 };
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveAsync(project, path, true)); Assert.Equal(oldHash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }
    [Fact] public async Task ArchivedSegmentTextAndSoftRemovedValuesSurviveRestart()
    {
        var project = new HikingProject(); var a = Guid.NewGuid(); var b = Guid.NewGuid(); var key = new GuideSegmentKey(Guid.NewGuid(), a, b); var camp = project.GuideData.Settings.LocationConditions[0];
        project.GuideData.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, RoadCondition = new string('长', 40000), PlannedDurationHours = 2 };
        project.GuideData.Manual.Nodes[a] = new() { GuideNodeId = a, LocationConditions = new() { [camp.DefinitionId] = LocationConditionValue.Yes } };
        GuideDataReconciler.RemoveCondition(project.GuideData.Settings, camp.DefinitionId); GuideDataReconciler.Reconcile(project.GuideData, key.TrackId, [a, Guid.NewGuid(), b]);
        var service = new ProjectFileService(); var path = Output("归档.hikeproj"); await service.SaveAsync(project, path, true); var loaded = await service.LoadAsync(path);
        Assert.Equal(40000, loaded.GuideData.Manual.ArchivedSegments.Single().Data.RoadCondition.Length); Assert.False(loaded.GuideData.Settings.LocationConditions[0].IsActive);
        Assert.Equal(LocationConditionValue.Yes, loaded.GuideData.Manual.Nodes[a].LocationConditions[camp.DefinitionId]);
    }
    [Fact] public void ActualUserTemplateIsReadBySemanticHeadersWithoutChangingItsBytes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Templates", "攻略数据模板.xlsx"); var before = File.ReadAllBytes(path); var mapping = ExcelGuideTemplateInspector.Read(path);
        Assert.Equal(new[] { "模板（旧）", "攻略数据" }, mapping.SheetNames); Assert.Equal(3, mapping.HeaderStartRow); Assert.Equal(4, mapping.HeaderEndRow);
        Assert.Equal(5, mapping.BodyStartRow); Assert.Equal(55, mapping.SampleBodyEndRow); Assert.Equal(57, mapping.NotesStartRow);
        Assert.Equal(2, mapping.Columns.Count(c => c.Key.StartsWith("TravelPlanPrototype:"))); Assert.Contains(mapping.Columns, c => c.Key == "ReferencePlan");
        Assert.Contains(mapping.Columns, c => c.Key == "RoadCondition" && c.Width > 50); Assert.Contains(mapping.TopCells, c => c.Text == "4"); Assert.Equal(before, File.ReadAllBytes(path));
    }
}
