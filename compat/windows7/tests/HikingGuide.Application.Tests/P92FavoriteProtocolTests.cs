using HikingGuide.Application.Maps;
namespace HikingGuide.Application.Tests;
public class P92FavoriteProtocolTests
{
    [Theory]
    [InlineData("FavoriteHoverEntered")][InlineData("FavoriteHoverLeft")][InlineData("FavoriteDragStarted")]
    [InlineData("FavoriteDragCompleted")][InlineData("FavoriteDragCanceled")][InlineData("FavoriteLinkRequested")]
    public void GlobalFavoriteEventsRetainIdentityRevisionAndRequestWithoutProject(string type)
    {
        var id = Guid.NewGuid(); var json = System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = MapProtocol.Version, type, favoriteId = id, revision = 25, requestId = 37, latitude = 23.1, longitude = 113.2 });
        var message = MapProtocol.TryParseEvent(json); Assert.NotNull(message); Assert.Null(message.ProjectId); Assert.Equal(id, message.FavoriteId); Assert.Equal(25, message.Revision); Assert.Equal(37, message.RequestId);
    }
}
