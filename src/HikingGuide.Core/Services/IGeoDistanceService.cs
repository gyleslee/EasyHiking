using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Services;

public interface IGeoDistanceService
{
    double CalculateDistance(GeoCoordinate pointA, GeoCoordinate pointB);
}
