using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Models;

public enum RoutePlanningMode { BRouter, Manual, Mixed }
public enum RouteLegMode { BRouter, Manual }

/// <summary>未完成的规划草稿；只有提交产生的 Track 才进入项目文件。</summary>
public sealed class RoutePlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public RoutePlanningMode PlanningMode { get; set; }
    public RouteLegMode CurrentLegMode { get; set; }
    public string Name { get; set; } = "";
    public List<RouteControlPoint> ControlPoints { get; set; } = [];
    public List<RouteLeg> Legs { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RouteLeg
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StartControlPointId { get; set; }
    public Guid EndControlPointId { get; set; }
    public RouteLegMode Mode { get; set; }
    /// <summary>BRouter 原始返回的完整几何；不使用地图显示简化结果。</summary>
    public List<GeoCoordinate> Geometry { get; set; } = [];
    public double DistanceMeters { get; set; }
    public RouteRequestMetadata? BRouterRequestMetadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RouteRequestMetadata
{
    public string Provider { get; set; } = "BRouter";
    public string Profile { get; set; } = "";
    public string DatasetId { get; set; } = "";
    public GeoCoordinate RequestedStart { get; set; }
    public GeoCoordinate RequestedEnd { get; set; }
    public DateTimeOffset RequestTime { get; set; }
}
