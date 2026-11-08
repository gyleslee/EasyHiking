using System.Text.Json.Serialization;
using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Models;

public sealed class Waypoint : MapMarkerBase
{
    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>普通标注在项目树中的归属；不表示吸附或几何约束。</summary>
    public Guid? AssociatedTrackId { get; set; }
    /// <summary>用户选择的完整轨迹位置，区分同坐标处去程与回程。</summary>
    public TrackPosition? PreferredTrackPosition { get; set; }

    /// <summary>仅用于攻略节点与普通标注互转时无损保留原坐标。</summary>
    public GeoCoordinate? PreservedOriginalCoordinate { get; set; }

    /// <summary>仅用于攻略节点与普通标注互转时无损保留吸附坐标。</summary>
    public GeoCoordinate? PreservedSnappedCoordinate { get; set; }

    [JsonIgnore]
    public GeoCoordinate Coordinate => new(Latitude, Longitude);

    public GuideNode ToGuideNode()
    {
        return new GuideNode
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Type = Type,
            CustomTypeName = CustomTypeName,
            SourceElevation = SourceElevation,
            DemElevation = DemElevation,
            Timestamp = Timestamp,
            OriginalAttributes = new Dictionary<string, string?>(OriginalAttributes),
            ManualData = ManualData?.DeepCopy(),
            SourceTrackId = AssociatedTrackId,
            PreferredTrackPosition = PreferredTrackPosition,
            OriginalCoordinate = PreservedOriginalCoordinate ?? Coordinate,
            SnappedCoordinate = PreservedSnappedCoordinate ?? Coordinate
        };
    }
}
