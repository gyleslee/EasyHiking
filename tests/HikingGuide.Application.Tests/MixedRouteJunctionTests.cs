using System.Text.Json;
using HikingGuide.Application.Routing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class MixedRouteJunctionTests
{
    private sealed class SnappingProvider : IRoutingProvider
    {
        public int Calls { get; private set; }
        public List<GeoCoordinate[]> Returned { get; } = [];
        public Task<RoutingResult> RouteFootShortestAsync(GeoCoordinate start, GeoCoordinate end,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var points = new[] { start with { Latitude = start.Latitude + .000003 },
                new GeoCoordinate((start.Latitude + end.Latitude) / 2, (start.Longitude + end.Longitude) / 2),
                end with { Latitude = end.Latitude - .000005 } };
            Returned.Add(points);
            return Task.FromResult(new RoutingResult(points, "shortest", "fixture"));
        }
    }

    [Fact]
    public async Task Mixed_HandDrawnEndpointSnapsDirectlyToRoutedStart_WithoutExtraPointOrLine()
    {
        var provider = new SnappingProvider();
        var plan = new RoutePlanService(Guid.NewGuid(), RoutePlanningMode.Mixed, provider);
        await plan.AddPointAsync(new(23, 113));
        await plan.AddPointAsync(new(23, 113.001));
        var first = plan.Plan.Legs[0]; var unchanged = first.Geometry.ToArray();
        var firstClickedEnd = plan.Plan.ControlPoints[1].Coordinate;
        plan.SetNextLegMode(RouteLegMode.Manual);
        var manual = (await plan.AddPointAsync(new(23.001, 113.002)))!;
        Assert.Equal(first.Geometry[^1], manual.Geometry[0]);
        Assert.NotEqual(firstClickedEnd, manual.Geometry[0]);
        Assert.Equal(plan.Plan.ControlPoints[1].Coordinate, manual.Geometry[0]);
        var clickedEnd = manual.Geometry[^1]; var originalManualDistance = manual.DistanceMeters;
        plan.SetNextLegMode(RouteLegMode.BRouter);
        var routed = (await plan.AddPointAsync(new(23.002, 113.003)))!;
        Assert.Equal(routed.Geometry[0], manual.Geometry[^1]);
        Assert.Equal(routed.Geometry[0], plan.Plan.ControlPoints[2].Coordinate);
        Assert.NotEqual(clickedEnd, manual.Geometry[^1]);
        Assert.Equal(clickedEnd, routed.BRouterRequestMetadata!.RequestedStart);
        Assert.Equal(provider.Returned[1], routed.Geometry); // 原始路由点全部保留
        Assert.Equal(unchanged, first.Geometry); // 不回改已完成段
        var track = plan.BuildTrack("混合连续路线");
        var points = Assert.Single(track.Segments).Points;
        Assert.Single(track.GetSplitSections());
        Assert.Equal(first.Geometry.Concat(manual.Geometry.Skip(1)).Concat(routed.Geometry.Skip(1)),
            points.Select(p => p.Coordinate));
        Assert.Equal(6, points.Count);
        Assert.DoesNotContain(points, p => p.Coordinate == clickedEnd);
        Assert.Equal(plan.Plan.Legs.Sum(l => l.DistanceMeters), track.GetTotalLength(), 7);
        Assert.All(points, p => { Assert.Null(p.SourceElevation); Assert.Null(p.DemElevation); });
        var restored = JsonSerializer.Deserialize<Track>(JsonSerializer.Serialize(track))!;
        Assert.Equal(points.Select(p=>p.Coordinate), restored.Segments[0].Points.Select(p=>p.Coordinate));
        Assert.True(plan.UndoLastPoint());
        Assert.Equal(clickedEnd, manual.Geometry[^1]);
        Assert.Equal(clickedEnd, plan.Plan.ControlPoints[2].Coordinate);
        Assert.Equal(originalManualDistance, manual.DistanceMeters);
        Assert.True(plan.RedoLastPoint());
        Assert.Equal(routed.Geometry[0], manual.Geometry[^1]);
        Assert.Equal(routed.Geometry[0], plan.Plan.ControlPoints[2].Coordinate);
        Assert.Equal(2, provider.Calls);
        Assert.Same(routed, plan.Plan.Legs[^1]);
        Assert.Equal(points.Select(p => p.Coordinate), plan.BuildTrack("重做").Segments[0].Points.Select(p => p.Coordinate));
        Assert.True(plan.UndoLastPoint());Assert.True(plan.UndoLastPoint());
        Assert.Equal(firstClickedEnd,plan.Plan.ControlPoints[1].Coordinate);
        Assert.Equal(unchanged,first.Geometry);
        Assert.True(plan.RedoLastPoint());Assert.True(plan.RedoLastPoint());
        Assert.Equal(points.Select(p=>p.Coordinate),plan.BuildTrack("完整重做").Segments[0].Points.Select(p=>p.Coordinate));
    }

    [Fact]
    public async Task RepeatedSwitchesAndSubmeterSnapOffsets_AlwaysProduceOnePhysicalPart()
    {
        var plan = new RoutePlanService(Guid.NewGuid(), RoutePlanningMode.Mixed, new SnappingProvider());
        await plan.AddPointAsync(new(23,113));
        for (var i = 1; i <= 12; i++)
        {
            plan.SetNextLegMode(i % 3 == 0 ? RouteLegMode.Manual : RouteLegMode.BRouter);
            await plan.AddPointAsync(new(23 + i * .0001, 113 + i * .001));
        }
        var track = plan.BuildTrack("多次交接");
        Assert.Single(track.Segments);
        Assert.Equal(plan.Plan.Legs.Sum(l=>l.Geometry.Count)-plan.Plan.Legs.Count+1,track.GetPointCount());
        for(var i=1;i<plan.Plan.Legs.Count;i++)
            Assert.Equal(RoutePlanService.GetPlannedGeometry(plan.Plan.Legs[i-1]).Last(),RoutePlanService.GetPlannedGeometry(plan.Plan.Legs[i]).First());
        Assert.Equal(plan.Plan.Legs.Sum(l => l.DistanceMeters), track.GetTotalLength(), 7);
        Assert.True(plan.UndoLastPoint());
        plan.SetNextLegMode(RouteLegMode.BRouter);
        await plan.AddPointAsync(new(23.01, 113.02));
        Assert.False(plan.CanRedo);
        Assert.Single(plan.BuildTrack("撤销后改画").Segments);
    }

    [Fact]
    public async Task FailedRouting_DoesNotMoveHandDrawnEndpointOrControlPoint()
    {
        var plan=new RoutePlanService(Guid.NewGuid(),RoutePlanningMode.Mixed,new FailingProvider());
        await plan.AddPointAsync(new(23,113));plan.SetNextLegMode(RouteLegMode.Manual);
        var manual=(await plan.AddPointAsync(new(23.001,113.001)))!;
        var before=manual.Geometry.ToArray();var distance=manual.DistanceMeters;
        plan.SetNextLegMode(RouteLegMode.BRouter);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>plan.AddPointAsync(new(23.002,113.002)));
        Assert.Equal(before,manual.Geometry);Assert.Equal(distance,manual.DistanceMeters);
        Assert.Equal(before[^1],plan.Plan.ControlPoints[^1].Coordinate);Assert.Single(plan.Plan.Legs);
    }
    private sealed class FailingProvider:IRoutingProvider
    {
        public Task<RoutingResult> RouteFootShortestAsync(GeoCoordinate start,GeoCoordinate end,CancellationToken cancellationToken=default)=>throw new InvalidOperationException("测试路由失败");
    }
}
