using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using HikingGuide.Application.Projects;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Guides;

namespace HikingGuide.Infrastructure.Projects;

public sealed class ProjectFileService : IProjectFileService
{
    public const string FormatName = "HikingGuideProject";
    public const string ApplicationVersion = "0.5.1";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly ProjectValidator _validator;
    private readonly ProjectMigrationRegistry _migrationRegistry;

    public ProjectFileService(ProjectValidator? validator = null, ProjectMigrationRegistry? migrationRegistry = null)
    {
        _validator = validator ?? new ProjectValidator();
        _migrationRegistry = migrationRegistry ?? new ProjectMigrationRegistry();
    }

    public async Task SaveAsync(
        HikingProject project,
        string filePath,
        bool createBackup,
        IProgress<ProjectOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var targetPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new ProjectFileException("项目保存失败。保存目录不存在，原项目文件未被覆盖。");

        progress?.Report(new ProjectOperationProgress("正在建立项目快照……"));
        ProjectArchiveSnapshot snapshot;
        try
        {
            lock (project)
            {
                var validation = _validator.Validate(project);
                if (!validation.IsValid)
                    throw new ProjectFileException("项目数据未通过完整性检查：" + string.Join("；", validation.Errors));
                snapshot = CreateSnapshot(project);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ProjectFileException)
        {
            throw new ProjectFileException("无法建立一致的项目快照，项目尚未保存。", exception);
        }

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            progress?.Report(new ProjectOperationProgress("正在写入临时项目文件……"));
            await WriteArchiveAsync(snapshot, tempPath, cancellationToken);
            progress?.Report(new ProjectOperationProgress("正在验证临时项目文件……"));
            await ValidateArchiveAsync(tempPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ProjectOperationProgress("正在安全替换项目文件……"));
            if (File.Exists(targetPath))
            {
                var backupPath = targetPath + ".bak";
                if (createBackup)
                    File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                else
                {
                    var transientBackup = backupPath + ".replace";
                    try { File.Replace(tempPath, targetPath, transientBackup, ignoreMetadataErrors: true); }
                    finally { if (File.Exists(transientBackup)) File.Delete(transientBackup); }
                }
            }
            else File.Move(tempPath, targetPath);
            lock (project)
            {
                foreach (var blob in project.MediaBlobs)
                {
                    if (snapshot.MediaSources.Any(source => source.EntryPath == blob.StoredPath &&
                            (ReferenceEquals(source.PendingBytes, blob.PendingBytes) ||
                             source.ArchiveFilePath == blob.ArchiveFilePath)))
                    {
                        blob.ArchiveFilePath = targetPath;
                        blob.PendingBytes = null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new ProjectFileException("项目文件正在被其它程序使用，或磁盘无法写入。原项目文件未被覆盖。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProjectFileException("项目保存失败。请检查文件权限，原项目文件未被覆盖。", exception);
        }
        catch (ProjectFileException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ProjectFileException("项目保存失败。原项目文件未被覆盖。请检查磁盘空间或文件权限。", exception);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* 临时文件清理由下次维护处理，不能掩盖原始错误。 */ }
        }
    }

    public async Task<HikingProject> LoadAsync(
        string filePath,
        IProgress<ProjectOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) throw new ProjectFileException("找不到项目文件。");
        Task<List<ElevationAnalysisResult>?>? pendingAnalyses = null;
        try
        {
            progress?.Report(new ProjectOperationProgress("正在读取项目清单……"));
            using var archive = ZipFile.OpenRead(fullPath);
            var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries.Where(item => !string.IsNullOrEmpty(item.Name) &&
                         !item.FullName.StartsWith("media/originals/", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = entry.Open();
                // ZIP已记录解压长度；直接读入最终缓冲，避免大分析缓存反复扩容再复制一份。
                // 仍完整读取、解析和验证所有数据，不丢弃或延迟加载分析结果。
                if (entry.Length > Array.MaxLength) throw new ProjectFileException("项目中的单项数据过大，无法安全读取。");
                var bytes = GC.AllocateUninitializedArray<byte>((int)entry.Length);
                await input.ReadExactlyAsync(bytes, cancellationToken);
                if (input.ReadByte() != -1) throw new InvalidDataException("项目条目长度与归档清单不一致。");
                entries[entry.FullName.Replace('\\', '/')] = bytes;
            }

            var manifest = DeserializeRequired<ProjectManifest>(entries, "manifest.json", "项目清单缺失或损坏。");
            ValidateManifest(manifest);
            var migrationContext = new ProjectMigrationContext { Entries = entries, ProjectVersion = manifest.ProjectVersion };
            await _migrationRegistry.ApplyAsync(migrationContext, cancellationToken);
            if (migrationContext.ProjectVersion != manifest.ProjectVersion)
                manifest = DeserializeRequired<ProjectManifest>(entries, "manifest.json", "升级后的项目清单无效。");

            // 迁移已完成且条目字典不再修改；大分析缓存与轨迹解码可独立并行。
            // 返回项目之前仍等待全部分析数据，不改变项目结构或完整性校验。
            pendingAnalyses = Task.Run(() => DeserializeOptional<List<ElevationAnalysisResult>>(entries, "analysis/results.json"), cancellationToken);

            progress?.Report(new ProjectOperationProgress("正在读取轨迹数据……"));
            var document = DeserializeRequired<ProjectDocument>(entries, "project.json", "项目基本信息缺失或损坏。");
            if (document.Id != manifest.ProjectId || document.ProjectVersion != manifest.ProjectVersion)
                throw new ProjectFileException("项目清单与项目基本信息不一致。");
            if (document.TrackIds.Count != manifest.TrackCount || document.TrackIds.Distinct().Count() != document.TrackIds.Count)
                throw new ProjectFileException("项目中的轨迹清单不完整。");

            var tracks = new List<Track>(document.TrackIds.Count);
            foreach (var id in document.TrackIds)
            {
                var track = DeserializeRequired<Track>(entries, $"tracks/{id:D}.json", "项目中的轨迹数据不完整。");
                if (track.Id != id) throw new ProjectFileException("项目中的轨迹 ID 与清单不一致。");
                tracks.Add(track);
            }

            var project = new HikingProject
            {
                Id = document.Id,
                Name = document.Name,
                Description = document.Description,
                CreatedAt = document.CreatedAt,
                ModifiedAt = document.ModifiedAt,
                ProjectVersion = document.ProjectVersion,
                IsAnalysisResultStale = document.IsAnalysisResultStale,
                ManualData = DeserializeRequired<ManualGuideData>(entries, "manual_data.json", "人工攻略数据缺失或损坏。"),
                AnalysisProfile = DeserializeRequired<AnalysisProfile>(entries, "analysis_profile.json", "分析配置缺失或损坏。"),
                AnalysisResult = DeserializeOptional<AnalysisResult>(entries, "analysis_result.json"),
                Settings = DeserializeRequired<ProjectSettings>(entries, "settings.json", "项目设置缺失或损坏。")
            };
            foreach (var track in tracks) project.Tracks.Add(track);
            if (document.ActiveTrackId.HasValue && tracks.All(track => track.Id != document.ActiveTrackId.Value))
                throw new ProjectFileException("当前工作轨迹引用了项目中不存在的轨迹。");
            project.ActiveTrackId = document.ActiveTrackId;
            // 旧项目没有独立主轨迹字段；旧版 ActiveTrackId 是攻略计算轨迹。
            project.MainGuideTrackId = document.HasMainGuideTrackSelection ? document.MainGuideTrackId :
                document.ActiveTrackId;
            Populate(project.Waypoints, DeserializeRequired<List<Waypoint>>(entries, "waypoints.json", "标注点数据缺失或损坏。"));
            Populate(project.GuideNodes, DeserializeRequired<List<GuideNode>>(entries, "guide_nodes.json", "攻略节点数据缺失或损坏。"));
            var ambiguousLegacyGuides = false;
            if (!document.HasMainGuideTrackSelection)
            {
                var owners = project.GuideNodes.Where(node => node.TrackId is not null)
                    .Select(node => node.TrackId!.Value).Distinct().ToArray();
                if (owners.Length == 1) project.MainGuideTrackId = owners[0];
                else if (owners.Length > 1)
                {
                    ambiguousLegacyGuides = true;
                    project.MainGuideTrackId = null;
                    foreach (var node in project.GuideNodes)
                    {
                        node.TrackId = null;
                        node.ExactPosition = null;
                        node.SnappedCoordinate = null;
                        node.IsOrphaned = true;
                    }
                }
            }
            Populate(project.GuideSegments, DeserializeRequired<List<GuideSegment>>(entries, "guide_segments.json", "攻略分段数据缺失或损坏。"));
            Populate(project.RetiredGuideSegments, DeserializeOptional<List<GuideSegment>>(entries, "retired_guide_segments.json") ?? []);
            if (ambiguousLegacyGuides) GuideStructureService.ReplaceSegments(project, []);
            Populate(project.RouteControlPoints, DeserializeRequired<List<RouteControlPoint>>(entries, "routing.json", "路线控制点数据缺失或损坏。"));
            Populate(project.ElevationAnalyses,
                await pendingAnalyses ?? []);

            if (entries.Keys.Any(key => key.StartsWith("guide-data/", StringComparison.OrdinalIgnoreCase)))
            {
                project.GuideData = new GuideWorkbenchData
                {
                    Settings = DeserializeRequired<GuideCalculationSettings>(entries, "guide-data/settings.json", "攻略计算设置缺失或损坏。"),
                    Manual = DeserializeRequired<GuideManualDataStore>(entries, "guide-data/manual.json", "攻略人工数据缺失或损坏。"),
                    Plans = DeserializeRequired<List<GuideTravelPlan>>(entries, "guide-data/plans.json", "行进方案数据缺失或损坏。"),
                    Derived = DeserializeOptional<GuideDataCalculationResult>(entries, "guide-data/derived.json")
                };
                project.GuideData.Validate();
            }

            var media = DeserializeRequired<ProjectMediaManifest>(entries, "media/manifest.json", "项目媒体清单缺失或损坏。");
            foreach (var blob in media.Blobs)
            {
                if (archive.GetEntry(blob.StoredPath) is null)
                    throw new ProjectFileException("项目图片原图缺失。");
                blob.ArchiveFilePath = fullPath;
                project.MediaBlobs.Add(blob);
            }
            Populate(project.MediaAttachments, media.Attachments);
            Populate(project.ExternalMediaReferences, media.ExternalReferences);

            new GuideStructureService().CheckPositions(project);

            var validation = _validator.Validate(project, repairDistances: true);
            if (!validation.IsValid)
                throw new ProjectFileException("项目完整性检查失败：" + string.Join("；", validation.Errors));
            project.MarkSaved();
            return project;
        }
        catch (OperationCanceledException) { throw; }
        catch (ProjectVersionException) { throw; }
        catch (ProjectFileException) { throw; }
        catch (InvalidDataException exception)
        {
            throw new ProjectFileException("无法打开项目文件。项目文件可能已损坏或格式不完整，可以尝试从自动保存或备份恢复。", exception);
        }
        catch (JsonException exception)
        {
            throw new ProjectFileException("无法打开项目文件。项目 JSON 数据已损坏，可以尝试从自动保存或备份恢复。", exception);
        }
        catch (Exception exception)
        {
            throw new ProjectFileException("无法打开项目文件。项目文件可能已损坏或格式不完整，可以尝试从自动保存或备份恢复。", exception);
        }
        finally
        {
            // 其它条目先损坏时也观察并结束已启动的解析任务，避免后台遗留故障任务。
            if (pendingAnalyses is not null) { try { await pendingAnalyses; } catch { } }
        }
    }

    public async Task ValidateArchiveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new ProjectFileException("临时项目缺少 manifest.json。");
        var projectEntry = archive.GetEntry("project.json") ?? throw new ProjectFileException("临时项目缺少 project.json。");
        var manifest = await DeserializeEntryAsync<ProjectManifest>(manifestEntry, cancellationToken);
        var document = await DeserializeEntryAsync<ProjectDocument>(projectEntry, cancellationToken);
        ValidateManifest(manifest);
        if (manifest.ProjectId != document.Id || manifest.TrackCount != document.TrackIds.Count)
            throw new ProjectFileException("临时项目的项目 ID 或轨迹数量不一致。");
        foreach (var trackId in document.TrackIds)
        {
            var entry = archive.GetEntry($"tracks/{trackId:D}.json")
                ?? throw new ProjectFileException("临时项目缺少必要轨迹文件。");
            var track = await DeserializeEntryAsync<Track>(entry, cancellationToken);
            if (track.Id != trackId) throw new ProjectFileException("临时项目的轨迹 ID 不一致。");
        }
        var mediaEntry = archive.GetEntry("media/manifest.json") ?? throw new ProjectFileException("临时项目缺少媒体清单。");
        var media = await DeserializeEntryAsync<ProjectMediaManifest>(mediaEntry, cancellationToken);
        foreach (var blob in media.Blobs)
        {
            var original = archive.GetEntry(blob.StoredPath) ?? throw new ProjectFileException("临时项目缺少图片原图。");
            if (original.Length != blob.ByteLength) throw new ProjectFileException("临时项目图片长度校验失败。");
            await using var input = original.Open();
            var hash = await SHA256.HashDataAsync(input, cancellationToken);
            if (!Convert.ToHexString(hash).Equals(blob.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ProjectFileException("临时项目图片 SHA-256 校验失败。");
        }
    }

    private static ProjectArchiveSnapshot CreateSnapshot(HikingProject project)
    {
        // UI 和后台保存均以 project 为同步边界；快照完成后 ZIP I/O 不再访问活动项目对象。
        lock (project)
        {
            project.GuideData.Validate();
            var now = DateTimeOffset.UtcNow;
            var trackBytes = project.Tracks.ToDictionary(track => track.Id, Serialize);
            var document = new ProjectDocument
            {
                Id = project.Id, Name = project.Name, Description = project.Description,
                CreatedAt = project.CreatedAt, ModifiedAt = now, ProjectVersion = project.ProjectVersion,
                ActiveTrackId = project.ActiveTrackId, MainGuideTrackId = project.MainGuideTrackId,
                HasMainGuideTrackSelection = true,
                IsAnalysisResultStale = project.IsAnalysisResultStale,
                TrackIds = project.Tracks.Select(track => track.Id).ToList()
            };
            var manifest = new ProjectManifest
            {
                ProjectVersion = project.ProjectVersion, ProjectId = project.Id, ProjectName = project.Name,
                CreatedAt = project.CreatedAt, ModifiedAt = now, TrackCount = project.Tracks.Count,
                WaypointCount = project.Waypoints.Count
            };
            return new ProjectArchiveSnapshot
            {
                Manifest = manifest, Project = document, Tracks = trackBytes,
                Waypoints = Serialize(project.Waypoints.ToList()),
                GuideNodes = Serialize(project.GuideNodes.ToList()),
                GuideSegments = Serialize(project.GuideSegments.ToList()),
                RetiredGuideSegments = Serialize(project.RetiredGuideSegments.ToList()),
                Routing = Serialize(project.RouteControlPoints.ToList()),
                ManualData = Serialize(project.ManualData), AnalysisProfile = Serialize(project.AnalysisProfile),
                AnalysisResult = Serialize(project.AnalysisResult), Settings = Serialize(project.Settings),
                ElevationAnalyses = Serialize(project.ElevationAnalyses.ToList()),
                GuideData = new Dictionary<string, byte[]>
                {
                    ["guide-data/settings.json"] = Serialize(project.GuideData.Settings),
                    ["guide-data/manual.json"] = Serialize(project.GuideData.Manual),
                    ["guide-data/plans.json"] = Serialize(project.GuideData.Plans),
                    ["guide-data/derived.json"] = Serialize(project.GuideData.Derived)
                },
                MediaManifest = Serialize(new ProjectMediaManifest
                {
                    Blobs = project.MediaBlobs.ToList(),
                    Attachments = project.MediaAttachments.ToList(),
                    ExternalReferences = project.ExternalMediaReferences.ToList()
                }),
                MediaSources = project.MediaBlobs.Select(blob =>
                    new MediaBlobCopySource(blob.StoredPath, blob.PendingBytes, blob.ArchiveFilePath)).ToArray()
            };
        }
    }

    private static async Task WriteArchiveAsync(ProjectArchiveSnapshot snapshot, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        await WriteEntryAsync(archive, "manifest.json", Serialize(snapshot.Manifest), cancellationToken);
        await WriteEntryAsync(archive, "project.json", Serialize(snapshot.Project), cancellationToken);
        foreach (var (id, bytes) in snapshot.Tracks)
            await WriteEntryAsync(archive, $"tracks/{id:D}.json", bytes, cancellationToken);
        await WriteEntryAsync(archive, "waypoints.json", snapshot.Waypoints, cancellationToken);
        await WriteEntryAsync(archive, "guide_nodes.json", snapshot.GuideNodes, cancellationToken);
        await WriteEntryAsync(archive, "guide_segments.json", snapshot.GuideSegments, cancellationToken);
        await WriteEntryAsync(archive, "retired_guide_segments.json", snapshot.RetiredGuideSegments, cancellationToken);
        await WriteEntryAsync(archive, "routing.json", snapshot.Routing, cancellationToken);
        await WriteEntryAsync(archive, "manual_data.json", snapshot.ManualData, cancellationToken);
        await WriteEntryAsync(archive, "analysis_profile.json", snapshot.AnalysisProfile, cancellationToken);
        await WriteEntryAsync(archive, "analysis_result.json", snapshot.AnalysisResult, cancellationToken);
        await WriteEntryAsync(archive, "analysis/results.json", snapshot.ElevationAnalyses, cancellationToken);
        foreach (var (entryName, bytes) in snapshot.GuideData)
            await WriteEntryAsync(archive, entryName, bytes, cancellationToken);
        await WriteEntryAsync(archive, "settings.json", snapshot.Settings, cancellationToken);
        await WriteEntryAsync(archive, "media/manifest.json", snapshot.MediaManifest, cancellationToken);
        foreach (var blob in snapshot.MediaSources)
        {
            var entry = archive.CreateEntry(blob.EntryPath, CompressionLevel.Optimal);
            await using var output = entry.Open();
            if (blob.PendingBytes is not null)
            {
                await output.WriteAsync(blob.PendingBytes, cancellationToken);
            }
            else if (blob.ArchiveFilePath is not null)
            {
                using var sourceArchive = ZipFile.OpenRead(blob.ArchiveFilePath);
                var sourceEntry = sourceArchive.GetEntry(blob.EntryPath)
                    ?? throw new ProjectFileException("项目图片原图缺失，无法保存。");
                await using var input = sourceEntry.Open();
                await input.CopyToAsync(output, cancellationToken);
            }
            else throw new ProjectFileException("项目图片没有可用的原图来源，无法保存。");
        }
        archive.CreateEntry("reserved/");
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] data, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await output.WriteAsync(data, cancellationToken);
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static T DeserializeRequired<T>(IDictionary<string, byte[]> entries, string name, string error)
    {
        if (!entries.TryGetValue(name, out var bytes)) throw new ProjectFileException(error);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new ProjectFileException(error);
    }

    private static T? DeserializeOptional<T>(IDictionary<string, byte[]> entries, string name) where T : class
    {
        if (!entries.TryGetValue(name, out var bytes)) return null;
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
    }

    private static async Task<T> DeserializeEntryAsync<T>(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(input, JsonOptions, cancellationToken)
            ?? throw new ProjectFileException($"{entry.FullName} 内容为空。");
    }

    private static void ValidateManifest(ProjectManifest manifest)
    {
        if (!string.Equals(manifest.Format, FormatName, StringComparison.Ordinal))
            throw new ProjectFileException("项目清单格式标识无效。");
        if (manifest.ProjectId == Guid.Empty) throw new ProjectFileException("项目清单中的项目 ID 无效。");
        if (manifest.ProjectVersion > ProjectMigrationRegistry.CurrentProjectVersion)
            throw new ProjectVersionException("此项目由更新版本的软件创建。当前版本无法安全打开该项目，请升级软件后重试。");
        if (manifest.ProjectVersion < 1) throw new ProjectFileException("项目清单中的版本号无效。");
    }

    private static void Populate<T>(System.Collections.ObjectModel.ObservableCollection<T> target, IEnumerable<T> source)
    {
        foreach (var item in source) target.Add(item);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
