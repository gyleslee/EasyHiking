using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Models;

public sealed class GuideNode : MapMarkerBase
{
    /// <summary>所属轨迹的稳定标识；空值表示尚未建立轨迹关系。</summary>
    public Guid? TrackId { get; set; }

    /// <summary>转为攻略节点前的普通标注归属，仅用于转回与来源追溯。</summary>
    public Guid? SourceTrackId { get; set; }
    /// <summary>即使来源轨迹被删除也保留的人可读来源；不作为结构化轨迹引用。</summary>
    public string? SourceTrackName { get; set; }

    public GeoCoordinate OriginalCoordinate { get; set; }

    public GeoCoordinate? SnappedCoordinate { get; set; }

    /// <summary>沿整条轨迹方向距起点的累计距离，单位：米。</summary>
    public double? DistanceFromStart { get; set; }

    public int? TrackSegmentIndex { get; set; }

    /// <summary>所在 TrackSegment 内的位置比例，取值 0 到 1。</summary>
    public double? TrackPosition { get; set; }

    /// <summary>P5 精确线段位置。旧版本的数值 TrackPosition 只用于迁移兼容。</summary>
    public TrackPosition? ExactPosition { get; set; }
    /// <summary>用户明确选择的重合路段位置。</summary>
    public TrackPosition? PreferredTrackPosition {get;set;}

    public double? DistanceToTrack { get; set; }

    public int StableOrder { get; set; }

    public string? TrackGeometryFingerprint { get; set; }

    public bool IsOrphaned { get; set; }

    public Waypoint ToWaypoint()
    {
        var coordinate = SnappedCoordinate ?? OriginalCoordinate;
        return new Waypoint
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
            Latitude = coordinate.Latitude,
            Longitude = coordinate.Longitude,
            AssociatedTrackId = SourceTrackId,
            PreservedOriginalCoordinate = OriginalCoordinate,
            PreservedSnappedCoordinate = SnappedCoordinate,
            PreferredTrackPosition = PreferredTrackPosition
        };
    }

    public Waypoint ToWaypoint(HikingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var waypoint = ToWaypoint();
        waypoint.AssociatedTrackId = SourceTrackId is Guid source &&
            project.Tracks.Any(track => track.Id == source) ? source :
            SourceTrackName is null && TrackId == project.MainGuideTrackId
                ? project.MainGuideTrackId : null;
        return waypoint;
    }
}
