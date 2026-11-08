using HikingGuide.Core.Geometry;

namespace HikingGuide.Application.Routing;

public interface IRoutingProvider
{
    Task<RoutingResult> RouteFootShortestAsync(
        GeoCoordinate start, GeoCoordinate end, CancellationToken cancellationToken = default);
}

public sealed record RoutingResult(
    IReadOnlyList<GeoCoordinate> Geometry,
    string Profile,
    string DatasetId);
