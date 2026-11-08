using System.IO.Compression;
using HikingGuide.Core.Favorites;
using HikingGuide.Infrastructure.Favorites;
using Microsoft.Data.Sqlite;

namespace HikingGuide.Infrastructure.Tests;

public sealed class FavoritesRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine("F:\\Codex\\Temp", "HikingGuide-Favorites-Tests", Guid.NewGuid().ToString("N"));
    private async Task<FavoritesRepository> Open(string name)
    { var repository = new FavoritesRepository(new FavoritesPathProvider(Path.Combine(_root, name))); await repository.InitializeAsync(); return repository; }
    private static FavoriteMediaInput Image => new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII="), "中文图片.png", "image/png", ".png", 1, 1);
    [Fact]
    public async Task CreatesSqliteAndPersistsAcrossRestart()
    {
        var repository = await Open("one"); Assert.True(repository.TrigramAvailable); Assert.NotEmpty(repository.SqliteVersion);
        var point = new FavoritePoint { Name = "船底顶备用营地", Description = "中文\n多行描述", Latitude = 23, Longitude = 113, ElevationMeters = 120 };
        await repository.SaveAsync(point); var reopened = await Open("one");
        Assert.Equal(repository.LibraryId, reopened.LibraryId); Assert.Equal(point.Name, (await reopened.GetAsync(point.Id))!.Point.Name);
        Assert.Single(await reopened.SearchAsync()); Assert.True(File.Exists(repository.Paths.DatabasePath));
    }
    [Theory]
    [InlineData("底顶")]
    [InlineData("备用")]
    [InlineData("营地")]
    [InlineData("船底顶")]
    [InlineData("中文描述")]
    public async Task SearchAlwaysSearchesAllCollectionsAndChineseSubstrings(string query)
    {
        var repository = await Open("one"); var collection = await repository.SaveCollectionAsync(new(Guid.NewGuid(), "水源", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await repository.SaveAsync(new() { Name = "船底顶备用营地", Description = "这是中文描述", Latitude = 23, Longitude = 113 });
        Assert.Single(await repository.SearchAsync(collection.Id, query)); Assert.Empty(await repository.SearchAsync(collection.Id));
    }
    [Fact]
    public async Task CollectionRenameDeleteMovesPointsSafelyAndProtectsSystem()
    {
        var repository = await Open("one"); var collection = await repository.SaveCollectionAsync(new(Guid.NewGuid(), "摄影点", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await repository.SaveCollectionAsync(collection with { Name = "观景" });
        var point = new FavoritePoint { Name = "点", CollectionId = collection.Id, Latitude = 23, Longitude = 113 }; await repository.SaveAsync(point);
        await repository.DeleteCollectionAsync(collection.Id); Assert.Equal(FavoriteDefaults.UnclassifiedId, (await repository.GetAsync(point.Id))!.Point.CollectionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteCollectionAsync(FavoriteDefaults.UnclassifiedId));
    }
    [Fact]
    public async Task DuplicateImagesHaveOneBlobTwoIndependentAttachmentsAndExactOriginalBytes()
    {
        var repository = await Open("one"); var first = new FavoritePoint { Name = "一", Latitude = 23, Longitude = 113 }; var second = first with { Id = Guid.NewGuid(), Name = "二" };
        await repository.SaveAsync(first, [Image]); await repository.SaveAsync(second, [Image]);
        var a = (await repository.GetAsync(first.Id))!; var b = (await repository.GetAsync(second.Id))!;
        Assert.Equal(a.Blobs.Single().Id, b.Blobs.Single().Id); Assert.NotEqual(a.Attachments.Single().Id, b.Attachments.Single().Id);
        Assert.Equal(Image.Bytes, await repository.ReadOriginalAsync(a.Blobs.Single()));
        await repository.DeleteAsync([first.Id]); Assert.Equal(Image.Bytes, await repository.ReadOriginalAsync((await repository.GetAsync(second.Id))!.Blobs.Single()));
    }
    [Theory]
    [InlineData(FavoriteConflictPolicy.Skip, 1)]
    [InlineData(FavoriteConflictPolicy.Overwrite, 1)]
    [InlineData(FavoriteConflictPolicy.KeepBoth, 2)]
    public async Task PackageRoundTripAndAllConflictPoliciesKeepMedia(FavoriteConflictPolicy policy, int count)
    {
        var repository = await Open("one"); var point = new FavoritePoint { Name = "中文点", Description = "说明", Latitude = 23, Longitude = 113, ElevationMeters = 120 };
        await repository.SaveAsync(point, [Image]); var package = Path.Combine(_root, "中文.hgfav"); await repository.ExportAsync(package);
        var imported = await Open("two"); await imported.ImportAsync(package); await imported.ImportAsync(package, policy);
        Assert.Equal(count, (await imported.SearchAsync()).Count);
        var detail = (await imported.GetAsync(point.Id))!; Assert.Equal(point.Description, detail.Point.Description); Assert.Equal(Image.Bytes, await imported.ReadOriginalAsync(detail.Blobs.Single()));
    }
    [Fact]
    public async Task ScopedExportIncludesOnlySelectedPointAndRequiredMedia()
    {
        var repository = await Open("one"); var first = new FavoritePoint { Name = "一", Latitude = 23, Longitude = 113 };
        await repository.SaveAsync(first, [Image]); await repository.SaveAsync(first with { Id = Guid.NewGuid(), Name = "二" });
        var package = Path.Combine(_root, "selected.hgfav"); await repository.ExportAsync(package, [first.Id]);
        var target = await Open("two"); await target.ImportAsync(package); Assert.Equal(first.Id, (await target.SearchAsync()).Single().Id);
    }
    [Fact]
    public async Task BadPackagePathRejectedWithoutAnyDatabaseChange()
    {
        var repository = await Open("one"); var point = new FavoritePoint { Name = "保留", Latitude = 23, Longitude = 113 }; await repository.SaveAsync(point);
        var package = Path.Combine(_root, "bad.hgfav"); using (var zip = ZipFile.Open(package, ZipArchiveMode.Create)) zip.CreateEntry("../outside.txt");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ImportAsync(package)); Assert.Single(await repository.SearchAsync());
    }
    [Fact]
    public async Task BatchMoveFailureRollsBackEarlierPoints()
    {
        var repository = await Open("one"); var collection = await repository.SaveCollectionAsync(new(Guid.NewGuid(), "水源", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var point = new FavoritePoint { Name = "点", Latitude = 23, Longitude = 113 }; await repository.SaveAsync(point);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.MoveAsync([point.Id, Guid.NewGuid()], collection.Id));
        Assert.Equal(FavoriteDefaults.UnclassifiedId, (await repository.GetAsync(point.Id))!.Point.CollectionId);
    }
    [Fact]
    public async Task UnknownSchemaRefusedWithoutModification()
    {
        var repository = await Open("one"); using (var db = new SqliteConnection($"Data Source={repository.Paths.DatabasePath};Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => Open("one"));
    }
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task LargeLibraryChineseSearchIsCorrectAndBounded(int count)
    {
        var repository = await Open("large");
        var points = Enumerable.Range(0, count).Select(i => new FavoritePoint { Name = $"船底顶备用营地{i}", Latitude = 23, Longitude = 113 }).ToArray();
        await repository.SaveManyAsync(points); var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(count, (await repository.SearchAsync(query: "营地")).Count);
        Assert.Equal(count, (await repository.SearchAsync(query: "备用营")).Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"两次搜索耗时{watch.Elapsed}");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkerCopyKeepsOriginalAndOwnsMediaIndependently(bool guide)
    {
        var repository = await Open("copy"); var project = new HikingGuide.Core.Models.HikingProject();
        HikingGuide.Core.Models.MapMarkerBase marker = guide ? new HikingGuide.Core.Models.GuideNode { Name = "攻略节点", OriginalCoordinate = new(23, 113), SnappedCoordinate = new(24, 114), SourceElevation = 123 } :
            new HikingGuide.Core.Models.Waypoint { Name = "普通标注", Latitude = 23, Longitude = 113, SourceElevation = 123 };
        if (marker is HikingGuide.Core.Models.GuideNode node) project.GuideNodes.Add(node); else project.Waypoints.Add((HikingGuide.Core.Models.Waypoint)marker);
        var blob = new HikingGuide.Core.Models.MediaBlob { PendingBytes = Image.Bytes, ByteLength = Image.Bytes.Length, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Image.Bytes)),
            MimeType = Image.MimeType, FileExtension = Image.Extension, Width = 1, Height = 1 };
        project.MediaBlobs.Add(blob); project.MediaAttachments.Add(new() { OwnerId = marker.Id, OwnerType = guide ? HikingGuide.Core.Models.MediaOwnerType.GuideNode : HikingGuide.Core.Models.MediaOwnerType.Waypoint, MediaBlobId = blob.Id });
        var originalId = marker.Id; var dirty = project.IsDirty;
        var draft = await new FavoriteMarkerCopyService().PrepareAsync(project, marker); await repository.SaveAsync(draft.Point, draft.Images, draft.OnlineReferences);
        Assert.NotEqual(originalId, draft.Point.Id); Assert.Equal(originalId, marker.Id); Assert.Equal(dirty, project.IsDirty); Assert.Equal(guide ? 24 : 23, draft.Point.Latitude);
        if (guide) Assert.Equal(23, draft.OriginalCoordinate!.Value.Latitude);
        project.MediaBlobs.Clear(); project.MediaAttachments.Clear();
        Assert.Equal(Image.Bytes, await repository.ReadOriginalAsync((await repository.GetAsync(draft.Point.Id))!.Blobs.Single()));
    }
    [Fact]
    public async Task ImageSaveFailureAfterCommitRestoresPreviousDatabase()
    {
        var repository = await Open("save-failure");
        var point = new FavoritePoint { Name = "保留原名称", Latitude = 23, Longitude = 113 };
        await repository.SaveAsync(point);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Image.Bytes));
        Directory.CreateDirectory(Path.Combine(repository.Paths.MediaOriginalsPath, hash + Image.Extension));
        await Assert.ThrowsAnyAsync<IOException>(() => repository.SaveAsync(point with { Name = "不应保存" }, [Image]));
        var restored = (await repository.GetAsync(point.Id))!;
        Assert.Equal(point.Name, restored.Point.Name); Assert.Empty(restored.Attachments);
        Assert.Single(await repository.SearchAsync());
    }
    [Fact]
    public async Task ImportMediaFailureRestoresEntirePreviousLibrary()
    {
        var source = await Open("import-source"); var photo = new FavoritePoint { Name = "外部照片点", Latitude = 23, Longitude = 113 };
        await source.SaveAsync(photo, [Image]); var package = Path.Combine(_root, "failure.hgfav"); await source.ExportAsync(package);
        var target = await Open("import-target"); var original = new FavoritePoint { Name = "原库内容", Latitude = 24, Longitude = 114 };
        await target.SaveAsync(original);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Image.Bytes));
        Directory.CreateDirectory(Path.Combine(target.Paths.MediaOriginalsPath, hash + Image.Extension));
        await Assert.ThrowsAnyAsync<IOException>(() => target.ImportAsync(package));
        Assert.Equal(original.Id, (await target.SearchAsync()).Single().Id);
        Assert.Equal(original.Name, (await target.GetAsync(original.Id))!.Point.Name);
    }
    [Fact]
    public async Task PendingMediaSurvivesRestartAndRecoversExactOriginalBytes()
    {
        var repository = await Open("recovery"); var point = new FavoritePoint { Name = "恢复照片", Latitude = 23, Longitude = 113 };
        await repository.SaveAsync(point, [Image]); var blob = (await repository.GetAsync(point.Id))!.Blobs.Single();
        var pending = Path.Combine(repository.Paths.TempPath, "crash-test.media");
        File.Move(Path.Combine(repository.Paths.FavoritesRoot, blob.StoredRelativePath), pending);
        using (var db = new SqliteConnection($"Data Source={repository.Paths.DatabasePath};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "INSERT INTO PendingMedia VALUES($source,$destination)";
            command.Parameters.AddWithValue("$source", pending); command.Parameters.AddWithValue("$destination", blob.StoredRelativePath); command.ExecuteNonQuery();
        }
        var reopened = await Open("recovery"); Assert.False(File.Exists(pending));
        Assert.Equal(Image.Bytes, await reopened.ReadOriginalAsync((await reopened.GetAsync(point.Id))!.Blobs.Single()));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportPreservesEmptyCollectionsForWholeLibraryAndCollectionScope(bool collectionOnly)
    {
        var source = await Open("empty-source"); var now = DateTimeOffset.UtcNow;
        var empty = await source.SaveCollectionAsync(new(Guid.NewGuid(), "空收藏夹", 1, now, now));
        var other = await source.SaveCollectionAsync(new(Guid.NewGuid(), "另一空收藏夹", 2, now, now));
        var package = Path.Combine(_root, "empty.hgfav"); await source.ExportAsync(package, collectionId: collectionOnly ? empty.Id : null);
        var target = await Open("empty-target"); await target.ImportAsync(package);
        var folders = await target.CollectionsAsync(); Assert.Contains(folders, c => c.Id == empty.Id);
        Assert.Equal(!collectionOnly, folders.Any(c => c.Id == other.Id)); Assert.Empty(await target.SearchAsync());
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
