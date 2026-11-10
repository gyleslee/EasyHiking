using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public readonly record struct MapCoordinate(double Longitude, double Latitude);

public sealed record MapTrackFeature(
    Guid TrackId,
    string Name,
    bool IsActive,
    IReadOnlyList<IReadOnlyList<MapCoordinate>> Segments,
    int OriginalPointCount,
    int DisplayPointCount,
    string? DisplayColor = null)
{
    public long GeometryRevision { get; init; }
    public IReadOnlyList<MapRenderPiece>? RenderPieces { get; init; }
}

public sealed record MapRenderPiece(Guid SplitSectionId, Guid? GuideSegmentId, IReadOnlyList<MapCoordinate> Coordinates);

public sealed record MapWaypointFeature(
    Guid WaypointId,
    string Name,
    WaypointType Type,
    MapCoordinate Coordinate);

public sealed record MapDisplayData(
    Guid ProjectId,
    long Revision,
    double Zoom,
    IReadOnlyList<MapTrackFeature> Tracks,
    IReadOnlyList<MapWaypointFeature> Waypoints);

public readonly record struct MapBounds(double West, double South, double East, double North)
{
    public static MapBounds? FromCoordinates(IEnumerable<MapCoordinate> coordinates)
    {
        var values = coordinates.ToArray();
        if (values.Length == 0) return null;
        return new MapBounds(
            values.Min(point => point.Longitude), values.Min(point => point.Latitude),
            values.Max(point => point.Longitude), values.Max(point => point.Latitude));
    }
}

public sealed record MapViewState(
    double CenterLatitude,
    double CenterLongitude,
    double Zoom,
    double Bearing = 0,
    double Pitch = 0);

public enum MapSelectionKind
{
    None,
    Track,
    Waypoint,
    GuideNode,
    GuideSegment
}

public sealed record MapSelection(MapSelectionKind Kind, Guid? ObjectId, bool CtrlKey = false)
{
    public static MapSelection None { get; } = new(MapSelectionKind.None, null);
}
