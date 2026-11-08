using System.Text.Json;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Projects;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace HikingGuide.Infrastructure.Elevation;

/// <summary>全局挂载配置与只读空间索引；不写入用户 DEM 根目录。</summary>
public sealed class DemDatabaseCatalog
{
    private sealed record IndexCache(string Fingerprint, List<RasterIndexEntry> Entries);
    private readonly ApplicationDataPaths _paths;
    private readonly DemDatabaseScanner _scanner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<Guid, DemDatabase> _databases = [];
    private readonly Dictionary<Guid, IReadOnlyList<RasterIndexEntry>> _entries = [];
    private readonly Dictionary<Guid, STRtree<RasterIndexEntry>> _spatial = [];
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private string DatabaseFile => Path.Combine(_paths.RootDirectory, "dem-databases.json");
    private string CacheRoot => Path.Combine(_paths.RootDirectory, "DemCache");

    public DemDatabaseCatalog(ApplicationDataPaths paths, DemDatabaseScanner? scanner = null)
    {
        _paths = paths;
        _scanner = scanner ?? new DemDatabaseScanner();
    }

    public IReadOnlyList<DemDatabase> Databases
    {
        get { lock (_stateGate) return _databases.Values.OrderBy(database => database.Name,
            StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateGate)
            {
                _databases.Clear();
                _entries.Clear();
                _spatial.Clear();
            }
            if (!File.Exists(DatabaseFile)) return;
            var saved = JsonSerializer.Deserialize<List<DemDatabase>>(
                await File.ReadAllTextAsync(DatabaseFile, cancellationToken), _json) ?? [];
            foreach (var database in saved)
            {
                if (database.Id == Guid.Empty || _databases.ContainsKey(database.Id))
                    throw new InvalidDataException("DEM数据库配置包含重复或无效ID。");
                var current = database with { Status = Directory.Exists(database.RootPath)
                    ? database.Status : DemDatabaseStatus.Disconnected };
                lock (_stateGate) _databases.Add(database.Id, current);
                var cacheFile = CachePath(database.Id);
                if (!File.Exists(cacheFile)) continue;
                try
                {
                    var cache = JsonSerializer.Deserialize<IndexCache>(
                        await File.ReadAllTextAsync(cacheFile, cancellationToken), _json);
                    if (cache?.Fingerprint != database.Fingerprint) continue;
                    SetEntries(database.Id, cache.Entries);
                }
                catch (Exception error) when (error is JsonException or IOException)
                {
                    // 配置不丢弃；用户可重新扫描重建缓存。
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<DemScanResult> MountAsync(string name, string rootPath,
        bool unitMetersConfirmed = false, string? verticalDatumName = null,
        IProgress<DemScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = CheckName(name);
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("DEM数据目录不存在。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureUniqueName(normalizedName);
            var database = new DemDatabase(Guid.NewGuid(), normalizedName, root,
                verticalDatumName, unitMetersConfirmed);
            var result = await _scanner.ScanAsync(database, progress, cancellationToken);
            lock (_stateGate) _databases.Add(database.Id, result.Database);
            SetEntries(database.Id, result.Entries);
            await SaveAsync(database.Id, result.Entries, cancellationToken);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<DemScanResult> RescanAsync(Guid id,
        IProgress<DemScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var database = Require(id);
            var result = await _scanner.ScanAsync(database, progress, cancellationToken);
            lock (_stateGate) _databases[id] = result.Database;
            SetEntries(id, result.Entries);
            await SaveAsync(id, result.Entries, cancellationToken);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<DemScanResult> RelocateAsync(Guid id, string newRoot,
        IProgress<DemScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(newRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("新的DEM数据目录不存在。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var database = Require(id) with { RootPath = root };
            var result = await _scanner.ScanAsync(database, progress, cancellationToken);
            lock (_stateGate) _databases[id] = result.Database; // 稳定 ID 不变。
            SetEntries(id, result.Entries);
            await SaveAsync(id, result.Entries, cancellationToken);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task RenameAsync(Guid id, string name,
        CancellationToken cancellationToken = default)
    {
        var normalized = CheckName(name);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureUniqueName(normalized, id);
            lock (_stateGate) _databases[id] = Require(id) with { Name = normalized };
            await SaveDatabasesAsync(cancellationToken); // 改名不影响指纹。
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveMountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Require(id);
            lock (_stateGate)
            {
                _databases.Remove(id);
                _entries.Remove(id);
                _spatial.Remove(id);
            }
            await SaveDatabasesAsync(cancellationToken);
            var cache = CachePath(id);
            if (File.Exists(cache)) File.Delete(cache); // 只删除软件缓存，不碰 DEM 原件。
        }
        finally { _gate.Release(); }
    }

    public DemDatabase Get(Guid id) { lock (_stateGate) return Require(id); }
    public IReadOnlyList<RasterIndexEntry> GetEntries(Guid id)
    {
        lock (_stateGate) return _entries.TryGetValue(id, out var entries) ? entries : [];
    }

    public IReadOnlyList<RasterIndexEntry> FindCovering(Guid id, GeoCoordinate coordinate)
    {
        lock (_stateGate)
        {
            if (!_spatial.TryGetValue(id, out var index)) return [];
            var candidates = index.Query(new Envelope(coordinate.Longitude, coordinate.Longitude,
                coordinate.Latitude, coordinate.Latitude));
            // 常见的单瓦片覆盖无需创建筛选/排序管线；重叠区仍沿用原优先级。
            if (candidates.Count == 0) return [];
            if (candidates.Count == 1)
                return candidates[0].Wgs84Coverage.Contains(coordinate) ? [candidates[0]] : [];
            return candidates
                .Where(entry => entry.Wgs84Coverage.Contains(coordinate))
                .OrderBy(entry => entry.ResolutionMeters)
                .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>密集显示采样只查询一次行范围；逐点仍检查真实覆盖并沿用同一确定性优先级。</summary>
    public IReadOnlyList<RasterIndexEntry> FindIntersecting(Guid id, GeoBoundingBox bounds)
    {
        lock (_stateGate)
        {
            if (!_spatial.TryGetValue(id,out var index)) return [];
            return index.Query(bounds.ToEnvelope()).OrderBy(e=>e.ResolutionMeters)
                .ThenBy(e=>e.RelativePath,StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private void SetEntries(Guid id, IReadOnlyList<RasterIndexEntry> entries)
    {
        var tree = new STRtree<RasterIndexEntry>();
        foreach (var entry in entries)
            tree.Insert(entry.Wgs84Coverage.ToEnvelope(), entry);
        tree.Build();
        lock (_stateGate)
        {
            _entries[id] = entries;
            _spatial[id] = tree;
        }
    }

    private DemDatabase Require(Guid id)
    {
        lock (_stateGate) return _databases.TryGetValue(id, out var value)
            ? value : throw new KeyNotFoundException("未找到DEM高程数据库。");
    }

    private static string CheckName(string name)
    {
        var result = name.Trim();
        if (string.IsNullOrWhiteSpace(result)) throw new ArgumentException("请输入DEM数据库名称。", nameof(name));
        return result;
    }

    private void EnsureUniqueName(string name, Guid? except = null)
    {
        if (Databases.Any(database => database.Id != except &&
            database.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("已有同名DEM数据库，请修改名称。");
    }

    private string CachePath(Guid id) => Path.Combine(CacheRoot, $"{id:N}.json");

    private async Task SaveAsync(Guid id, IReadOnlyList<RasterIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CacheRoot);
        await WriteAtomicAsync(CachePath(id), new IndexCache(_databases[id].Fingerprint,
            entries.ToList()), cancellationToken);
        await SaveDatabasesAsync(cancellationToken);
    }

    private Task SaveDatabasesAsync(CancellationToken cancellationToken) =>
        WriteAtomicAsync(DatabaseFile, Databases, cancellationToken);

    private async Task WriteAtomicAsync<T>(string path, T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, _json),
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
