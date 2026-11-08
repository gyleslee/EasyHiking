using System.IO;
using HikingGuide.Application.Maps;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Favorites;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Favorites;

namespace HikingGuide.App;

public partial class App
{
    public FavoriteSelectionService FavoriteSelection { get; } = new();
    public FavoriteMoveService FavoriteMover { get; private set; } = null!;
    public event EventHandler<string>? FavoriteMoveStatus;
    private CancellationTokenSource? _favoriteHoverRequest;
    private void InitializeFavoriteInteractions(MapViewModel map)
    {
        map.ConfigureFavoriteSelection(FavoriteSelection);
        FavoriteMover = new(Favorites,
            () => DemCatalog.Databases.Where(d => d.Status == DemDatabaseStatus.Ready && Directory.Exists(d.RootPath)).Select(d => new FavoriteDemSource(d.Id, d.Name)).ToArray(),
            (id, coordinate, token) => Task.Run(() => DemProvider?.SampleDetailed(id, coordinate, token).ElevationMeters, token));
        map.FavoritesEvent += async (_, e) =>
        {
            try
            {
                if (e.Type is "FavoriteHoverLeft" or "FavoriteDragStarted") { _favoriteHoverRequest?.Cancel(); if (e.Type == "FavoriteDragStarted" && e.FavoriteId is Guid movingId) FavoriteMover.CancelPending(movingId); return; }
                if (e.FavoriteId is not Guid id) return;
                if (e.Type == "FavoriteLinkRequested" && e.Message is { } link) { FavoritesDialogs.OpenExternal(link); return; }
                if (e.Type == "FavoriteDragCanceled") { FavoriteMover.CancelPending(id); NotifyFavoritesChanged(); return; }
                if (e.Type is "FavoriteClicked" or "FavoriteRightClicked") return;
                if (e.Type == "FavoriteHoverEntered")
                {
                    _favoriteHoverRequest?.Cancel(); using var request = new CancellationTokenSource(); _favoriteHoverRequest = request;
                    try
                    {
                        var detail = await Favorites.GetAsync(id, request.Token); if (detail is null) return;
                        var collections = await Favorites.CollectionsAsync(request.Token); byte[]? thumbnail = null;
                        var firstBlob = detail.Attachments.OrderBy(a => a.DisplayOrder).Select(a => detail.Blobs.FirstOrDefault(b => b.Id == a.MediaBlobId)).FirstOrDefault(b => b is not null);
                        if (firstBlob is { } blob)
                        {
                            await FavoriteImageService.ThumbnailAsync(Favorites, blob);
                            thumbnail = await File.ReadAllBytesAsync(Path.Combine(Favorites.Paths.ThumbnailPath, blob.Sha256 + ".png"), request.Token);
                        }
                        request.Token.ThrowIfCancellationRequested(); var p = detail.Point;
                        var links = detail.Attachments.Select(a => a.ExternalUrl).Append(p.SourceReference).Where(s => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https").Distinct().ToArray();
                        await map.ShowFavoriteHoverAsync(e, new { p.Name, p.Description, elevation = p.ElevationMeters, p.Latitude, p.Longitude,
                            collection = collections.FirstOrDefault(c => c.Id == p.CollectionId)?.Name ?? "未分类", imageCount = detail.Attachments.Count,
                            thumbnailWidth = firstBlob?.Width, thumbnailHeight = firstBlob?.Height, links }, thumbnail);
                    }
                    finally { if (ReferenceEquals(_favoriteHoverRequest, request)) _favoriteHoverRequest = null; }
                }
                if (e.Type == "FavoriteDragCompleted" && e.Latitude is double latitude && e.Longitude is double longitude)
                {
                    FavoriteMoveStatus?.Invoke(this, "正在更新收藏点海拔…");
                    try
                    {
                        var moved = await FavoriteMover.MoveAsync(id, new GeoCoordinate(latitude, longitude));
                        await map.SendFavoriteCommandAsync("EndFavoriteMove", new { favoriteId = id, requestId = e.RequestId, moved.Point.Latitude, moved.Point.Longitude });
                        if (FavoriteSelection.SelectedFavoritePointId == id) FavoriteSelection.SelectFavorite(moved.Point);
                        if (moved.Warning is not null) _loggingService?.Warning("[Favorites] " + moved.Warning);
                        NotifyFavoritesChanged(); FavoriteMoveStatus?.Invoke(this, moved.Warning ?? "收藏点坐标和海拔已更新。");
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        var point = (await Favorites.GetAsync(id))?.Point;
                        if (point is not null) await map.SendFavoriteCommandAsync("EndFavoriteMove", new { favoriteId = id, requestId = e.RequestId, point.Latitude, point.Longitude });
                        NotifyFavoritesChanged(); throw new InvalidOperationException("收藏点移动失败，已恢复保存的位置：" + error.Message, error);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { _loggingService?.Error(error, "[Favorites] 地图收藏点操作失败"); FavoriteMoveStatus?.Invoke(this, error.Message); }
        };
    }
}
