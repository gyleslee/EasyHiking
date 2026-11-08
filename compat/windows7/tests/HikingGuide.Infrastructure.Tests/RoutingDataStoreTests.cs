using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Routing;
using Xunit;

namespace HikingGuide.Infrastructure.Tests;

public sealed class RoutingDataStoreTests
{
    private sealed class TileHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? FailingName { get; set; }
        public string? BlockedName { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var name = Path.GetFileName(request.RequestUri!.LocalPath);
            if (name == BlockedName && request.Method == HttpMethod.Get)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (name == FailingName)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4, 5])
            };
        }
    }

    private sealed class FakeValidator : IRd5IntegrityValidator
    {
        public Task ValidateAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (File.ReadAllBytes(filePath)[0] == 0xFF)
                throw new InvalidDataException("模拟rd5损坏。");
            return Task.CompletedTask;
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string PathValue { get; } = Path.Combine(Path.GetTempPath(),
            $"HikingGuide-P7-routing-test-{Guid.NewGuid():N}");
        public void Dispose()
        {
            var full = Path.GetFullPath(PathValue);
            if (System.IO.Path.GetFileName(full).StartsWith("HikingGuide-P7-routing-test-",
                    StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
    }

    [Fact]
    public async Task ManualReplacement_ContainsOnlyLatestSelection_AndDeletesOld()
    {
        using var root = new TestDirectory();
        using var client = new HttpClient(new TileHandler());
        var store = new RoutingDataStore(root.PathValue, client);
        var first = Tiles(10);
        await store.ReplaceAsync(first, RoutingTileSource.WorldManualDownload, null, _ => Task.CompletedTask);
        Assert.Equal(10, store.CurrentManifest!.TileCount);
        var second = Tiles(5).Select(tile => new BRouterTile(tile.SouthLatitude + 5,
            tile.WestLongitude)).ToArray();
        await store.ReplaceAsync(second, RoutingTileSource.WorldManualDownload, null, _ => Task.CompletedTask);
        Assert.Equal(5, store.CurrentManifest!.TileCount);
        Assert.Equal(5, Directory.EnumerateFiles(store.CurrentSegmentsDirectory, "*.rd5").Count());
        Assert.False(Directory.Exists(Path.Combine(root.PathValue, "old")));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(65)]
    public async Task FailedTile_DoesNotReplaceOrDeleteCurrent(int failedIndex)
    {
        using var root = new TestDirectory();
        var handler = new TileHandler();
        using var client = new HttpClient(handler);
        var store = new RoutingDataStore(root.PathValue, client);
        var original = await store.ReplaceAsync(Tiles(2), RoutingTileSource.WorldManualDownload,
            null, _ => Task.CompletedTask);
        var requested = Tiles(67);
        handler.FailingName = requested[failedIndex].FileName;
        await Assert.ThrowsAnyAsync<Exception>(() => store.ReplaceAsync(requested,
            RoutingTileSource.ChinaDownload, "source-hash", _ => Task.CompletedTask));
        Assert.Equal(original.DatasetId, store.CurrentManifest!.DatasetId);
        Assert.Equal(2, Directory.EnumerateFiles(store.CurrentSegmentsDirectory, "*.rd5").Count());
        Assert.False(Directory.Exists(Path.Combine(root.PathValue, "old")));
    }

    [Fact]
    public async Task PostSwitchSmokeFailure_RestoresOldDataset()
    {
        using var root = new TestDirectory();
        using var client = new HttpClient(new TileHandler());
        var store = new RoutingDataStore(root.PathValue, client);
        var first = await store.ReplaceAsync(Tiles(2), RoutingTileSource.WorldManualDownload,
            null, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceAsync(Tiles(3),
            RoutingTileSource.ChinaDownload, "source-hash",
            _ => throw new InvalidOperationException("BRouter Smoke 失败")));
        Assert.Equal(first.DatasetId, store.CurrentManifest!.DatasetId);
        Assert.False(Directory.Exists(Path.Combine(root.PathValue, "old")));
    }

    [Fact]
    public async Task CrashDuringSwitch_RecoversOldCurrent()
    {
        using var root = new TestDirectory();
        using var client = new HttpClient(new TileHandler());
        var store = new RoutingDataStore(root.PathValue, client);
        var first = await store.ReplaceAsync(Tiles(2), RoutingTileSource.WorldManualDownload,
            null, _ => Task.CompletedTask);
        Directory.Move(store.CurrentSegmentsDirectory,
            Path.Combine(root.PathValue, "old"));
        File.WriteAllText(Path.Combine(root.PathValue, "routing-data-state.json"), "Switching");
        store.Recover();
        Assert.Equal(first.DatasetId, store.CurrentManifest!.DatasetId);
        Assert.False(Directory.Exists(Path.Combine(root.PathValue, "old")));
    }

    [Fact]
    public async Task InsufficientSpace_DoesNotStartUpdateOrDeleteCurrent()
    {
        using var root = new TestDirectory();
        using var client = new HttpClient(new TileHandler());
        var store = new RoutingDataStore(root.PathValue, client,
            availableSpaceBytes: () => 100L * 1024 * 1024);
        var original = await store.ReplaceAsync(Tiles(1), RoutingTileSource.WorldManualDownload,
            null, _ => Task.CompletedTask);
        var noSpace = new RoutingDataStore(root.PathValue, client, availableSpaceBytes: () => 0);
        var error = await Assert.ThrowsAsync<IOException>(() => noSpace.ReplaceAsync(Tiles(2),
            RoutingTileSource.WorldManualDownload, null, _ => Task.CompletedTask));
        Assert.Contains("磁盘空间不足", error.Message);
        Assert.Equal(original.DatasetId, noSpace.CurrentManifest!.DatasetId);
        Assert.False(Directory.Exists(store.StagingDirectory));
    }

    [Fact]
    public async Task Cancellation_DoesNotReplaceOrDeleteCurrent()
    {
        using var root = new TestDirectory();
        var handler = new TileHandler();
        using var client = new HttpClient(handler);
        var store = new RoutingDataStore(root.PathValue, client);
        var original = await store.ReplaceAsync(Tiles(1), RoutingTileSource.WorldManualDownload,
            null, _ => Task.CompletedTask);
        handler.BlockedName = Tiles(2)[1].FileName;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReplaceAsync(Tiles(2),
            RoutingTileSource.ChinaDownload, "source-hash", _ => Task.CompletedTask,
            cancellation.Token));
        Assert.Equal(original.DatasetId, store.CurrentManifest!.DatasetId);
        Assert.False(Directory.Exists(Path.Combine(root.PathValue, "old")));
        Assert.False(Directory.Exists(store.StagingDirectory));
    }

    [Fact]
    public async Task Import_AddsThenAppends_AndChangesDatasetId()
    {
        using var root = new TestDirectory();
        var store = new RoutingDataStore(root.PathValue);
        var first = CreateImportFiles(root.PathValue, 3);
        var added = await store.ImportAsync(first, new FakeValidator(), _ => Task.CompletedTask);
        Assert.Equal(3, added.Added);
        Assert.Equal(3, added.Manifest.TileCount);
        Assert.All(added.Manifest.Tiles, tile => Assert.Equal(RoutingTileSource.ImportedFile, tile.Source));
        var second = CreateImportFiles(root.PathValue, 2, 3);
        var appended = await store.ImportAsync(second, new FakeValidator(), _ => Task.CompletedTask);
        Assert.Equal(5, appended.Manifest.TileCount);
        Assert.NotEqual(added.Manifest.DatasetId, appended.Manifest.DatasetId);
        Assert.Equal(5, Directory.EnumerateFiles(store.CurrentSegmentsDirectory, "*.rd5").Count());
        Assert.False(Directory.Exists(Path.Combine(root.PathValue, "old")));
    }

    [Fact]
    public async Task Import_SameNameKeepAndReplace_PreserveSourceFile()
    {
        using var root = new TestDirectory();
        var store = new RoutingDataStore(root.PathValue);
        var originalFile = CreateImportFiles(root.PathValue, 1)[0];
        var original = await store.ImportAsync([originalFile], new FakeValidator(), _ => Task.CompletedTask);
        var alternate = Path.Combine(root.PathValue, "other", "E0_N0.rd5");
        Directory.CreateDirectory(Path.GetDirectoryName(alternate)!);
        File.WriteAllBytes(alternate, Enumerable.Repeat((byte)7, 256).ToArray());
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(alternate)));
        var kept = await store.ImportAsync([new RoutingImportRequest(alternate,
            RoutingImportAction.Keep)], new FakeValidator(), _ => Task.CompletedTask);
        Assert.Equal(original.Manifest.DatasetId, kept.Manifest.DatasetId);
        Assert.Equal(original.Manifest.Tiles[0].Sha256, kept.Manifest.Tiles[0].Sha256);
        var replaced = await store.ImportAsync([new RoutingImportRequest(alternate,
            RoutingImportAction.Replace)], new FakeValidator(), _ => Task.CompletedTask);
        Assert.Equal(1, replaced.Replaced);
        Assert.Equal(1, replaced.Manifest.TileCount);
        Assert.Equal(sourceHash, replaced.Manifest.Tiles[0].Sha256);
        Assert.NotEqual(original.Manifest.DatasetId, replaced.Manifest.DatasetId);
        Assert.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(alternate))));
    }

    [Fact]
    public async Task Import_FifthCorruptFile_RollsBackAllChanges()
    {
        using var root = new TestDirectory();
        var store = new RoutingDataStore(root.PathValue);
        var original = await store.ImportAsync(CreateImportFiles(root.PathValue, 1),
            new FakeValidator(), _ => Task.CompletedTask);
        var incoming = CreateImportFiles(root.PathValue, 5, 1);
        File.WriteAllBytes(incoming[4].SourcePath, Enumerable.Repeat((byte)0xFF, 256).ToArray());
        var before = HashCurrent(store.CurrentSegmentsDirectory);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(incoming,
            new FakeValidator(), _ => Task.CompletedTask));
        Assert.Equal(before, HashCurrent(store.CurrentSegmentsDirectory));
        Assert.Equal(original.Manifest.DatasetId, store.CurrentManifest!.DatasetId);
        Assert.False(Directory.Exists(store.StagingDirectory));
    }

    [Fact]
    public async Task Import_ExistingFileHashMismatch_RejectsBeforeCommit()
    {
        using var root = new TestDirectory();
        var store = new RoutingDataStore(root.PathValue);
        var initial = await store.ImportAsync(CreateImportFiles(root.PathValue, 1),
            new FakeValidator(), _ => Task.CompletedTask);
        var oldFile = Path.Combine(store.CurrentSegmentsDirectory, "E0_N0.rd5");
        File.WriteAllBytes(oldFile, Enumerable.Repeat((byte)9, 256).ToArray());
        var before = HashCurrent(store.CurrentSegmentsDirectory);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(
            CreateImportFiles(root.PathValue, 1, 1), new FakeValidator(),
            _ => Task.CompletedTask));

        Assert.Contains("哈希不符", error.Message);
        Assert.Equal(initial.Manifest.DatasetId, store.CurrentManifest!.DatasetId);
        Assert.Equal(before, HashCurrent(store.CurrentSegmentsDirectory));
        Assert.False(Directory.Exists(store.StagingDirectory));
    }

    [Fact]
    public async Task Import_TwentyFiles_ThenRebuildMissingManifest_WithoutHttp()
    {
        using var root = new TestDirectory();
        var handler = new TileHandler();
        using var client = new HttpClient(handler);
        var store = new RoutingDataStore(root.PathValue, client);
        var result = await store.ImportAsync(CreateImportFiles(root.PathValue, 20),
            new FakeValidator(), _ => Task.CompletedTask);
        Assert.Equal(20, result.Manifest.TileCount);
        File.Delete(Path.Combine(store.CurrentSegmentsDirectory, "routing-data-manifest.json"));
        var scanned = await store.ScanCurrentAsync(new FakeValidator());
        Assert.True(scanned.Rebuilt);
        Assert.Equal(20, scanned.Manifest!.TileCount);
        Assert.All(scanned.Manifest.Tiles,
            tile => Assert.Equal(RoutingTileSource.ExternalFileDetected, tile.Source));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Scan_QuarantinesInvalidUnknownFile_WithoutDeletingIt()
    {
        using var root = new TestDirectory();
        var store = new RoutingDataStore(root.PathValue);
        Directory.CreateDirectory(store.CurrentSegmentsDirectory);
        File.WriteAllBytes(Path.Combine(store.CurrentSegmentsDirectory, "E0_N0.rd5"), [0, 1]);
        var scan = await store.ScanCurrentAsync(new FakeValidator());
        Assert.Single(scan.InvalidTiles);
        Assert.Empty(Directory.EnumerateFiles(store.CurrentSegmentsDirectory, "*.rd5"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root.PathValue, "invalid")));
    }

    [Fact]
    public async Task LegacyMigration_UsesHashesAndRemovesOnlyOldDataset()
    {
        using var root = new TestDirectory();
        var legacyRoot = Path.Combine(root.PathValue, "legacy");
        var legacyCurrent = Path.Combine(legacyRoot, "segments4-current");
        Directory.CreateDirectory(legacyCurrent);
        var source = Path.Combine(legacyCurrent, "E0_N0.rd5");
        File.WriteAllBytes(source, Enumerable.Repeat((byte)3, 256).ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var legacyManifest = new
        {
            ChinaSourceManifestHash = "source-hash",
            Tiles = new[] { new { FileName = "E0_N0.rd5", ByteLength = 256, Sha256 = hash } }
        };
        File.WriteAllText(Path.Combine(legacyCurrent, "routing-data-manifest.json"),
            JsonSerializer.Serialize(legacyManifest));
        var store = new RoutingDataStore(Path.Combine(root.PathValue, "new"));
        Assert.True(await store.MigrateLegacyAsync(legacyRoot, _ => Task.CompletedTask));
        Assert.Equal(hash, store.CurrentManifest!.Tiles.Single().Sha256);
        Assert.Equal(RoutingTileSource.ChinaDownload, store.CurrentManifest.Tiles.Single().Source);
        Assert.False(Directory.Exists(legacyCurrent));
        Assert.True(File.Exists(Path.Combine(store.CurrentSegmentsDirectory, "E0_N0.rd5")));
    }

    private static RoutingImportRequest[] CreateImportFiles(string root, int count, int offset = 0)
    {
        var source = Path.Combine(root, "sources");
        Directory.CreateDirectory(source);
        return Enumerable.Range(offset, count).Select(index =>
        {
            var tile = new BRouterTile(index / 36 * 5, index % 36 * 5);
            var file = Path.Combine(source, tile.FileName);
            File.WriteAllBytes(file, Enumerable.Repeat((byte)(index + 1), 256).ToArray());
            return new RoutingImportRequest(file, RoutingImportAction.Add);
        }).ToArray();
    }

    private static string HashCurrent(string directory) => string.Join(";",
        Directory.EnumerateFiles(directory, "*.rd5").OrderBy(Path.GetFileName).Select(file =>
            $"{Path.GetFileName(file)}={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}"));

    private static BRouterTile[] Tiles(int count) =>
        Enumerable.Range(0, count).Select(index =>
            new BRouterTile(index / 36 * 5, index % 36 * 5 - 180)).ToArray();
}
