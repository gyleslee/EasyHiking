using System.Text.Json.Serialization;
using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Models;

public sealed class TrackPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public double? SourceElevation { get; set; }

    public double? DemElevation { get; set; }

    /// <summary>本点由项目内轨迹编辑创建，而不是来源文件中的记录。</summary>
    public bool IsInsertedByEditor { get; set; }

    /// <summary>当前位置已不同于来源记录；旧源海拔不得作为此位置的导出海拔。</summary>
    public bool IsCoordinateEdited { get; set; }

    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>沿整条轨迹方向距起点的累计距离，单位：米。</summary>
    public double DistanceFromStart { get; set; }

    public Dictionary<string, string?> OriginalAttributes { get; set; } = [];

    [JsonIgnore]
    public GeoCoordinate Coordinate => new(Latitude, Longitude);
}
