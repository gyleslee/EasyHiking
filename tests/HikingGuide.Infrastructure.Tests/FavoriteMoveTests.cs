using HikingGuide.Core.Favorites;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Favorites;
using System.Security.Cryptography;

namespace HikingGuide.Infrastructure.Tests;
public class FavoriteMoveTests
{
    private static async Task<(FavoritesRepository Repo, FavoritePoint Point)> Seed()
    {
        var repo = new FavoritesRepository(new FavoritesPathProvider(Path.Combine("F:\\Codex\\Temp\\HikingGuide-P92-tests", Guid.NewGuid().ToString("N")))); await repo.InitializeAsync();
        await repo.SaveAsync(new FavoritePoint { Name = "甲收藏点", Description = "原始说明", Latitude = 23, Longitude = 113, ElevationMeters = 987, ElevationSourceKind = FavoriteElevationSourceKind.CopiedGps });
        return (repo, (await repo.SearchAsync()).Single());
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task OriginalDemFirstOtherwiseStableDisplayOrder(bool previousDem)
    {
        var (repo, point) = await Seed(); var a = new FavoriteDemSource(Guid.NewGuid(), "显示第一套"); var b = new FavoriteDemSource(Guid.NewGuid(), "原来源");
        if (previousDem) { await repo.SaveAsync(point with { ElevationSourceKind = FavoriteElevationSourceKind.CopiedDem, ElevationSourceReference = b.Id + "|旧快照" }); point = (await repo.GetAsync(point.Id))!.Point; }
        var calls = new List<Guid>(); var mover = new FavoriteMoveService(repo, () => [a, b], (id, c, t) => { calls.Add(id); return Task.FromResult<double?>(id == a.Id ? 111 : 222); });
        var result = await mover.MoveAsync(point.Id, new(24.1234567890123, 114.9876543210987));
        Assert.Equal(previousDem ? b.Id : a.Id, calls.Single()); Assert.Equal(previousDem ? 222d : 111d, result.Point.ElevationMeters);
        Assert.Equal(point.Name, result.Point.Name); Assert.Equal(point.Description, result.Point.Description); Assert.Equal(point.CreatedAt, result.Point.CreatedAt);
        var reopened = new FavoritesRepository(new FavoritesPathProvider(Path.GetDirectoryName(repo.Paths.FavoritesRoot)!)); var persisted = (await reopened.GetAsync(point.Id))!.Point;
        Assert.Equal(result.Point, persisted); Assert.Equal(24.1234567890123, persisted.Latitude);
    }
    [Theory][InlineData("missing")][InlineData("exception")][InlineData("timeout")]
    public async Task MissingFailureOrTimeoutClearsOldHeightButCommitsCoordinate(string mode)
    {
        var (repo, p) = await Seed(); var db = new FavoriteDemSource(Guid.NewGuid(), "测试DEM");
        var mover = new FavoriteMoveService(repo, () => [db], async (_, _, _) => { if (mode == "exception") throw new IOException("故障"); if (mode == "timeout") await Task.Delay(200); return null; }, TimeSpan.FromMilliseconds(20));
        var result = await mover.MoveAsync(p.Id, new(25, 115)); Assert.Equal(25, result.Point.Latitude); Assert.Null(result.Point.ElevationMeters);
        Assert.Equal(FavoriteElevationSourceKind.Unknown, result.Point.ElevationSourceKind); Assert.Null(result.Point.ElevationSourceReference); Assert.NotNull(result.Warning);
    }
    [Fact] public async Task NoCoverageAtPreferredSourceFallsBackToStableFirstCoveringSource()
    {
        var (repo, p) = await Seed(); var a = new FavoriteDemSource(Guid.NewGuid(), "显示第一套"); var b = new FavoriteDemSource(Guid.NewGuid(), "原来源");
        await repo.SaveAsync(p with { ElevationSourceKind = FavoriteElevationSourceKind.CopiedDem, ElevationSourceReference = b.Id + "|原来源" });
        var mover = new FavoriteMoveService(repo, () => [a, b], (id, _, _) => Task.FromResult<double?>(id == a.Id ? 5 : null));
        var result = await mover.MoveAsync(p.Id, new(25, 115)); Assert.StartsWith(a.Id.ToString(), result.Point.ElevationSourceReference); Assert.Equal(5, result.Point.ElevationMeters);
    }
    [Fact] public async Task LateFirstDemResultNeverOverwritesSecondMove()
    {
        var (repo, p) = await Seed(); var started = new TaskCompletionSource(); var delayed = new TaskCompletionSource<double?>();
        var mover = new FavoriteMoveService(repo, () => [new(Guid.NewGuid(), "DEM")], (_, c, _) => { if (c.Latitude == 24) { started.SetResult(); return delayed.Task; } return Task.FromResult<double?>(333); });
        var first = mover.MoveAsync(p.Id, new(24, 114)); await started.Task; var second = await mover.MoveAsync(p.Id, new(25, 115)); delayed.SetResult(111);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.Equal(333, second.Point.ElevationMeters); Assert.Equal(25, (await repo.GetAsync(p.Id))!.Point.Latitude);
    }
    [Fact] public async Task CancelDoesNotWriteAndSelectionDoesNotWriteDatabase()
    {
        var (repo, p) = await Seed(); var hash = SHA256.HashData(await File.ReadAllBytesAsync(repo.Paths.DatabasePath));
        var selection = new FavoriteSelectionService(); selection.SelectFavorite(p); selection.ClearSelection();
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(repo.Paths.DatabasePath)));
        var mover = new FavoriteMoveService(repo, () => [], (_, _, _) => Task.FromResult<double?>(null)); using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mover.MoveAsync(p.Id, new(24, 114), token.Token)); Assert.Equal(p, (await repo.GetAsync(p.Id))!.Point);
    }
    [Fact] public async Task EditingDuringSamplingIsNotOverwritten()
    {
        var (repo, p) = await Seed(); var started = new TaskCompletionSource(); var delayed = new TaskCompletionSource<double?>();
        var mover = new FavoriteMoveService(repo, () => [new(Guid.NewGuid(), "DEM")], (_, _, _) => { started.SetResult(); return delayed.Task; });
        var move = mover.MoveAsync(p.Id, new(24, 114)); await started.Task; await repo.SaveAsync(p with { Description = "用户新说明" }); delayed.SetResult(10);
        await Assert.ThrowsAsync<InvalidOperationException>(() => move); Assert.Equal("用户新说明", (await repo.GetAsync(p.Id))!.Point.Description); Assert.Equal(23, (await repo.GetAsync(p.Id))!.Point.Latitude);
    }
    [Fact] public async Task MovePreservesImagesDescriptionCollectionSourceLinksAndCreationTime()
    {
        var (repo, p) = await Seed();
        var image = new FavoriteMediaInput(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII="), "原图.png", "image/png", ".png", 1, 1);
        await repo.SaveAsync(p with { SourceReference = "https://example.com/link" }, [image]); var before = (await repo.GetAsync(p.Id))!;
        var mover = new FavoriteMoveService(repo, () => [], (_, _, _) => Task.FromResult<double?>(null)); await mover.MoveAsync(p.Id, new(24, 114)); var after = (await repo.GetAsync(p.Id))!;
        Assert.Equal(before.Attachments, after.Attachments); Assert.Equal(before.Blobs, after.Blobs);
        Assert.Equal(before.Point.Description, after.Point.Description); Assert.Equal(before.Point.CollectionId, after.Point.CollectionId); Assert.Equal(before.Point.Name, after.Point.Name);
        Assert.Equal(before.Point.SourceReference, after.Point.SourceReference); Assert.Equal(before.Point.CreatedAt, after.Point.CreatedAt);
    }
}
