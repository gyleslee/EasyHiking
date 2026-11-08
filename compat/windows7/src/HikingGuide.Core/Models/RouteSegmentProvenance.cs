namespace HikingGuide.Core.Models;

public sealed class RouteSegmentProvenance
{
    public Guid RoutePlanId { get; set; }
    /// <summary>兼容旧项目的首个规划 Leg；完整规划来源以 Legs 为准。</summary>
    public Guid RouteLegId { get; set; }
    public RouteLegMode Mode { get; set; }
    public RouteRequestMetadata? BRouterRequestMetadata { get; set; }

    /// <summary>完成规划后生成一个逻辑轨迹，不将各 Leg 变为人工拆分分段。</summary>
    public RoutePlanningMode? PlanningMode { get; set; }

    /// <summary>各规划 Leg 的原始来源证据；轨迹后续裁剪、反转不重算这些历史信息。</summary>
    public List<RouteLegProvenance> Legs { get; set; } = [];
}

public sealed class RouteLegProvenance
{
    public Guid RouteLegId { get; set; }
    public Guid StartControlPointId { get; set; }
    public Guid EndControlPointId { get; set; }
    public RouteLegMode Mode { get; set; }
    public double OriginalDistanceMeters { get; set; }
    public RouteRequestMetadata? BRouterRequestMetadata { get; set; }
}
