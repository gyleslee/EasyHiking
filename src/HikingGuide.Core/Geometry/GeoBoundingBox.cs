using NetTopologySuite.Geometries;

namespace HikingGuide.Core.Geometry;

public sealed class GeoBoundingBox
{
    public GeoBoundingBox(double minLatitude, double maxLatitude, double minLongitude, double maxLongitude)
    {
        if (minLatitude > maxLatitude)
        {
            throw new ArgumentException("最小纬度不能大于最大纬度。", nameof(minLatitude));
        }

        if (minLongitude > maxLongitude)
        {
            throw new ArgumentException("最小经度不能大于最大经度。", nameof(minLongitude));
        }

        new GeoCoordinate(minLatitude, minLongitude).Validate();
        new GeoCoordinate(maxLatitude, maxLongitude).Validate();

        MinLatitude = minLatitude;
        MaxLatitude = maxLatitude;
        MinLongitude = minLongitude;
        MaxLongitude = maxLongitude;
    }

    public double MinLatitude { get; private set; }

    public double MaxLatitude { get; private set; }

    public double MinLongitude { get; private set; }

    public double MaxLongitude { get; private set; }

    public double LatitudeSpan => MaxLatitude - MinLatitude;

    public double LongitudeSpan => MaxLongitude - MinLongitude;

    public bool Contains(GeoCoordinate coordinate) =>
        coordinate.Latitude >= MinLatitude && coordinate.Latitude <= MaxLatitude &&
        coordinate.Longitude >= MinLongitude && coordinate.Longitude <= MaxLongitude;

    public void ExpandToInclude(GeoCoordinate coordinate)
    {
        coordinate.Validate();
        MinLatitude = Math.Min(MinLatitude, coordinate.Latitude);
        MaxLatitude = Math.Max(MaxLatitude, coordinate.Latitude);
        MinLongitude = Math.Min(MinLongitude, coordinate.Longitude);
        MaxLongitude = Math.Max(MaxLongitude, coordinate.Longitude);
    }

    /// <summary>
    /// 转换为 NetTopologySuite Envelope。NTS 使用 X=经度、Y=纬度。
    /// </summary>
    public Envelope ToEnvelope() => new(MinLongitude, MaxLongitude, MinLatitude, MaxLatitude);
}
