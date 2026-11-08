using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Services;

/// <summary>
/// 使用 Haversine 公式计算 WGS84 经纬度点之间的球面距离。
/// 徒步尺度下误差远小于轨迹采样和 DEM 数据本身的典型误差。
/// </summary>
public sealed class GeoDistanceCalculator : IGeoDistanceService
{
    // IUGG 推荐的平均地球半径，单位：米。
    private const double EarthMeanRadiusMeters = 6_371_008.8;

    public static GeoDistanceCalculator Instance { get; } = new();

    public double CalculateDistance(GeoCoordinate pointA, GeoCoordinate pointB)
    {
        pointA.Validate();
        pointB.Validate();

        if (pointA == pointB)
        {
            return 0;
        }

        var latitude1 = DegreesToRadians(pointA.Latitude);
        var latitude2 = DegreesToRadians(pointB.Latitude);
        var latitudeDelta = latitude2 - latitude1;
        var longitudeDelta = DegreesToRadians(pointB.Longitude - pointA.Longitude);

        var haversine = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                        Math.Cos(latitude1) * Math.Cos(latitude2) *
                        Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        var normalizedHaversine = Math.Clamp(haversine, 0, 1);
        var centralAngle = 2 * Math.Atan2(
            Math.Sqrt(normalizedHaversine),
            Math.Sqrt(1 - normalizedHaversine));

        return EarthMeanRadiusMeters * centralAngle;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}
