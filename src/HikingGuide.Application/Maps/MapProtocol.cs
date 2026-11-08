using System.Text.Json;

namespace HikingGuide.Application.Maps;

public static class MapProtocol
{
    public const int Version = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializeCommand(
        string type,
        object? payload = null,
        Guid? projectId = null,
        long revision = 0) =>
        JsonSerializer.Serialize(new MapCommandMessage(Version, type, projectId, revision, payload), Options);

    public static MapEventMessage? TryParseEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != Version ||
                !root.TryGetProperty("type", out var typeElement)) return null;
            var type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type) || !KnownEventTypes.Contains(type)) return null;
            return new MapEventMessage(
                Version,
                type,
                ReadGuid(root, "projectId"),
                ReadInt64(root, "revision"),
                ReadGuid(root, "trackId"),
                ReadGuid(root, "waypointId"),
                ReadGuid(root, "guideNodeId"),
                ReadGuid(root, "guideSegmentId"),
                ReadDouble(root, "latitude"),
                ReadDouble(root, "longitude"),
                ReadDouble(root, "zoom"),
                ReadDouble(root, "bearing"),
                ReadDouble(root, "pitch"),
                ReadDouble(root, "screenX"),
                ReadDouble(root, "screenY"),
                ReadString(root, "message"),
                ReadDouble(root, "durationMs"),
                ReadGuid(root, "trackPointId"),
                ReadInt64(root, "geometryRevision"),
                ReadGuid(root, "editSessionId"),
                ReadGuid(root, "splitSectionId"),
                ReadBool(root, "ctrlKey"), ReadGuid(root, "favoriteId"), ReadInt64(root, "requestId"));
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    public static IReadOnlySet<string> KnownCommandTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "SetTerrainState", "TerrainHealthResult", "SetTerrainCamera", "PauseTerrain", "SetOfflineTerrain", "SetOsmRoads", "BeginOfflineRegion", "CancelOfflineRegion", "ClearOfflineRegion", "SetMapLayerStack",
        "SetFavorites", "FlyToFavorite", "RunFavoritesSmoke", "SetSelectedFavorite", "ShowFavoriteHover", "CloseFavoritePopup", "EndFavoriteMove", "CancelFavoriteDrag",
        "Initialize", "SetMapSource", "SetTracks", "SetSplitSections", "SetSplitSectionSelection", "SetTrackDirectionArrows", "SetWaypoints", "SetGuideNodes", "SetGuideSegments", "SetSelection", "SetElevationHover", "SetMarkerPopupMedia", "SetMarkerPopupError", "FitBounds", "SetView", "FlyToWaypoint", "RunInteractionSmoke", "RunP5InteractionSmoke", "RunP51MediaSmoke", "RunP61SplitSectionSmoke", "RunP62TrackClickSmoke", "RunP62RightClickSmoke", "RunP7PlanningClickSmoke", "ClearProject", "BeginSnapLocationPick", "BeginMarkerAdd", "RunP5MarkerAddSmoke", "RunP5WaypointEditSmoke", "RunP5GuideEditSmoke", "BeginTrackEdit", "EndTrackEdit", "SetTrackEditHandles", "PreviewTrackRange", "RunP6TrackEditSmoke", "BeginRoutePlanning", "EndRoutePlanning", "SetRoutePreview"
    };

    public static IReadOnlySet<string> KnownEventTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "TerrainStateChanged", "TerrainUnavailable", "TerrainEditBlocked",
        "FavoriteClicked", "FavoriteRightClicked", "FavoritesSmokeCompleted", "FavoriteHoverEntered", "FavoriteHoverLeft", "FavoriteDragStarted", "FavoriteDragCompleted", "FavoriteDragCanceled", "FavoriteLinkRequested",
        "MapReady", "ViewChanged", "TrackClicked", "SplitSectionClicked", "SplitSectionContextMenuRequested", "TrackContextMenuRequested", "WaypointClicked", "GuideNodeClicked", "GuideSegmentClicked", "MediaOpenRequested", "MediaThumbnailRequested", "MediaThumbnailReady", "WaypointDragEnded", "GuideNodeDragEnded", "TrackSnapLocationPicked", "MarkerAddLocationPicked", "MapRightClicked", "MarkerRightClicked", "MouseMoved", "MouseLeft", "MapError", "TileError", "RenderMetrics", "InteractionSmokeCompleted", "TrackEditLineClicked", "TrackPointDragEnded", "TrackEditPreviewCancelled", "RoutePlanningPointPicked"
    };

    private static Guid? ReadGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && Guid.TryParse(element.GetString(), out var value) ? value : null;
    private static long ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt64(out var value) ? value : 0;
    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetDouble(out var value) ? value : null;
    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) ? element.GetString() : null;
    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;
}

public sealed record MapCommandMessage(
    int ProtocolVersion,
    string Type,
    Guid? ProjectId,
    long Revision,
    object? Payload);

public sealed record MapEventMessage(
    int ProtocolVersion,
    string Type,
    Guid? ProjectId,
    long Revision,
    Guid? TrackId,
    Guid? WaypointId,
    Guid? GuideNodeId,
    Guid? GuideSegmentId,
    double? Latitude,
    double? Longitude,
    double? Zoom,
    double? Bearing,
    double? Pitch,
    double? ScreenX,
    double? ScreenY,
    string? Message,
    double? DurationMilliseconds,
    Guid? TrackPointId = null,
    long GeometryRevision = 0,
    Guid? EditSessionId = null,
    Guid? SplitSectionId = null,
    bool CtrlKey = false,
    Guid? FavoriteId = null,
    long RequestId = 0);


