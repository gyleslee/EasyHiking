using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using HikingGuide.Application.Projects;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;

public sealed class ProjectFileServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HikingGuide.P3.Tests", Guid.NewGuid().ToString("N"));
    private readonly ProjectFileService _service = new();

    public ProjectFileServiceTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("{broken")]
    [InlineData("[null]")]
    public async Task DamagedParallelAnalysis_IsNotSilentlySkipped(string json)
    {
        var path = Output("分析损坏.hikeproj");
        await _service.SaveAsync(CreateProject("分析损坏"), path, false);
        ReplaceEntryText(path, "analysis/results.json", json);
        await Assert.ThrowsAsync<ProjectFileException>(() => _service.LoadAsync(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalAnalysis_MissingOrNullRetainsCompatibility(bool missing)
    {
        var path = Output("可选分析.hikeproj");
        await _service.SaveAsync(CreateProject("可选分析"), path, false);
        if (missing) { using var zip = ZipFile.Open(path, ZipArchiveMode.Update); zip.GetEntry("analysis/results.json")!.Delete(); }
        else ReplaceEntryText(path, "analysis/results.json", "null");
        Assert.Empty((await _service.LoadAsync(path)).ElevationAnalyses);
    }

    [Fact]
    public async Task LoadCancelledBeforeRead_DoesNotAlterArchive()
    {
        var path = Output("取消读取.hikeproj");
        await _service.SaveAsync(CreateProject("取消读取"), path, false);
        var bytes = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.LoadAsync(path, cancellationToken: cancellation.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task EmptyProject_RoundTrip_PreservesIdentity()
    {
        var project = CreateProject("空项目");
        var path = Output("空项目.hikeproj");
        await _service.SaveAsync(project, path, true);

        var restored = await _service.LoadAsync(path);

        Assert.Equal(project.Id, restored.Id);
        Assert.Equal("空项目", restored.Name);
        Assert.Empty(restored.Tracks);
        Assert.Null(restored.ActiveTrackId);
        Assert.False(restored.IsDirty);
    }

    [Fact]
    public async Task P52_WorkspaceDeduplicatesSamePathButAllowsCopiedProjectAtDifferentPath()
    {
        var project = CreateProject("工作区");
        var firstPath = Output("工作区甲.hikeproj");
        var copiedPath = Output("工作区乙.hikeproj");
        await _service.SaveAsync(project, firstPath, true);
        File.Copy(firstPath, copiedPath);
        var workspace = new ProjectService(_service);
        var first = await workspace.OpenAsync(firstPath);
        Assert.Same(first, await workspace.OpenAsync(firstPath));
        Assert.Single(workspace.OpenProjects);
        var copied = await workspace.OpenAsync(copiedPath);
        Assert.NotSame(first, copied);
        Assert.Equal(first.Id, copied.Id);
        Assert.Equal(2, workspace.OpenProjects.Count);
        Assert.NotEqual(workspace.OpenProjects[0].ContextId, workspace.OpenProjects[1].ContextId);
        Assert.True(workspace.Activate(workspace.OpenProjects[0].ContextId));
        Assert.Same(first, workspace.CurrentProject);
    }

    [Fact]
    public async Task MultiTrack_RoundTrip_PreservesActiveTrackAndAllPoints()
    {
        var project = CreateProject("多轨迹");
        var first = CreateTrack("主线", 30, 110);
        var second = CreateTrack("备用线", 31, 111);
        project.AddTrack(first);
        project.AddTrack(second);
        project.SetActiveTrack(second.Id);

        var path = Output("多轨迹.hikeproj");
        await _service.SaveAsync(project, path, true);
        var restored = await _service.LoadAsync(path);

        Assert.Equal(2, restored.Tracks.Count);
        Assert.Equal(second.Id, restored.ActiveTrackId);
        Assert.Equal("备用线", restored.ActiveTrack?.Name);
        Assert.Equal(4, restored.Tracks.Sum(track => track.GetPointCount()));
    }

    [Fact]
    public async Task P52_MainTrackAndWaypointOwnershipPersistAndDeletedTrackDoesNotRevive()
    {
        var project = CreateProject("P5-2持久化");
        var main = CreateTrack("主线", 30, 110);
        var ordinary = CreateTrack("备用", 31, 111);
        project.AddTrack(main); project.AddTrack(ordinary);
        project.MainGuideTrackId = main.Id;
        project.ActiveTrackId = ordinary.Id;
        var marker = new Waypoint { Name = "水源", Latitude = 31, Longitude = 111,
            AssociatedTrackId = ordinary.Id };
        project.Waypoints.Add(marker);
        var guide = marker.ToGuideNode();
        guide.TrackId = main.Id; guide.IsOrphaned = true;
        project.GuideNodes.Add(guide);
        var path = Output("P5-2持久化.hikeproj");
        await _service.SaveAsync(project, path, true);
        var loaded = await _service.LoadAsync(path);
        Assert.Equal(main.Id, loaded.MainGuideTrackId);
        Assert.Equal(ordinary.Id, loaded.ActiveTrackId);
        Assert.Equal(ordinary.Id, Assert.Single(loaded.Waypoints).AssociatedTrackId);
        Assert.Equal(ordinary.Id, Assert.Single(loaded.GuideNodes).SourceTrackId);
        new TrackManagementService().PrepareDeletion(loaded, ordinary.Id).Execute();
        await _service.SaveAsync(loaded, path, true);
        using (var archive = ZipFile.OpenRead(path))
            Assert.Null(archive.GetEntry($"tracks/{ordinary.Id:D}.json"));
        var reloaded = await _service.LoadAsync(path);
        Assert.Single(reloaded.Tracks);
        Assert.Null(Assert.Single(reloaded.Waypoints).AssociatedTrackId);
        Assert.Null(Assert.Single(reloaded.GuideNodes).SourceTrackId);
    }

    [Fact]
    public async Task P52_AmbiguousLegacyGuideOwnersBecomeOrphanedWithoutLosingManualSegments()
    {
        var project = CreateProject("旧版多主线冲突");
        var first = CreateTrack("甲", 30, 110);
        var second = CreateTrack("乙", 31, 111);
        project.AddTrack(first); project.AddTrack(second);
        var guideService = new GuideStructureService();
        var start = new GuideNode { Name = "起点", OriginalCoordinate = new GeoCoordinate(30, 110) };
        var end = new GuideNode { Name = "终点", OriginalCoordinate = new GeoCoordinate(30, 110.01) };
        guideService.SnapNode(start, first); guideService.SnapNode(end, first);
        project.GuideNodes.Add(start); project.GuideNodes.Add(end);
        GuideStructureService.ReplaceSegments(project, guideService.RebuildSegments(project));
        Assert.Single(project.GuideSegments);
        var path = Output("旧版多主线冲突.hikeproj");
        await _service.SaveAsync(project, path, true);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            ReplaceJson("project.json", root =>
            {
                root["hasMainGuideTrackSelection"] = false;
                root["mainGuideTrackId"] = null;
            });
            ReplaceJson("guide_nodes.json", root =>
                ((JsonArray)root)[1]!["trackId"] = second.Id.ToString());

            void ReplaceJson(string name, Action<JsonNode> update)
            {
                var entry = archive.GetEntry(name)!;
                JsonNode root;
                using (var reader = new StreamReader(entry.Open())) root = JsonNode.Parse(reader.ReadToEnd())!;
                update(root);
                entry.Delete();
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(root.ToJsonString());
            }
        }
        var migrated = await _service.LoadAsync(path);
        Assert.Null(migrated.MainGuideTrackId);
        Assert.Equal(2, migrated.GuideNodes.Count);
        Assert.All(migrated.GuideNodes, node => { Assert.True(node.IsOrphaned); Assert.Null(node.TrackId); });
        Assert.Empty(migrated.GuideSegments);
        Assert.Single(migrated.RetiredGuideSegments);
    }

    [Fact]
    public async Task MapLayerVisibility_RoundTrip_DoesNotRemoveTrackOrSegment()
    {
        var project = CreateProject("地图显隐");
        var track = CreateTrack("隐藏轨迹", 30, 110);
        project.AddTrack(track);
        var start = new GuideNode { TrackId = track.Id, OriginalCoordinate = new GeoCoordinate(30, 110) };
        var end = new GuideNode { TrackId = track.Id, OriginalCoordinate = new GeoCoordinate(30, 110.01) };
        project.GuideNodes.Add(start);
        project.GuideNodes.Add(end);
        var segment = new GuideSegment { TrackId = track.Id, StartNodeId = start.Id, EndNodeId = end.Id,
            StartDistance = 0, EndDistance = 1000 };
        project.GuideSegments.Add(segment);
        MapLayerVisibility.SetTrackVisible(project.Settings.Map, track.Id, false);
        MapLayerVisibility.SetGuideSegmentVisible(project.Settings.Map, segment.Id, false);

        var path = Output("地图显隐.hikeproj");
        await _service.SaveAsync(project, path, true);
        var restored = await _service.LoadAsync(path);

        Assert.False(MapLayerVisibility.IsTrackVisible(restored.Settings.Map, track.Id));
        Assert.False(MapLayerVisibility.IsGuideSegmentVisible(restored.Settings.Map, segment.Id));
        Assert.Equal(track.GetPointCount(), restored.Tracks.Single().GetPointCount());
        Assert.Single(restored.GuideSegments);
    }

    [Fact]
    public async Task ModularArchive_ContainsExpectedEntriesAndOneFilePerTrack()
    {
        var project = CreateProject("模块化");
        project.AddTrack(CreateTrack("一", 30, 110));
        project.AddTrack(CreateTrack("二", 31, 111));
        var path = Output("模块化.hikeproj");
        await _service.SaveAsync(project, path, true);

        using var archive = ZipFile.OpenRead(path);
        var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("manifest.json", names);
        Assert.Contains("project.json", names);
        Assert.Contains("waypoints.json", names);
        Assert.Contains("guide_nodes.json", names);
        Assert.Contains("guide_segments.json", names);
        Assert.Contains("routing.json", names);
        Assert.Contains("manual_data.json", names);
        Assert.Contains("analysis_profile.json", names);
        Assert.Contains("analysis_result.json", names);
        Assert.Contains("settings.json", names);
        Assert.Equal(2, names.Count(name => name.StartsWith("tracks/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json")));
    }

    [Fact]
    public async Task CompleteData_RoundTrip_PreservesManualAttributesSettingsAndReferences()
    {
        var project = CreateProject("完整数据");
        var track = CreateTrack("主线", 30, 110);
        track.Metadata["来源"] = "中文元数据";
        track.Segments[0].OriginalAttributes["扩展"] = "轨迹段";
        track.Segments[0].Points[0].OriginalAttributes["原始"] = "保留";
        project.AddTrack(track);
        project.Waypoints.Add(new Waypoint { Name = "水源", Latitude = 30.2, Longitude = 110.2, OriginalAttributes = new() { ["图标"] = "water" } });
        project.GuideNodes.Add(new GuideNode
        {
            Name = "营地", TrackId = track.Id, OriginalCoordinate = new(30.1, 110.1),
            SnappedCoordinate = new(30.1001, 110.1001), ManualData = new() { Notes = "人工备注", Water = "有水" }
        });
        project.RouteControlPoints.Add(new RouteControlPoint { Name = "必经点", Coordinate = new(30.3, 110.3), Order = 1 });
        project.ManualData.Plan = "两天方案";
        project.AnalysisProfile = AnalysisProfile.CreateTestProfile();
        project.AnalysisResult = new AnalysisResult { TotalDistance = 1234 };
        project.IsAnalysisResultStale = true;
        project.Settings.Dem.DataDirectory = new PathReference { Path = "F:\\DEM", Kind = PathReferenceKind.Absolute };
        project.Settings.Excel.Template = new PathReference { Path = "模板\\攻略.xlsx", Kind = PathReferenceKind.ProjectRelative };

        var path = Output("中文 路径（完整）.hikeproj");
        await _service.SaveAsync(project, path, true);
        var restored = await _service.LoadAsync(path);

        Assert.Equal("保留", restored.Tracks[0].Segments[0].Points[0].OriginalAttributes["原始"]);
        Assert.Equal("轨迹段", restored.Tracks[0].Segments[0].OriginalAttributes["扩展"]);
        Assert.Equal("人工备注", restored.GuideNodes[0].ManualData?.Notes);
        Assert.Equal(track.Id, restored.GuideNodes[0].TrackId);
        Assert.Equal("两天方案", restored.ManualData.Plan);
        Assert.Equal(1234, restored.AnalysisResult?.TotalDistance);
        Assert.True(restored.IsAnalysisResultStale);
        Assert.Equal(PathReferenceKind.ProjectRelative, restored.Settings.Excel.Template?.Kind);
    }

    [Fact]
    public async Task SourceFile_IsInformationalAndNotRequiredToOpen()
    {
        var project = CreateProject("来源独立");
        var track = CreateTrack("路线", 30, 110);
        track.SourceFile = Output("已经不存在.kml");
        project.AddTrack(track);
        var path = Output("来源独立.hikeproj");
        await _service.SaveAsync(project, path, true);

        var restored = await _service.LoadAsync(path);

        Assert.Equal(2, restored.Tracks[0].GetPointCount());
        Assert.Equal(track.SourceFile, restored.Tracks[0].SourceFile);
    }

    [Fact]
    public async Task Overwrite_CreatesSingleLatestBackup()
    {
        var project = CreateProject("第一版");
        var path = Output("备份.hikeproj");
        await _service.SaveAsync(project, path, true);
        project.Name = "第二版";
        await _service.SaveAsync(project, path, true);
        project.Name = "第三版";
        await _service.SaveAsync(project, path, true);

        Assert.True(File.Exists(path + ".bak"));
        Assert.Equal("第二版", (await _service.LoadAsync(path + ".bak")).Name);
        Assert.Equal("第三版", (await _service.LoadAsync(path)).Name);
        Assert.Single(Directory.EnumerateFiles(_directory, "*.bak"));
    }

    [Fact]
    public async Task InvalidProject_SaveFailsWithoutChangingExistingFile()
    {
        var project = CreateProject("有效版本");
        var path = Output("保护.hikeproj");
        await _service.SaveAsync(project, path, true);
        var before = await File.ReadAllBytesAsync(path);
        project.Waypoints.Add(new Waypoint { Latitude = 500, Longitude = 110 });

        await Assert.ThrowsAsync<ProjectFileException>(() => _service.SaveAsync(project, path, true));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MissingDirectory_SaveReturnsFriendlyFailure()
    {
        var exception = await Assert.ThrowsAsync<ProjectFileException>(() =>
            _service.SaveAsync(CreateProject("测试"), Path.Combine(_directory, "不存在", "项目.hikeproj"), true));
        Assert.Contains("目录不存在", exception.Message);
    }

    [Fact]
    public async Task LockedTarget_SaveFailsWithoutInfiniteRetryOrOverwrite()
    {
        var path = Output("占用.hikeproj");
        var project = CreateProject("第一版");
        await _service.SaveAsync(project, path, true);
        var before = await File.ReadAllBytesAsync(path);
        project.Name = "第二版";
        await using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = await Assert.ThrowsAsync<ProjectFileException>(() => _service.SaveAsync(project, path, true));
            Assert.Contains("其它程序", exception.Message);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("不是ZIP.hikeproj")]
    [InlineData("空ZIP.hikeproj")]
    public async Task DamagedArchive_ReturnsFriendlyFailure(string fileName)
    {
        var path = Output(fileName);
        if (fileName.StartsWith("不是", StringComparison.Ordinal)) await File.WriteAllTextAsync(path, "not zip");
        else using (ZipFile.Open(path, ZipArchiveMode.Create)) { }

        var exception = await Assert.ThrowsAsync<ProjectFileException>(() => _service.LoadAsync(path));

        Assert.Contains("项目", exception.Message);
    }

    [Fact]
    public async Task MissingTrackEntry_IsRejected()
    {
        var project = CreateProject("缺轨迹");
        project.AddTrack(CreateTrack("轨迹", 30, 110));
        var path = Output("缺轨迹.hikeproj");
        await _service.SaveAsync(project, path, true);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            archive.Entries.Single(entry => entry.FullName.StartsWith("tracks/") && entry.Name.Length > 0).Delete();

        var exception = await Assert.ThrowsAsync<ProjectFileException>(() => _service.LoadAsync(path));

        Assert.Contains("轨迹数据不完整", exception.Message);
    }

    [Fact]
    public async Task MalformedManifestJson_IsRejected()
    {
        var path = Output("清单损坏.hikeproj");
        await _service.SaveAsync(CreateProject("清单损坏"), path, true);
        ReplaceEntryText(path, "manifest.json", "{ not-json");

        var exception = await Assert.ThrowsAsync<ProjectFileException>(() => _service.LoadAsync(path));

        Assert.Contains("JSON", exception.Message);
    }

    [Fact]
    public async Task MalformedTrackJson_IsRejected()
    {
        var project = CreateProject("轨迹损坏");
        project.AddTrack(CreateTrack("轨迹", 30, 110));
        var path = Output("轨迹损坏.hikeproj");
        await _service.SaveAsync(project, path, true);
        ReplaceEntryText(path, $"tracks/{project.Tracks[0].Id:D}.json", "[]");

        var exception = await Assert.ThrowsAsync<ProjectFileException>(() => _service.LoadAsync(path));

        Assert.Contains("JSON", exception.Message);
    }

    [Fact]
    public async Task HigherProjectVersion_IsRejectedWithoutChangingFile()
    {
        var project = CreateProject("高版本");
        var path = Output("高版本.hikeproj");
        await _service.SaveAsync(project, path, true);
        ReplaceJsonProperty(path, "manifest.json", "projectVersion", 99);
        var before = await File.ReadAllBytesAsync(path);

        var exception = await Assert.ThrowsAsync<ProjectVersionException>(() => _service.LoadAsync(path));

        Assert.Contains("更新版本", exception.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task V1Archive_IsMigratedInMemoryWithoutChangingOriginalFile()
    {
        var project = CreateProject("旧版项目");
        project.AddTrack(CreateTrack("旧轨迹", 30, 110));
        var path = Output("旧版项目.hikeproj");
        await _service.SaveAsync(project, path, false);
        ReplaceJsonProperty(path, "manifest.json", "projectVersion", 1);
        ReplaceJsonProperty(path, "project.json", "projectVersion", 1);
        var original = await File.ReadAllBytesAsync(path);

        var restored = await _service.LoadAsync(path);

        Assert.Equal(3, restored.ProjectVersion);
        Assert.Single(restored.Tracks);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task P5GuidePositionsTypesAndManualData_RoundTripWithoutReSnapping()
    {
        var project = CreateProject("攻略项目");
        var track = CreateTrack("主线", 30, 110);
        project.AddTrack(track);
        var guide = new GuideStructureService();
        foreach (var (name, offset) in new[] { ("起点", 0.0002), ("营地", 0.0008) })
        {
            var node = new GuideNode { Name = name, Type = WaypointType.Custom, CustomTypeName = "铁梯",
                TrackId = track.Id, OriginalCoordinate = new(30 + offset, 110 + offset),
                ManualData = new ManualGuideData { Notes = "人工备注" } };
            guide.SnapNode(node, track);
            project.GuideNodes.Add(node);
        }
        var segment = Assert.Single(guide.RebuildSegments(project));
        segment.ManualData = new SegmentManualData { RoadCondition = "乱石" };
        project.GuideSegments.Add(segment);
        var path = Output("攻略项目.hikeproj");
        await _service.SaveAsync(project, path, false);

        var restored = await _service.LoadAsync(path);

        Assert.Equal(3, restored.ProjectVersion);
        Assert.Equal(segment.Id, Assert.Single(restored.GuideSegments).Id);
        Assert.Equal("乱石", restored.GuideSegments[0].ManualData?.RoadCondition);
        Assert.All(restored.GuideNodes, node =>
        {
            Assert.False(node.IsOrphaned);
            Assert.NotNull(node.ExactPosition);
            Assert.Equal("铁梯", node.CustomTypeName);
            Assert.Equal("人工备注", node.ManualData?.Notes);
        });
    }

    [Fact]
    public async Task RenamingTrackDoesNotOrphanGuideNode()
    {
        var project = CreateProject("重命名");
        var track = CreateTrack("旧名", 30, 110);
        project.AddTrack(track);
        var node = new GuideNode { TrackId = track.Id, OriginalCoordinate = new(30, 110) };
        new GuideStructureService().SnapNode(node, track);
        project.GuideNodes.Add(node);
        track.Name = "新名";
        project.NotifyTrackChanged();
        var path = Output("重命名.hikeproj");
        await _service.SaveAsync(project, path, false);
        var restored = await _service.LoadAsync(path);
        Assert.False(Assert.Single(restored.GuideNodes).IsOrphaned);
    }

    [Fact]
    public async Task RetiredGuideSegmentManualData_IsSavedAndRestored()
    {
        var project = CreateProject("历史分段");
        var retired = new GuideSegment { ManualData = new SegmentManualData { Description = "人工分段说明" } };
        project.RetiredGuideSegments.Add(retired);
        var path = Output("历史分段.hikeproj");
        await _service.SaveAsync(project, path, false);
        var reopened = await _service.LoadAsync(path);
        Assert.Equal(retired.Id, Assert.Single(reopened.RetiredGuideSegments).Id);
        Assert.Equal("人工分段说明", reopened.RetiredGuideSegments[0].ManualData?.Description);
    }

    [Fact]
    public async Task InvalidCoordinateInArchive_IsRejected()
    {
        var project = CreateProject("非法坐标");
        project.Waypoints.Add(new Waypoint { Latitude = 30, Longitude = 110 });
        var path = Output("非法坐标.hikeproj");
        await _service.SaveAsync(project, path, true);
        ReplaceJsonProperty(path, "waypoints.json", "latitude", 500);

        var exception = await Assert.ThrowsAsync<ProjectFileException>(() => _service.LoadAsync(path));

        Assert.Contains("无效坐标", exception.Message);
    }

    [Fact]
    public async Task InvalidCumulativeDistance_IsRecalculatedOnLoad()
    {
        var project = CreateProject("距离修复");
        project.AddTrack(CreateTrack("轨迹", 30, 110));
        var path = Output("距离修复.hikeproj");
        await _service.SaveAsync(project, path, true);
        var trackEntry = $"tracks/{project.Tracks[0].Id:D}.json";
        ReplaceJsonProperty(path, trackEntry, "distanceFromStart", 999999, occurrence: 1);

        var restored = await _service.LoadAsync(path);

        Assert.InRange(restored.Tracks[0].Segments[0].Points[1].DistanceFromStart, 140, 160);
    }

    [Fact]
    public void PathReference_ResolvesAbsoluteAndProjectRelativePaths()
    {
        var projectPath = Output("项目.hikeproj");
        var relative = new PathReference { Path = Path.Combine("模板", "攻略.xlsx"), Kind = PathReferenceKind.ProjectRelative };
        var absolutePath = Output("DEM");
        var absolute = new PathReference { Path = absolutePath, Kind = PathReferenceKind.Absolute };

        Assert.Equal(Path.GetFullPath(Path.Combine(_directory, "模板", "攻略.xlsx")), relative.Resolve(projectPath));
        Assert.Equal(Path.GetFullPath(absolutePath), absolute.Resolve(projectPath));
    }

    private static HikingProject CreateProject(string name)
    {
        var project = new HikingProject { Name = name };
        project.MarkSaved();
        return project;
    }

    private static Track CreateTrack(string name, double latitude, double longitude)
    {
        var track = new Track
        {
            Name = name,
            SourceFormat = TrackSourceFormat.Kml,
            Segments = [new TrackSegment { Points = [
                new TrackPoint { Latitude = latitude, Longitude = longitude },
                new TrackPoint { Latitude = latitude + 0.001, Longitude = longitude + 0.001 }
            ] }]
        };
        track.RecalculateDistances();
        return track;
    }

    private string Output(string fileName) => Path.Combine(_directory, fileName);

    private static void ReplaceJsonProperty(string archivePath, string entryName, string propertyName, double value, int occurrence = 0)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry(entryName)!;
        string json;
        using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
        using var document = JsonDocument.Parse(json);
        var count = -1;
        var replaced = Rewrite(document.RootElement);
        entry.Delete();
        var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(replacement.Open());
        writer.Write(replaced);

        string Rewrite(JsonElement element)
        {
            using var stream = new MemoryStream();
            using (var jsonWriter = new Utf8JsonWriter(stream)) Write(element, jsonWriter);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        void Write(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.NameEquals(propertyName) && ++count == occurrence) writer.WriteNumberValue(value);
                    else Write(property.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item, writer); writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }
    }

    private static void ReplaceEntryText(string archivePath, string entryName, string content)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
