using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Routing;

public sealed record RoutingTileRecord(string FileName, long ByteLength, string Sha256,
    RoutingTileSource Source, DateTimeOffset AcquiredAt, string? ETag = null,
    DateTimeOffset? LastModified = null);

public sealed record RoutingDataManifest(Guid DatasetId, DateTimeOffset UpdatedAt,
    IReadOnlyList<RoutingTileRecord> Tiles, string? ChinaSourceManifestHash = null,
    IReadOnlyList<string>? InvalidTiles = null)
{
    public int TileCount => Tiles.Count;
    public long TotalBytes => Tiles.Sum(tile => tile.ByteLength);
}

public sealed record RoutingDownloadProgress(int Completed, int Total, long DownloadedBytes,
    string? CurrentTile);
public enum RoutingImportAction { Add, Replace, Keep }
public sealed record RoutingImportRequest(string SourcePath, RoutingImportAction Action);
public sealed record RoutingImportResult(RoutingDataManifest Manifest, int Added, int Replaced,
    int Kept, long CopiedBytes);
public sealed record RoutingImportProgress(int Completed, int Total, long CopiedBytes,
    string CurrentTile);
public sealed record RoutingScanResult(RoutingDataManifest? Manifest,
    IReadOnlyList<string> InvalidTiles, bool Rebuilt);

/// <summary>只操作自己的 RoutingData 根目录；下载阶段绝不改动 current。</summary>
public sealed class RoutingDataStore
{
    private const long MaxTileBytes = 512L * 1024 * 1024;
    private const int MaxParallelDownloads = 3;
    public static readonly Uri OfficialBaseUrl = new("https://brouter.de/brouter/segments4/");
    private readonly string _root;
    private readonly HttpClient _client;
    private readonly Func<long> _availableSpaceBytes;
    private readonly string _manifestDirectory;
    private readonly SemaphoreSlim? _routeGate;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly JsonSerializerOptions _json = CreateJsonOptions();
    private string CurrentPath => Path.Combine(_root, "current");
    private string OldPath => Path.Combine(_root, "old");
    private string StagingPath => Path.Combine(_root, "staging");
    private string StatePath => Path.Combine(_root, "routing-data-state.json");
    private string PublicManifestPath => Path.Combine(_manifestDirectory, "current-routing-data.json");
    private static string ManifestPath(string directory) =>
        Path.Combine(directory, "routing-data-manifest.json");

    public RoutingDataStore(string root, HttpClient? client = null, SemaphoreSlim? routeGate = null,
        Func<long>? availableSpaceBytes = null, string? manifestDirectory = null)
    {
        _root = Path.GetFullPath(root);
        _manifestDirectory = Path.GetFullPath(manifestDirectory ?? Path.Combine(_root, "manifests"));
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _routeGate = routeGate;
        _availableSpaceBytes = availableSpaceBytes ?? (() =>
            new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace);
    }

    public string CurrentSegmentsDirectory => CurrentPath;
    public string StagingDirectory => StagingPath;
    public bool IsBusy => _updateGate.CurrentCount == 0;
    public RoutingDataManifest? CurrentManifest => ReadValidManifest(CurrentPath);
    public event EventHandler<RoutingDownloadProgress>? ProgressChanged;
    public event EventHandler<RoutingImportProgress>? ImportProgressChanged;

    public async Task<RoutingScanResult> ScanCurrentAsync(IRd5IntegrityValidator validator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            Recover();
            if (!Directory.Exists(CurrentPath)) return new(null, [], false);
            var previous = ReadValidManifest(CurrentPath);
            var known = previous?.Tiles.ToDictionary(tile => tile.FileName,
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, RoutingTileRecord>(
                    StringComparer.OrdinalIgnoreCase);
            var records = new List<RoutingTileRecord>();
            var invalid = new List<string>();
            var changed = previous is null;
            foreach (var file in Directory.EnumerateFiles(CurrentPath, "*.rd5")
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                try
                {
                    BRouterTile.ParseFileName(name);
                    var size = new FileInfo(file).Length;
                    if (size < 200 || size > MaxTileBytes)
                        throw new InvalidDataException("rd5大小无效。");
                    var hash = await HashFileAsync(file, cancellationToken);
                    if (known.TryGetValue(name, out var old) &&
                        old.ByteLength == size && old.Sha256.Equals(hash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        records.Add(old);
                        continue;
                    }
                    await validator.ValidateAsync(file, cancellationToken);
                    records.Add(new RoutingTileRecord(name, size, hash,
                        RoutingTileSource.ExternalFileDetected, DateTimeOffset.UtcNow));
                    changed = true;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    invalid.Add(name);
                    changed = true;
                    var quarantine = Path.Combine(_root, "invalid");
                    Directory.CreateDirectory(quarantine);
                    File.Move(file, Path.Combine(quarantine,
                        $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}-{name}"));
                }
            }
            if (records.Count == 0)
            {
                if (invalid.Count > 0)
                    File.WriteAllText(Path.Combine(_root, "invalid-tiles.json"),
                        JsonSerializer.Serialize(invalid, _json));
                return new(null, invalid, changed);
            }
            if (!changed && previous is not null && records.Count == previous.TileCount)
                return new(previous, [], false);
            var manifest = new RoutingDataManifest(Guid.NewGuid(), DateTimeOffset.UtcNow,
                records, null, invalid);
            await File.WriteAllTextAsync(ManifestPath(CurrentPath),
                JsonSerializer.Serialize(manifest, _json), cancellationToken);
            WritePublicManifest(manifest);
            return new(manifest, invalid, true);
        }
        finally { _updateGate.Release(); }
    }

    /// <summary>只在新目录尚无数据时迁移 P7 的旧 current；哈希不符绝不删除旧目录。</summary>
    public async Task<bool> MigrateLegacyAsync(string legacyRoutingDataDirectory,
        Func<CancellationToken, Task> restartAndCheck,
        CancellationToken cancellationToken = default)
    {
        var legacyRoot = Path.GetFullPath(legacyRoutingDataDirectory);
        var legacyCurrent = Path.Combine(legacyRoot, "segments4-current");
        if (!Directory.Exists(legacyCurrent)) return false;
        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            Recover();
            if (Directory.Exists(CurrentPath) &&
                Directory.EnumerateFiles(CurrentPath, "*.rd5").Any()) return false;
            var oldManifestFile = ManifestPath(legacyCurrent);
            if (!File.Exists(oldManifestFile))
                throw new InvalidDataException("旧BRouter路由数据清单缺失；旧数据未删除，请手动使用导入功能迁移。");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(oldManifestFile,
                cancellationToken));
            var oldTiles = document.RootElement.GetProperty("Tiles").EnumerateArray()
                .Select(value => (Name: value.GetProperty("FileName").GetString()!,
                    Length: value.GetProperty("ByteLength").GetInt64(),
                    Hash: value.GetProperty("Sha256").GetString()!)).ToArray();
            if (oldTiles.Length == 0 ||
                Directory.EnumerateFiles(legacyCurrent, "*.rd5").Count() != oldTiles.Length)
                throw new InvalidDataException("旧BRouter数据集文件数与清单不一致；旧数据未删除。");
            var source = document.RootElement.TryGetProperty("ChinaSourceManifestHash", out var china)
                && china.ValueKind == JsonValueKind.String
                ? RoutingTileSource.ChinaDownload : RoutingTileSource.WorldManualDownload;
            var needed = checked(oldTiles.Sum(tile => tile.Length) + 64L * 1024 * 1024);
            if (_availableSpaceBytes() < needed)
                throw new IOException("BRouter路由数据目录磁盘空间不足，操作未开始。");
            Directory.CreateDirectory(StagingPath);
            WriteState("Migrating");
            try
            {
                var records = new List<RoutingTileRecord>();
                foreach (var tile in oldTiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BRouterTile.ParseFileName(tile.Name);
                    var oldFile = Path.Combine(legacyCurrent, tile.Name);
                    if (!File.Exists(oldFile) || new FileInfo(oldFile).Length != tile.Length)
                        throw new InvalidDataException($"旧BRouter瓦片长度不符：{tile.Name}。");
                    var hash = await CopyAndHashAsync(oldFile,
                        Path.Combine(StagingPath, tile.Name), cancellationToken);
                    if (!hash.Equals(tile.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"旧BRouter瓦片哈希不符：{tile.Name}。");
                    records.Add(new RoutingTileRecord(tile.Name, tile.Length, hash,
                        source, DateTimeOffset.UtcNow));
                }
                var newManifest = new RoutingDataManifest(Guid.NewGuid(), DateTimeOffset.UtcNow,
                    records.OrderBy(tile => tile.FileName, StringComparer.Ordinal).ToArray(),
                    source == RoutingTileSource.ChinaDownload
                        ? document.RootElement.GetProperty("ChinaSourceManifestHash").GetString() : null);
                await File.WriteAllTextAsync(ManifestPath(StagingPath),
                    JsonSerializer.Serialize(newManifest, _json), cancellationToken);
                if (ReadValidManifest(StagingPath)?.DatasetId != newManifest.DatasetId)
                    throw new InvalidDataException("迁移后BRouter数据清单验证失败。");
                await CommitStagingAsync(newManifest, restartAndCheck, cancellationToken);
                // 已由新 current 保存所有字节且哈希一致；只删除确切的旧数据集目录。
                Directory.Delete(legacyCurrent, recursive: true);
                return true;
            }
            catch
            {
                if (Directory.Exists(StagingPath)) Directory.Delete(StagingPath, recursive: true);
                if (!Directory.Exists(OldPath)) WriteState("Ready");
                throw;
            }
        }
        finally { _updateGate.Release(); }
    }

    public void Recover()
    {
        Directory.CreateDirectory(_root);
        var state = File.Exists(StatePath) ? File.ReadAllText(StatePath).Trim() : "Ready";
        if (Directory.Exists(OldPath))
        {
            if (state == "Ready" && ReadValidManifest(CurrentPath) is not null)
                Directory.Delete(OldPath, recursive: true);
            else
            {
                if (Directory.Exists(CurrentPath)) Directory.Delete(CurrentPath, recursive: true);
                Directory.Move(OldPath, CurrentPath);
            }
        }
        if (Directory.Exists(StagingPath)) Directory.Delete(StagingPath, recursive: true);
        var recovered = ReadValidManifest(CurrentPath);
        if (recovered is not null) WritePublicManifest(recovered);
        WriteState("Ready");
    }

    public async Task<RoutingDataManifest> ReplaceAsync(IReadOnlyCollection<BRouterTile> requestedTiles,
        RoutingTileSource source, string? chinaSourceHash,
        Func<CancellationToken, Task> validateAndRestartBRouter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedTiles);
        ArgumentNullException.ThrowIfNull(validateAndRestartBRouter);
        if (source is not (RoutingTileSource.ChinaDownload or RoutingTileSource.WorldManualDownload))
            throw new ArgumentException("下载只能标记为中国或世界手动来源。", nameof(source));
        if (requestedTiles.Count == 0) throw new ArgumentException("未选择任何BRouter路由瓦片。", nameof(requestedTiles));
        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            Recover();
            var tiles = requestedTiles.Distinct().OrderBy(tile => tile.FileName,
                StringComparer.Ordinal).ToArray();
            foreach (var tile in tiles) BRouterTile.ParseFileName(tile.FileName);
            await EnsureSpaceAsync(tiles, cancellationToken);
            var id = Guid.NewGuid();
            var staging = StagingPath;
            Directory.CreateDirectory(staging);
            WriteState("Downloading");
            var records = new RoutingTileRecord[tiles.Length];
            var completed = 0;
            long downloaded = 0;
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, tiles.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDownloads,
                        CancellationToken = cancellationToken }, async (index, token) =>
                    {
                        var record = await DownloadTileAsync(tiles[index], staging, source, token);
                        records[index] = record;
                        var totalBytes = Interlocked.Add(ref downloaded, record.ByteLength);
                        var done = Interlocked.Increment(ref completed);
                        ProgressChanged?.Invoke(this, new(done, tiles.Length, totalBytes, record.FileName));
                    });
                WriteState("Validating");
                var manifest = new RoutingDataManifest(id, DateTimeOffset.UtcNow,
                    records, chinaSourceHash);
                await File.WriteAllTextAsync(ManifestPath(staging),
                    JsonSerializer.Serialize(manifest, _json), cancellationToken);
                if (ReadValidManifest(staging)?.DatasetId != id)
                    throw new InvalidDataException("新下载的路由数据验证失败。");
                cancellationToken.ThrowIfCancellationRequested();
                if (_routeGate is not null) await _routeGate.WaitAsync(cancellationToken);
                try
                {
                WriteState("Switching");
                var hadOld = Directory.Exists(CurrentPath);
                if (hadOld) Directory.Move(CurrentPath, OldPath);
                try
                {
                    Directory.Move(staging, CurrentPath);
                    await validateAndRestartBRouter(cancellationToken);
                    WritePublicManifest(manifest);
                    WriteState("Ready");
                    if (hadOld) Directory.Delete(OldPath, recursive: true);
                    return manifest;
                }
                catch
                {
                    if (Directory.Exists(CurrentPath)) Directory.Delete(CurrentPath, recursive: true);
                    if (Directory.Exists(OldPath)) Directory.Move(OldPath, CurrentPath);
                    var restored = ReadValidManifest(CurrentPath);
                    if (restored is not null) WritePublicManifest(restored);
                    WriteState("Ready");
                    if (Directory.Exists(CurrentPath))
                    {
                        try { await validateAndRestartBRouter(CancellationToken.None); }
                        catch { /* 保留原始验证失败；旧数据仍已恢复到磁盘。 */ }
                    }
                    throw;
                }
                }
                finally { _routeGate?.Release(); }
            }
            catch
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
                if (File.Exists(StatePath) && !Directory.Exists(OldPath)) WriteState("Ready");
                throw;
            }
        }
        finally { _updateGate.Release(); }
    }

    public async Task<RoutingImportResult> ImportAsync(IReadOnlyCollection<RoutingImportRequest> requests,
        IRd5IntegrityValidator validator, Func<CancellationToken, Task> restartAndCheck,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(restartAndCheck);
        if (requests.Count == 0) throw new ArgumentException("请选择至少一个rd5文件。", nameof(requests));
        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            Recover();
            var existing = ReadValidManifest(CurrentPath);
            if (Directory.Exists(CurrentPath) &&
                Directory.EnumerateFiles(CurrentPath, "*.rd5").Any() && existing is null)
                throw new InvalidDataException("当前路由数据清单缺失或无效，请先扫描重建后再导入。");
            var selected = new List<(RoutingImportRequest Request, string Name, long Length)>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(request.SourcePath);
                if (!seenPaths.Add(path)) continue;
                if (!File.Exists(path)) throw new FileNotFoundException("找不到待导入rd5文件。", path);
                var name = Path.GetFileName(path);
                BRouterTile.ParseFileName(name);
                if (!seenNames.Add(name))
                    throw new InvalidOperationException($"一次导入不能包含两个同名瓦片：{name}。请只保留一个版本。");
                var length = new FileInfo(path).Length;
                if (length < 200 || length > MaxTileBytes)
                    throw new InvalidDataException($"rd5文件大小无效：{name}。");
                var exists = existing?.Tiles.Any(tile => tile.FileName == name) == true;
                if (exists && request.Action == RoutingImportAction.Add)
                    throw new InvalidOperationException($"目标路由数据已存在：{name}。请选择保留或替换。");
                if (!exists && request.Action != RoutingImportAction.Add)
                    throw new InvalidOperationException($"当前未安装瓦片：{name}，应选择新增。");
                selected.Add((request with { SourcePath = path }, name, length));
            }
            if (selected.Count == 0) throw new InvalidOperationException("没有可导入的rd5文件。");
            var toCopy = selected.Where(item => item.Request.Action != RoutingImportAction.Keep).ToArray();
            if (toCopy.Length == 0)
                return new RoutingImportResult(existing!, 0, 0, selected.Count, 0);
            var needed = checked((existing?.TotalBytes ?? 0) + toCopy.Sum(item => item.Length) +
                64L * 1024 * 1024);
            if (_availableSpaceBytes() < needed)
                throw new IOException("BRouter路由数据目录磁盘空间不足，操作未开始。");

            Directory.CreateDirectory(StagingPath);
            WriteState("Importing");
            long copied = 0;
            var completed = 0;
            try
            {
                var records = new Dictionary<string, RoutingTileRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var old in existing?.Tiles ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var oldHash = await CopyAndHashAsync(Path.Combine(CurrentPath, old.FileName),
                        Path.Combine(StagingPath, old.FileName), cancellationToken);
                    if (!oldHash.Equals(old.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"当前路由数据与清单哈希不符：{old.FileName}。请重新扫描后再导入。");
                    records.Add(old.FileName, old);
                }
                foreach (var item in toCopy)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Path.Combine(StagingPath, item.Name);
                    var staged = target + ".import-part";
                    var sourceHash = await CopyAndHashAsync(item.Request.SourcePath, staged,
                        cancellationToken);
                    File.Move(staged, target, overwrite: true);
                    await validator.ValidateAsync(target, cancellationToken);
                    // 校验进程读取的是正式 .rd5 名称；复制后还验证源未在事务期间被修改。
                    var afterHash = await HashFileAsync(item.Request.SourcePath, cancellationToken);
                    if (!sourceHash.Equals(afterHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"导入期间源文件发生变化：{item.Name}。");
                    records[item.Name] = new RoutingTileRecord(item.Name, item.Length, sourceHash,
                        RoutingTileSource.ImportedFile, DateTimeOffset.UtcNow);
                    copied += item.Length;
                    ImportProgressChanged?.Invoke(this, new(++completed, toCopy.Length, copied, item.Name));
                }
                var manifest = new RoutingDataManifest(Guid.NewGuid(), DateTimeOffset.UtcNow,
                    records.Values.OrderBy(tile => tile.FileName, StringComparer.Ordinal).ToArray());
                await File.WriteAllTextAsync(ManifestPath(StagingPath),
                    JsonSerializer.Serialize(manifest, _json), cancellationToken);
                if (ReadValidManifest(StagingPath)?.DatasetId != manifest.DatasetId)
                    throw new InvalidDataException("导入后路由数据清单验证失败。");
                await CommitStagingAsync(manifest, restartAndCheck, cancellationToken);
                return new RoutingImportResult(manifest,
                    selected.Count(item => item.Request.Action == RoutingImportAction.Add),
                    selected.Count(item => item.Request.Action == RoutingImportAction.Replace),
                    selected.Count(item => item.Request.Action == RoutingImportAction.Keep), copied);
            }
            catch
            {
                if (Directory.Exists(StagingPath)) Directory.Delete(StagingPath, recursive: true);
                if (!Directory.Exists(OldPath)) WriteState("Ready");
                throw;
            }
        }
        finally { _updateGate.Release(); }
    }

    private async Task CommitStagingAsync(RoutingDataManifest manifest,
        Func<CancellationToken, Task> restartAndCheck, CancellationToken cancellationToken)
    {
        if (_routeGate is not null) await _routeGate.WaitAsync(cancellationToken);
        try
        {
            WriteState("Switching");
            var hadCurrent = Directory.Exists(CurrentPath);
            if (hadCurrent) Directory.Move(CurrentPath, OldPath);
            try
            {
                Directory.Move(StagingPath, CurrentPath);
                await restartAndCheck(cancellationToken);
                WritePublicManifest(manifest);
                WriteState("Ready");
                if (hadCurrent) Directory.Delete(OldPath, recursive: true);
            }
            catch
            {
                if (Directory.Exists(CurrentPath)) Directory.Delete(CurrentPath, recursive: true);
                if (Directory.Exists(OldPath)) Directory.Move(OldPath, CurrentPath);
                var restored = ReadValidManifest(CurrentPath);
                if (restored is not null) WritePublicManifest(restored);
                WriteState("Ready");
                if (restored is not null)
                    try { await restartAndCheck(CancellationToken.None); }
                    catch { /* 保留原始错误，旧数据已恢复。 */ }
                throw;
            }
        }
        finally { _routeGate?.Release(); }
    }

    private static async Task<string> CopyAndHashAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        return Convert.ToHexString(await System.Security.Cryptography.LegacySha256.HashDataAsync(stream, cancellationToken));
    }

    private async Task EnsureSpaceAsync(IReadOnlyList<BRouterTile> tiles,
        CancellationToken cancellationToken)
    {
        long required = 0;
        using var limiter = new SemaphoreSlim(MaxParallelDownloads);
        var lengths = new long[tiles.Count];
        await Task.WhenAll(Enumerable.Range(0, tiles.Count).Select(async index =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head,
                    new Uri(OfficialBaseUrl, tiles[index].FileName));
                using var response = await _client.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                if (length is null or <= 0 or > MaxTileBytes)
                    throw new InvalidDataException($"无法确认BRouter瓦片大小：{tiles[index].FileName}。");
                lengths[index] = length.Value;
            }
            finally { limiter.Release(); }
        }));
        foreach (var length in lengths) required = checked(required + length);
        // 旧 current 已计入磁盘占用；新增 staging 需要独立空间，并保留写入余量。
        var reserve = Math.Max(64L * 1024 * 1024, required / 20);
        if (_availableSpaceBytes() < checked(required + reserve))
            throw new IOException("磁盘空间不足，未开始更新，现有BRouter数据保持不变。");
    }

    private async Task<RoutingTileRecord> DownloadTileAsync(BRouterTile tile, string staging,
        RoutingTileSource source,
        CancellationToken cancellationToken)
    {
        var file = tile.FileName;
        BRouterTile.ParseFileName(file);
        var uri = new Uri(OfficialBaseUrl, file);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500)
                {
                    if (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
                        continue;
                    }
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaxTileBytes)
                    throw new InvalidDataException($"BRouter瓦片超过大小上限：{file}");
                var part = Path.Combine(staging, file + ".part");
                var destination = Path.Combine(staging, file);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                {
                    var buffer = new byte[1024 * 1024];
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        length += count;
                        if (length > MaxTileBytes) throw new InvalidDataException($"BRouter瓦片超过大小上限：{file}");
                        hash.AppendData(buffer.AsSpan(0, count));
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                if (length <= 0 || response.Content.Headers.ContentLength is long expected && length != expected)
                    throw new InvalidDataException($"BRouter瓦片下载不完整：{file}");
                File.Move(part, destination);
                return new RoutingTileRecord(file, length,
                    Convert.ToHexString(hash.GetHashAndReset()), source, DateTimeOffset.UtcNow,
                    response.Headers.ETag?.Tag, response.Content.Headers.LastModified);
            }
            catch (Exception) when (attempt < 2 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
        }
        throw new IOException($"BRouter瓦片下载失败：{file}");
    }

    private RoutingDataManifest? ReadValidManifest(string directory)
    {
        var path = ManifestPath(directory);
        if (!File.Exists(path)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<RoutingDataManifest>(File.ReadAllText(path), _json);
            if (manifest is null || manifest.Tiles.Count == 0) return null;
            foreach (var tile in manifest.Tiles)
            {
                BRouterTile.ParseFileName(tile.FileName);
                var file = Path.Combine(directory, tile.FileName);
                if (!File.Exists(file) || new FileInfo(file).Length != tile.ByteLength) return null;
            }
            return manifest;
        }
        catch (Exception) { return null; }
    }

    private void WriteState(string state)
    {
        Directory.CreateDirectory(_root);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, state);
        File.Move(temp, StatePath, overwrite: true);
    }

    private void WritePublicManifest(RoutingDataManifest manifest)
    {
        Directory.CreateDirectory(_manifestDirectory);
        var temp = PublicManifestPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, _json));
        File.Move(temp, PublicManifestPath, overwrite: true);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
