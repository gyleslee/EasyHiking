using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.Routing;

/// <summary>项目外的增量规划会话。不重算已有路由；手绘交接点的吸附随新增段一并撤销。</summary>
public sealed class RoutePlanService
{
    private readonly IRoutingProvider _provider;
    private readonly IGeoDistanceService _distance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stack<(RouteControlPoint Point, RouteLeg? Leg, JunctionEdit? Junction)> _redo = new();
    private readonly Dictionary<Guid, JunctionEdit> _junctionEdits = [];
    private sealed record JunctionEdit(RouteLeg? ManualLeg, RouteControlPoint Point,
        GeoCoordinate OldEnd, GeoCoordinate OldPoint, double OldDistance,
        GeoCoordinate NewEnd, double NewDistance)
    {
        public void Apply(bool forward)
        {
            if (ManualLeg is not null)
            {
                ManualLeg.Geometry[^1] = forward ? NewEnd : OldEnd;
                ManualLeg.DistanceMeters = forward ? NewDistance : OldDistance;
            }
            Point.Coordinate = forward ? NewEnd : OldPoint;
        }
    }

    public RoutePlan Plan { get; }
    public bool CanRedo => _redo.Count > 0;

    public RoutePlanService(Guid projectId, RoutePlanningMode mode, IRoutingProvider provider,
        IGeoDistanceService? distance = null)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("规划必须属于一个项目。", nameof(projectId));
        _provider = provider;
        _distance = distance ?? GeoDistanceCalculator.Instance;
        Plan = new RoutePlan
        {
            ProjectId = projectId,
            PlanningMode = mode,
            CurrentLegMode = mode == RoutePlanningMode.Manual ? RouteLegMode.Manual : RouteLegMode.BRouter,
            Name = $"规划路线 {DateTime.Today:yyyy-MM-dd}"
        };
    }

    public void SetNextLegMode(RouteLegMode mode)
    {
        if (Plan.PlanningMode != RoutePlanningMode.Mixed && mode != Plan.CurrentLegMode)
            throw new InvalidOperationException("只有混合规划允许切换下一段方式。");
        Plan.CurrentLegMode = mode;
    }

    public async Task<RouteLeg?> AddPointAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken = default)
    {
        coordinate.Validate();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = Plan.ControlPoints.LastOrDefault();
            var point = new RouteControlPoint { Coordinate = coordinate, Order = Plan.ControlPoints.Count,
                Type = previous is null ? RouteControlPointType.Start : RouteControlPointType.End };
            RouteLeg? leg = null;
            JunctionEdit? junctionEdit = null;
            if (previous is not null)
            {
                var mode = Plan.CurrentLegMode;
                // 控制点是用户点击位置，BRouter端点则可能已吸附到路网。
                // 手绘从路由实际终点继续；切回路由时，将手绘终点直接吸附到路网。
                var start = Plan.PlanningMode == RoutePlanningMode.Mixed && Plan.Legs.Count > 0
                    ? Plan.Legs[^1].Geometry[^1] : previous.Coordinate;
                IReadOnlyList<GeoCoordinate> geometry;
                RouteRequestMetadata? metadata = null;
                if (mode == RouteLegMode.Manual)
                {
                    geometry = [start, coordinate];
                    if (start != previous.Coordinate)
                        junctionEdit = new(null, previous, previous.Coordinate, previous.Coordinate, 0, start, 0);
                }
                else
                {
                    var requestedAt = DateTimeOffset.UtcNow;
                    var requestStart = Plan.Legs.LastOrDefault()?.Mode == RouteLegMode.BRouter
                        ? previous.Coordinate : start;
                    var routed = await _provider.RouteFootShortestAsync(requestStart, coordinate,
                        cancellationToken);
                    geometry = routed.Geometry;
                    metadata = new RouteRequestMetadata
                    {
                        Profile = routed.Profile, DatasetId = routed.DatasetId,
                        RequestedStart = requestStart, RequestedEnd = coordinate,
                        RequestTime = requestedAt
                    };
                }
                if (geometry.Count < 2)
                    throw new InvalidOperationException("路由没有返回可用的轨迹几何，规划点未添加。");
                var checkedGeometry = geometry.ToList();
                foreach (var item in checkedGeometry) item.Validate();
                GeoCoordinate? alignedStart = null;
                if (Plan.PlanningMode == RoutePlanningMode.Mixed && Plan.Legs.Count > 0 &&
                    start != checkedGeometry[0])
                {
                    var last = Plan.Legs[^1];
                    if (last.Mode == RouteLegMode.Manual)
                    {
                        var end = checkedGeometry[0];
                        var changed = last.Geometry.ToArray(); changed[^1] = end;
                        junctionEdit = new(last, previous, last.Geometry[^1], previous.Coordinate,
                            last.DistanceMeters, end, Measure(changed));
                    }
                    else alignedStart = start;
                }
                var distance = 0d;
                for (var index = 1; index < checkedGeometry.Count; index++)
                    distance += _distance.CalculateDistance(index == 1 ? alignedStart ?? checkedGeometry[0] : checkedGeometry[index - 1], checkedGeometry[index]);
                leg = new RouteLeg { StartControlPointId = previous.Id, EndControlPointId = point.Id,
                    Mode = mode, Geometry = checkedGeometry, AlignedStartCoordinate = alignedStart,
                    DistanceMeters = distance,
                    BRouterRequestMetadata = metadata };
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (junctionEdit is not null) { junctionEdit.Apply(true); _junctionEdits.Add(leg!.Id, junctionEdit); }
            if (previous is not null && previous.Type == RouteControlPointType.End)
                previous.Type = RouteControlPointType.Via;
            Plan.ControlPoints.Add(point);
            if (leg is not null) Plan.Legs.Add(leg);
            _redo.Clear();
            Plan.ModifiedAt = DateTimeOffset.UtcNow;
            return leg;
        }
        finally { _gate.Release(); }
    }

    public bool UndoLastPoint()
    {
        if (!_gate.Wait(0)) return false;
        try
        {
            if (Plan.ControlPoints.Count == 0) return false;
            var point = Plan.ControlPoints[^1];
            var leg = Plan.Legs.Count == 0 ? null : Plan.Legs[^1];
            JunctionEdit? junction = null;
            if (leg is not null && _junctionEdits.Remove(leg.Id, out junction)) junction.Apply(false);
            Plan.ControlPoints.RemoveAt(Plan.ControlPoints.Count - 1);
            if (leg is not null) Plan.Legs.RemoveAt(Plan.Legs.Count - 1);
            if (Plan.ControlPoints.Count > 1) Plan.ControlPoints[^1].Type = RouteControlPointType.End;
            _redo.Push((point, leg, junction));
            Plan.ModifiedAt = DateTimeOffset.UtcNow;
            return true;
        }
        finally { _gate.Release(); }
    }

    public bool RedoLastPoint()
    {
        if (!_gate.Wait(0)) return false;
        try
        {
            if (!_redo.TryPop(out var item)) return false;
            if (item.Junction is { } junction) { junction.Apply(true); _junctionEdits.Add(item.Leg!.Id, junction); }
            if (Plan.ControlPoints.Count > 1) Plan.ControlPoints[^1].Type = RouteControlPointType.Via;
            Plan.ControlPoints.Add(item.Point);
            if (item.Leg is not null) Plan.Legs.Add(item.Leg);
            Plan.ModifiedAt = DateTimeOffset.UtcNow;
            return true;
        }
        finally { _gate.Release(); }
    }

    public void Clear()
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("正在计算路线，请稍后再清空。");
        try
        {
            Plan.ControlPoints.Clear();
            Plan.Legs.Clear();
            _redo.Clear();
            _junctionEdits.Clear();
            Plan.ModifiedAt = DateTimeOffset.UtcNow;
        }
        finally { _gate.Release(); }
    }

    public Track BuildTrack(string name, string? description = null)
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("正在计算路线，请稍后再完成规划。");
        try
        {
            if (Plan.ControlPoints.Count < 2 || Plan.Legs.Count != Plan.ControlPoints.Count - 1)
                throw new InvalidOperationException("至少选择两个规划点，且每一段都成功规划后才能完成。");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请填写轨迹名称。", nameof(name));
            var track = new Track { Name = name.Trim(), Description = description,
                SourceFormat = TrackSourceFormat.RoutePlanner };
            track.Metadata["RoutingSource"] = Plan.PlanningMode.ToString();
            var provenance = new RouteSegmentProvenance
            {
                RoutePlanId = Plan.Id, PlanningMode = Plan.PlanningMode,
                RouteLegId = Plan.Legs[0].Id, Mode = Plan.Legs[0].Mode,
                BRouterRequestMetadata = CopyRequestMetadata(Plan.Legs[0].BRouterRequestMetadata),
                Legs = Plan.Legs.Select(leg => new RouteLegProvenance
                {
                    RouteLegId = leg.Id, StartControlPointId = leg.StartControlPointId,
                    EndControlPointId = leg.EndControlPointId, Mode = leg.Mode,
                    OriginalDistanceMeters = leg.DistanceMeters,
                    OriginalRoutingStart = leg.AlignedStartCoordinate is null ? null : leg.Geometry[0],
                    AlignedStartCoordinate = leg.AlignedStartCoordinate,
                    BRouterRequestMetadata = CopyRequestMetadata(leg.BRouterRequestMetadata)
                }).ToList()
            };
            var sectionId = Guid.NewGuid();
            TrackSegment? segment = null;
            foreach (var leg in Plan.Legs)
            {
                if (leg.Geometry.Count < 2) throw new InvalidOperationException("规划分段几何不完整。");
                var geometry = GetPlannedGeometry(leg).ToList();
                // 规划控制点不是人工拆分边界。连续 Leg 共用一个物理段，仅去掉交界处
                // 完全重复的端点；不简化、不删除各 Leg 内原有的轨迹点。
                var continuous = segment?.EndPoint?.Coordinate == geometry[0];
                if (!continuous)
                {
                    // BRouter 可能返回真实不连续的端点，保留物理断点以免虚构连接距离。
                    // 所有 Part 仍共用一个逻辑分段，项目不会按规划点生成拆分分段。
                    segment = new TrackSegment
                    {
                        Name = track.Name, SplitSectionId = sectionId, SplitSectionName = track.Name,
                        RouteProvenance = provenance
                    };
                    track.Segments.Add(segment);
                }
                foreach (var value in geometry.Skip(continuous ? 1 : 0))
                {
                    var point = new TrackPoint
                    {
                        Latitude = value.Latitude, Longitude = value.Longitude,
                        SourceElevation = null, DemElevation = null, Timestamp = null,
                        IsInsertedByEditor = true
                    };
                    segment!.Points.Add(point);
                }
            }
            track.RecalculateDistances(_distance);
            return track;
        }
        finally { _gate.Release(); }
    }

    public static IEnumerable<GeoCoordinate> GetPlannedGeometry(RouteLeg leg) =>
        leg.AlignedStartCoordinate is { } start
            ? new[] { start }.Concat(leg.Geometry.Skip(1)) : leg.Geometry;

    private double Measure(IReadOnlyList<GeoCoordinate> points)
    {
        var result = 0d;
        for (var i = 1; i < points.Count; i++) result += _distance.CalculateDistance(points[i - 1], points[i]);
        return result;
    }

    private static RouteRequestMetadata? CopyRequestMetadata(RouteRequestMetadata? source) =>
        source is null ? null : new RouteRequestMetadata
        {
            Provider = source.Provider, Profile = source.Profile, DatasetId = source.DatasetId,
            RequestedStart = source.RequestedStart, RequestedEnd = source.RequestedEnd,
            RequestTime = source.RequestTime
        };
}
