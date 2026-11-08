using System.IO.Compression;
using System.Xml.Linq;
using HikingGuide.Core.Favorites;
using HikingGuide.Infrastructure.Favorites;
using HikingGuide.Infrastructure.TrackFiles;

namespace HikingGuide.Infrastructure.Tests;

public sealed class FavoriteKmzTests : IDisposable
{
    private readonly string _root = Path.Combine("F:\\Codex\\Temp", "HikingGuide-P91-KmzTests", Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private static FavoriteMediaInput Image => new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII="), "中文原图.png", "image/png", ".png", 1, 1);
    private async Task<(FavoritesRepository Repo, FavoritePoint[] Points, FavoriteCollection Collection)> Seed()
    {
        var repo = new FavoritesRepository(new FavoritesPathProvider(_root)); await repo.InitializeAsync(); var now = DateTimeOffset.UtcNow;
        var photo = await repo.SaveCollectionAsync(new(Guid.NewGuid(), "摄影点", 1, now, now)); var camp = await repo.SaveCollectionAsync(new(Guid.NewGuid(), "营地", 2, now, now));
        var points = Enumerable.Range(0, 10).Select(i => new FavoritePoint { Name = $"收藏点{i}", Description = "中文 & < > \"\n第二行", Latitude = 23 + i / 1000d, Longitude = 113,
            CollectionId = i < 5 ? photo.Id : camp.Id, ElevationMeters = i == 0 ? 123.45 : null, SourceReference = i == 0 ? "https://example.com/guide?a=1&b=2" : null }).ToArray();
        for (var i = 0; i < points.Length; i++) await repo.SaveAsync(points[i], i < 3 ? [Image] : []);
        var detail = (await repo.GetAsync(points[0].Id))!;
        await repo.SaveAsync(points[0], retained: detail.Attachments.Concat([new FavoriteMediaAttachment(Guid.NewGuid(), points[0].Id, null, "网络照片", null, 1, now, "https://example.com/photo.jpg?a=1&b=2", true)]).ToArray());
        return (repo, points, photo);
    }
    [Theory]
    [InlineData("All", 10, 2)][InlineData("Collection", 5, 1)][InlineData("Multi", 3, 2)][InlineData("Single", 1, 1)]
    public async Task FourScopesContainExactlyExpectedPointsAndFolders(string scope, int count, int folderCount)
    {
        var (repo, points, collection) = await Seed(); var file = Path.Combine(_root, scope + ".kmz");
        IReadOnlyList<Guid>? ids = scope == "Multi" ? [points[0].Id, points[4].Id, points[8].Id] : scope == "Single" ? [points[0].Id] : null;
        var result = await repo.ExportKmzAsync(file, ids, scope == "Collection" ? collection.Id : null);
        Assert.Equal(count, result.PointCount); Assert.Equal(folderCount, result.CollectionCount);
        using var zip = ZipFile.OpenRead(file); using var reader = new StreamReader(zip.GetEntry("doc.kml")!.Open()); var document = XDocument.Parse(reader.ReadToEnd());
        Assert.Equal(count, document.Descendants(Kml + "Placemark").Count()); Assert.Equal(folderCount, document.Descendants(Kml + "Folder").Count());
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".sqlite")); Assert.Single(zip.Entries, e => e.FullName.StartsWith("files/"));
        var imported = await new KmzTrackImporter().ImportAsync(file); Assert.True(imported.IsSuccess); Assert.Equal(count, imported.Waypoints.Count);
        var first = imported.Waypoints.Single(p => p.Name == points[0].Name); Assert.Equal(points[0].Latitude, first.Latitude); Assert.Equal(points[0].Longitude, first.Longitude);
        Assert.Equal(points[0].Description, first.Description); Assert.Equal(points[0].ElevationMeters, first.SourceElevation);
        Assert.Contains(imported.MediaAttachments, a => a.OwnerId == first.Id); Assert.Contains(imported.ExternalMediaReferences, a => a.Url.Contains("photo.jpg"));
        Assert.All(imported.Waypoints.Where(p => p.Name != points[0].Name), p => Assert.Null(p.SourceElevation));
        var html = string.Join("\n", document.Descendants(Kml + "description").Select(d => d.Value));
        Assert.Contains("<img src=\"files/", html); Assert.DoesNotContain("&lt;img", html); Assert.DoesNotContain("href=\"\"", html); Assert.DoesNotContain(repo.Paths.FavoritesRoot, html);
        Assert.Contains("https://example.com/photo.jpg?a=1&amp;b=2", html);
        Assert.Equal(10, (await repo.SearchAsync()).Count); // 导出不写数据库。
    }
    [Fact]
    public async Task CancellationKeepsPreviousTargetAndCleansTemporaryOutput()
    {
        var (repo, _, _) = await Seed(); var file = Path.Combine(_root, "cancel.kmz"); await repo.ExportKmzAsync(file); var original = await File.ReadAllBytesAsync(file);
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.PointsCompleted >= 1) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.ExportKmzAsync(file, overwrite: true, progress: progress, token: cancel.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(file)); Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
    [Fact]
    public async Task BadImageDoesNotReplaceExistingTarget()
    {
        var (repo, points, _) = await Seed(); var file = Path.Combine(_root, "safe.kmz"); await repo.ExportKmzAsync(file); var original = await File.ReadAllBytesAsync(file);
        var blob = (await repo.GetAsync(points[0].Id))!.Blobs[0]; await File.WriteAllBytesAsync(Path.Combine(repo.Paths.FavoritesRoot, blob.StoredRelativePath), [0, 1, 2]);
        await Assert.ThrowsAnyAsync<IOException>(() => repo.ExportKmzAsync(file, overwrite: true));
        Assert.Equal(original, await File.ReadAllBytesAsync(file)); Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
    [Fact]
    public async Task SearchResultsUseSameStableNameOrderAcrossCollectionsAndRestart()
    {
        var (repo, _, collection) = await Seed(); var names = new[] { "船底顶", "白云山", "阿尔山", "b point", "A Point" };
        await repo.SaveManyAsync(names.Select(n => new FavoritePoint { Name = n, Description = "匹配", Latitude = 23, Longitude = 113, CollectionId = collection.Id }).ToArray());
        var expected = new[] { "阿尔山", "白云山", "船底顶", "A Point", "b point" };
        Assert.Equal(expected, (await repo.SearchAsync(query: "匹配")).Select(p => p.Name));
        var reopened = new FavoritesRepository(new FavoritesPathProvider(_root)); await reopened.InitializeAsync();
        Assert.Equal(expected, (await reopened.SearchAsync(query: "匹配")).Select(p => p.Name));
    }
    private sealed class InlineProgress(Action<FavoriteKmzProgress> action) : IProgress<FavoriteKmzProgress> { public void Report(FavoriteKmzProgress value) => action(value); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
