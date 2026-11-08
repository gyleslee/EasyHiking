using System.Text.Json.Serialization;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Models;

public sealed class TrackSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? Name { get; set; }

    /// <summary>逻辑拆分分段 ID；旧项目为空时以物理段 ID 作为稳定业务 ID。</summary>
    public Guid? SplitSectionId { get; set; }

    /// <summary>同一逻辑分段的各连续 Part 共用的显示名称。</summary>
    public string? SplitSectionName { get; set; }

    /// <summary>逻辑拆分分段的用户说明；不覆盖导入文件的原始扩展属性。</summary>
    public string? SplitSectionDescription { get; set; }

    /// <summary>P7 规划来源；与 P6 人工直线连接的来源属性分开。</summary>
    public RouteSegmentProvenance? RouteProvenance { get; set; }

    public List<TrackPoint> Points { get; set; } = [];

    /// <summary>导入格式中属于该轨迹段的原始属性和扩展XML。</summary>
    public Dictionary<string, string?> OriginalAttributes { get; set; } = [];

    [JsonIgnore]
    public int PointCount => Points.Count;

    [JsonIgnore]
    public bool IsEmpty => Points.Count == 0;

    [JsonIgnore]
    public TrackPoint? StartPoint => Points.Count == 0 ? null : Points[0];

    [JsonIgnore]
    public TrackPoint? EndPoint => Points.Count == 0 ? null : Points[^1];

    [JsonIgnore]
    public double Length => GetLength();

    [JsonIgnore]
    public GeoBoundingBox? BoundingBox => GetBoundingBox();

    public double GetLength(IGeoDistanceService? distanceService = null)
    {
        distanceService ??= GeoDistanceCalculator.Instance;
        var length = 0d;

        for (var index = 1; index < Points.Count; index++)
        {
            length += distanceService.CalculateDistance(Points[index - 1].Coordinate, Points[index].Coordinate);
        }

        return length;
    }

    public GeoBoundingBox? GetBoundingBox()
    {
        if (Points.Count == 0)
        {
            return null;
        }

        var first = Points[0].Coordinate;
        var boundingBox = new GeoBoundingBox(first.Latitude, first.Latitude, first.Longitude, first.Longitude);
        for (var index = 1; index < Points.Count; index++)
        {
            boundingBox.ExpandToInclude(Points[index].Coordinate);
        }

        return boundingBox;
    }

    public void Reverse(IGeoDistanceService? distanceService = null)
    {
        Points.Reverse();
        RecalculateDistances(0, distanceService);
    }

    internal double RecalculateDistances(double initialDistance, IGeoDistanceService? distanceService = null)
    {
        distanceService ??= GeoDistanceCalculator.Instance;
        if (Points.Count == 0)
        {
            return initialDistance;
        }

        var cumulativeDistance = initialDistance;
        Points[0].DistanceFromStart = cumulativeDistance;

        for (var index = 1; index < Points.Count; index++)
        {
            cumulativeDistance += distanceService.CalculateDistance(
                Points[index - 1].Coordinate,
                Points[index].Coordinate);
            Points[index].DistanceFromStart = cumulativeDistance;
        }

        return cumulativeDistance;
    }
}
