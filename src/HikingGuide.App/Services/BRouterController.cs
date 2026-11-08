using System.IO;
using HikingGuide.Application.Logging;
using HikingGuide.Application.Maps;
using HikingGuide.Application.Routing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.Routing;

namespace HikingGuide.App.Services;

public sealed class BRouterController : IAsyncDisposable
{
    private readonly ILoggingService _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _routeGate = new(1, 1);
    internal SemaphoreSlim DisplayDataGate=>_routeGate;
    public event EventHandler? DatasetChanged;
    public IBRouterPathProvider Paths { get; }
    public RoutingDataStore Data { get; }
    public BRouterProcessService Process { get; }
    public BRouterProvider Provider { get; }
    public WorldTileCatalog WorldTiles { get; } = new();
    public ChinaTileManifest China { get; }
    public event EventHandler<RoutingDownloadProgress>? ProgressChanged;
    public event EventHandler<RoutingImportProgress>? ImportProgressChanged;
    public event EventHandler<string>? UpdateCompleted;
    public event EventHandler<string>? WarningRaised;

    public BRouterController(ILoggingService log, IBRouterPathProvider? paths = null)
    {
        _log = log;
        Paths = paths ?? new BRouterPathProvider();
        Data = new RoutingDataStore(Paths.RoutingDataDirectory, routeGate: _routeGate,
            manifestDirectory: Paths.ManifestDirectory);
        var runtime = Environment.GetEnvironmentVariable("HIKINGGUIDE_BROUTER_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtime))
            runtime = Paths.RuntimeDirectory;
        var java = Environment.GetEnvironmentVariable("HIKINGGUIDE_BROUTER_JAVA_EXE");
        if (string.IsNullOrWhiteSpace(java))
            java = Path.Combine(runtime, "java", "bin", "java.exe");
        Process = new BRouterProcessService(java,
            Path.Combine(runtime, "brouter-1.7.10-all.jar"),
            Data.CurrentSegmentsDirectory, Path.Combine(runtime, "profiles2"));
        Provider = new BRouterProvider(Process,
            () => Data.CurrentManifest?.DatasetId.ToString("N") ?? "unknown",
            routeGate: _routeGate);
        var resource = typeof(App).Assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("ChinaRd5Tiles.txt", StringComparison.Ordinal));
        using var stream = typeof(App).Assembly.GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        China = ChinaTileManifest.ParseRd5(buffer.ToArray());
        if (!China.IsApprovedRd5Range) throw new InvalidDataException("内置中国范围路由清单与已确认的文件清单不一致。");
        Data.ProgressChanged += (_, progress) => ProgressChanged?.Invoke(this, progress);
        Data.ImportProgressChanged += (_, progress) => ImportProgressChanged?.Invoke(this, progress);
    }

    public Task RecoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Data.Recover, cancellationToken);

    public async Task InitializeLocalAsync(CancellationToken cancellationToken = default)
    {
        Paths.EnsureWritable();
        var validator = new BRouterRd5Validator(
            Path.Combine(Paths.RuntimeDirectory, "java", "bin", "java.exe"),
            Path.Combine(Paths.RuntimeDirectory, "brouter-1.7.10-all.jar"));
        var legacy = Path.Combine(new ApplicationDataPaths().BRouterDirectory, "RoutingData");
        var migrated = await Data.MigrateLegacyAsync(legacy, token => Process.RestartAsync(token),
            cancellationToken);
        if (migrated) _log.Information("[RoutingData] 旧版BRouter数据已迁入软件目录并完成哈希校验");
        var scan = await Data.ScanCurrentAsync(validator, cancellationToken);
        if (scan.InvalidTiles.Count > 0)
        {
            _log.Warning($"[RoutingData] 发现{scan.InvalidTiles.Count}个无效rd5，已隔离，不供BRouter使用");
            WarningRaised?.Invoke(this,
                $"发现 {scan.InvalidTiles.Count} 个无效BRouter路由数据文件，已隔离到路由数据目录的 invalid 文件夹，不会用于路线规划。\n\n文件：{string.Join("、", scan.InvalidTiles.Take(10))}");
        }
        if (scan.Manifest is not null && !Process.IsRunning)
            await Process.StartAsync(cancellationToken);
    }

    public Task<RoutingDataManifest> UpdateChinaAsync(CancellationToken cancellationToken = default)
    {
        if (!China.IsApprovedRd5Range) throw new InvalidDataException("中国范围路由清单校验失败，已阻止下载。");
        return UpdateAsync(China.Tiles, RoutingTileSource.ChinaDownload, China.SourceSha256, cancellationToken);
    }

    public Task<RoutingDataManifest> UpdateManualAsync(IReadOnlyCollection<BRouterTile> tiles,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(tiles, RoutingTileSource.WorldManualDownload, null, cancellationToken);

    public async Task<RoutingImportResult> ImportAsync(IReadOnlyCollection<RoutingImportRequest> files,
        CancellationToken cancellationToken = default)
    {
        Paths.EnsureWritable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var validator = new BRouterRd5Validator(
            Path.Combine(Paths.RuntimeDirectory, "java", "bin", "java.exe"),
            Path.Combine(Paths.RuntimeDirectory, "brouter-1.7.10-all.jar"));
        var result = await Data.ImportAsync(files, validator, async token =>
        {
            await Process.RestartAsync(token);
            if (Data.CurrentManifest?.Tiles.Any(tile => tile.FileName == "E110_N20.rd5") == true)
                await new BRouterProvider(Process, () => "import-smoke").RouteFootShortestAsync(
                    new GeoCoordinate(23.1382, 113.3270),
                    new GeoCoordinate(23.1386, 113.3280), token);
        }, linked.Token);
        _log.Information($"[RoutingImport] 文件={files.Count}；新增={result.Added}；替换={result.Replaced}；保留={result.Kept}；数据集={result.Manifest.DatasetId:N}");
        DatasetChanged?.Invoke(this,EventArgs.Empty);
        return result;
    }

    private async Task<RoutingDataManifest> UpdateAsync(IReadOnlyCollection<BRouterTile> tiles,
        RoutingTileSource source, string? chinaHash, CancellationToken cancellationToken)
    {
        Paths.EnsureWritable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _log.Information($"[RoutingDownload] 开始下载 {tiles.Count} 个官方rd5瓦片，来源={source}");
        var manifest = await Data.ReplaceAsync(tiles, source, chinaHash, async token =>
        {
            await Process.RestartAsync(token);
            if (tiles.Any(tile => tile.FileName == "E110_N20.rd5"))
            {
                // 已通过真实广东 rd5 核对的固定点，仅用于切换后的引擎烟雾测试。
                var smoke = new BRouterProvider(Process, () => "smoke");
                await smoke.RouteFootShortestAsync(new GeoCoordinate(23.1382, 113.3270),
                    new GeoCoordinate(23.1386, 113.3280), token);
            }
        }, linked.Token);
        _log.Information($"[RoutingDownload] 数据集={manifest.DatasetId:N}；瓦片={manifest.TileCount}；字节={manifest.TotalBytes}");
        UpdateCompleted?.Invoke(this, "下载完成并已删除旧数据");
        DatasetChanged?.Invoke(this,EventArgs.Empty);
        return manifest;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await Process.DisposeAsync();
        _routeGate.Dispose();
        _lifetime.Dispose();
    }
}
