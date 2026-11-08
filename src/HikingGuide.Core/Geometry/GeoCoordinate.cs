namespace HikingGuide.Core.Geometry;

/// <summary>
/// WGS84（EPSG:4326）坐标。业务层始终按 Latitude、Longitude 顺序表达。
/// </summary>
public readonly record struct GeoCoordinate(double Latitude, double Longitude)
{
    public void Validate()
    {
        if (!double.IsFinite(Latitude) || Latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(Latitude), "纬度必须是 -90 到 90 之间的有限数值。");
        }

        if (!double.IsFinite(Longitude) || Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(Longitude), "经度必须是 -180 到 180 之间的有限数值。");
        }
    }
}
