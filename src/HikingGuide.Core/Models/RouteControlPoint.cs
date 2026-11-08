using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Models;

public sealed class RouteControlPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public GeoCoordinate Coordinate { get; set; }

    public int Order { get; set; }

    public RouteControlPointType Type { get; set; }

    public string? Name { get; set; }
}
