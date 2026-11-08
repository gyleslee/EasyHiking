using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HikingGuide.Application.Routing;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class RoutePlanServiceTests
{
    private sealed class FakeProvider : IRoutingProvider
    {
        public int Calls { get; private set; }
        public Task<RoutingResult> RouteFootShortestAsync(GeoCoordinate start,
            GeoCoordinate end, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new RoutingResult(
                [start, new((start.Latitude + end.Latitude) / 2, (start.Longitude + end.Longitude) / 2), end],
                "shortest", "dataset-1"));
        }
    }

    private static string Hash(RouteLeg leg) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(';', leg.Geometry.Select(p => $"{p.Latitude:R},{p.Longitude:R}")))));

    [Fact]
    public async Task Mixed_AddsOnlyNewLeg_AndRedoPreservesOriginalGeometry()
    {
        var provider = new FakeProvider();
        var service = new RoutePlanService(Guid.NewGuid(), RoutePlanningMode.Mixed, provider);
        for (var i = 0; i < 3; i++)
            await service.AddPointAsync(new GeoCoordinate(23, 113 + i * .001));
        Assert.Equal(2, provider.Calls);
        var first = Hash(service.Plan.Legs[0]);
        var second = Hash(service.Plan.Legs[1]);
        service.SetNextLegMode(RouteLegMode.Manual);
        await service.AddPointAsync(new GeoCoordinate(23, 113.003));
        Assert.Equal(2, provider.Calls);
        service.SetNextLegMode(RouteLegMode.BRouter);
        await service.AddPointAsync(new GeoCoordinate(23, 113.004));
        Assert.Equal(3, provider.Calls);
        Assert.Equal([RouteLegMode.BRouter, RouteLegMode.BRouter, RouteLegMode.Manual,
            RouteLegMode.BRouter], service.Plan.Legs.Select(leg => leg.Mode));
        Assert.Equal(first, Hash(service.Plan.Legs[0]));
        Assert.Equal(second, Hash(service.Plan.Legs[1]));
        var last = Hash(service.Plan.Legs[^1]);
        Assert.True(service.UndoLastPoint());
        Assert.True(service.RedoLastPoint());
        Assert.Equal(3, provider.Calls);
        Assert.Equal(last, Hash(service.Plan.Legs[^1]));
        Assert.Equal(RouteControlPointType.Start, service.Plan.ControlPoints[0].Type);
        Assert.Equal(RouteControlPointType.End, service.Plan.ControlPoints[^1].Type);
        Assert.All(service.Plan.ControlPoints.Skip(1).SkipLast(1),
            point => Assert.Equal(RouteControlPointType.Via, point.Type));
    }

    [Fact]
    public async Task Manual_NeverCallsProvider_AndBuildsOrdinaryEditableTrack()
    {
        var provider = new FakeProvider();
        var service = new RoutePlanService(Guid.NewGuid(), RoutePlanningMode.Manual, provider);
        await service.AddPointAsync(new GeoCoordinate(23, 113));
        await service.AddPointAsync(new GeoCoordinate(23, 113.001));
        await service.AddPointAsync(new GeoCoordinate(23, 113.002));
        var track = service.BuildTrack("手绘路线", "说明");
        Assert.Equal(0, provider.Calls);
        Assert.Equal(TrackSourceFormat.RoutePlanner, track.SourceFormat);
        Assert.Null(track.SourceFile);
        Assert.Single(track.Segments);
        Assert.Single(track.GetSplitSections());
        Assert.Equal(service.Plan.ControlPoints.Select(point => point.Coordinate),
            track.Segments.Single().Points.Select(point => point.Coordinate));
        Assert.All(track.Segments, segment =>
        {
            Assert.Equal(RouteLegMode.Manual, segment.RouteProvenance!.Mode);
            Assert.All(segment.Points, point =>
            {
                Assert.True(point.IsInsertedByEditor);
                Assert.Null(point.DemElevation);
                Assert.Null(point.SourceElevation);
            });
        });
        var project = new HikingProject();
        project.AddTrack(track, false);
        Assert.Null(project.MainGuideTrackId);
    }

    [Theory]
    [InlineData(RoutePlanningMode.BRouter, 3, 7)]
    [InlineData(RoutePlanningMode.Manual, 0, 4)]
    [InlineData(RoutePlanningMode.Mixed, 1, 5)]
    public async Task CompletePlanning_ProducesOneContinuousTrack_NotOneSectionPerControlPoint(
        RoutePlanningMode mode, int providerCalls, int pointCount)
    {
        var provider = new FakeProvider();
        var service = new RoutePlanService(Guid.NewGuid(), mode, provider);
        for (var index = 0; index < 4; index++)
        {
            if (mode == RoutePlanningMode.Mixed && index == 2)
                service.SetNextLegMode(RouteLegMode.Manual);
            await service.AddPointAsync(new GeoCoordinate(23, 113 + index * .001));
        }
        var expectedDistance = service.Plan.Legs.Sum(leg => leg.DistanceMeters);
        var track = service.BuildTrack(" 完整规划路线 ");
        var segment = Assert.Single(track.Segments);
        Assert.Single(track.GetSplitSections());
        Assert.Equal("完整规划路线", track.Name);
        Assert.Equal(providerCalls, provider.Calls);
        Assert.Equal(pointCount, segment.Points.Count);
        Assert.Equal(expectedDistance, track.GetTotalLength(), 8);
        Assert.Equal(expectedDistance, track.EndPoint!.DistanceFromStart, 8);
        Assert.Equal(service.Plan.ControlPoints[0].Coordinate, track.StartPoint!.Coordinate);
        Assert.Equal(service.Plan.ControlPoints[^1].Coordinate, track.EndPoint.Coordinate);
        Assert.Equal(3, segment.RouteProvenance!.Legs.Count);
        Assert.Equal(mode, segment.RouteProvenance.PlanningMode);
        Assert.Equal(service.Plan.Legs.Select(leg => leg.Id),
            segment.RouteProvenance.Legs.Select(leg => leg.RouteLegId));
        Assert.Equal(service.Plan.Legs.Select(leg => leg.Mode),
            segment.RouteProvenance.Legs.Select(leg => leg.Mode));
    }

    [Fact]
    public async Task CompletePlanning_RemovesOnlyExactDuplicateJunction_NotLegInteriorPoints()
    {
        var service = new RoutePlanService(Guid.NewGuid(), RoutePlanningMode.BRouter, new FakeProvider());
        for (var index = 0; index < 3; index++)
            await service.AddPointAsync(new GeoCoordinate(23, 113 + index * .001));
        var firstLeg = service.Plan.Legs[0];
        firstLeg.Geometry.Insert(1, firstLeg.Geometry[0]);
        var originalGeometries = service.Plan.Legs.Select(leg => leg.Geometry.ToArray()).ToArray();
        var track = service.BuildTrack("完整路线");
        var points = Assert.Single(track.Segments).Points;
        Assert.Equal(6, points.Count);
        Assert.Equal(firstLeg.Geometry[0], points[0].Coordinate);
        Assert.Equal(firstLeg.Geometry[0], points[1].Coordinate);
        Assert.Equal(firstLeg.Geometry.Concat(service.Plan.Legs[1].Geometry.Skip(1)),
            points.Select(point => point.Coordinate));
        for (var index = 0; index < originalGeometries.Length; index++)
            Assert.Equal(originalGeometries[index], service.Plan.Legs[index].Geometry);
    }

    [Fact]
    public async Task RealRoutingGap_RemainsPhysicalBreak_InOneLogicalSection_WithoutInventedDistance()
    {
        var service = new RoutePlanService(Guid.NewGuid(), RoutePlanningMode.BRouter, new GapProvider());
        for (var index = 0; index < 3; index++)
            await service.AddPointAsync(new GeoCoordinate(23, 113 + index * .001));
        var expectedDistance = service.Plan.Legs.Sum(leg => leg.DistanceMeters);
        var track = service.BuildTrack("有真实断点的路线");
        Assert.Equal(2, track.Segments.Count);
        var section = Assert.Single(track.GetSplitSections());
        Assert.Equal(2, section.GeometryParts.Count);
        Assert.Equal(expectedDistance, track.GetTotalLength(), 8);
        Assert.Equal(expectedDistance, track.EndPoint!.DistanceFromStart, 8);
        Assert.Equal(service.Plan.Legs[0].Geometry, track.Segments[0].Points.Select(point => point.Coordinate));
        Assert.Equal(service.Plan.Legs[1].Geometry, track.Segments[1].Points.Select(point => point.Coordinate));
        Assert.NotEqual(track.Segments[0].EndPoint!.Coordinate, track.Segments[1].StartPoint!.Coordinate);
        Assert.Equal(track.Segments[0].EndPoint!.DistanceFromStart,
            track.Segments[1].StartPoint!.DistanceFromStart);
        Assert.All(track.Segments, segment => Assert.Equal(2, segment.RouteProvenance!.Legs.Count));
    }

    [Fact]
    public async Task AggregatedProvenance_SnapshotsEveryLeg_AndSurvivesPersistenceAndEdits()
    {
        var service = new RoutePlanService(Guid.NewGuid(), RoutePlanningMode.Mixed, new FakeProvider());
        await service.AddPointAsync(new GeoCoordinate(23, 113));
        await service.AddPointAsync(new GeoCoordinate(23, 113.001));
        service.SetNextLegMode(RouteLegMode.Manual);
        await service.AddPointAsync(new GeoCoordinate(23, 113.002));
        var track = service.BuildTrack("混合完整路线");
        var provenance = track.Segments.Single().RouteProvenance!;
        var routed = provenance.Legs[0];
        Assert.Equal(service.Plan.Id, provenance.RoutePlanId);
        Assert.Equal(service.Plan.Legs[0].Id, provenance.RouteLegId);
        Assert.Equal(service.Plan.Legs[0].StartControlPointId, routed.StartControlPointId);
        Assert.Equal(service.Plan.Legs[0].EndControlPointId, routed.EndControlPointId);
        Assert.Equal(service.Plan.Legs[0].DistanceMeters, routed.OriginalDistanceMeters);
        Assert.Equal("shortest", provenance.BRouterRequestMetadata!.Profile);
        Assert.Equal("shortest", routed.BRouterRequestMetadata!.Profile);
        Assert.Null(provenance.Legs[1].BRouterRequestMetadata);
        service.Plan.Legs[0].BRouterRequestMetadata!.Profile = "changed-after-commit";
        Assert.Equal("shortest", provenance.BRouterRequestMetadata.Profile);
        Assert.Equal("shortest", routed.BRouterRequestMetadata.Profile);

        // 项目持久化采用 System.Text.Json：新增字段可往返，旧单 Leg 字段也保持兼容。
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var restored = JsonSerializer.Deserialize<Track>(JsonSerializer.Serialize(track, options), options)!;
        var restoredProvenance = Assert.Single(restored.Segments).RouteProvenance!;
        Assert.Equal(service.Plan.Id, restoredProvenance.RoutePlanId);
        Assert.Equal(RoutePlanningMode.Mixed, restoredProvenance.PlanningMode);
        Assert.Equal([RouteLegMode.BRouter, RouteLegMode.Manual],
            restoredProvenance.Legs.Select(leg => leg.Mode));
        Assert.Equal("dataset-1", restoredProvenance.Legs[0].BRouterRequestMetadata!.DatasetId);
        var project = new HikingProject();
        project.AddTrack(restored, false);
        var editor = new TrackEditService();
        var snap = new TrackSnapService();
        var start = snap.Snap(restored, new GeoCoordinate(23, 113.00025)).Position!;
        var end = snap.Snap(restored, new GeoCoordinate(23, 113.00175)).Position!;
        var crop = editor.PrepareCropRange(project, new TrackRange(restored.Id, start, end));
        crop.Apply();
        Assert.Same(restoredProvenance, restored.Segments.Single().RouteProvenance);
        crop.Undo();
        var other = service.BuildTrack("第二条规划路线");
        project.AddTrack(other, false);
        var merged = new TrackMergeService().Prepare(project,
            [new(restored.Id, false), new(other.Id, false)], "合并验证", false).NewTrack;
        Assert.Same(restoredProvenance, merged.Segments[0].RouteProvenance);
        Assert.All(merged.Segments, segment => Assert.Equal(2, segment.RouteProvenance!.Legs.Count));
        editor.PrepareReverseTrack(project, restored.Id).Apply();
        Assert.Same(restoredProvenance, restored.Segments.Single().RouteProvenance);
        editor.PrepareSplitSegment(project, restored.Id, restored.Segments[0].Id, 0, .5).Apply();
        Assert.Equal(2, restored.GetSplitSections().Count);
        Assert.All(restored.Segments, segment => Assert.Same(restoredProvenance, segment.RouteProvenance));
        Assert.Equal([RouteLegMode.BRouter, RouteLegMode.Manual],
            restoredProvenance.Legs.Select(leg => leg.Mode));
    }

    [Fact]
    public void LegacySingleLegProvenance_StillDeserializesWithoutMigration()
    {
        var legId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { RouteLegId = legId, Mode = RouteLegMode.BRouter,
            BRouterRequestMetadata = new { Profile = "shortest" } });
        var restored = JsonSerializer.Deserialize<RouteSegmentProvenance>(json)!;
        Assert.Equal(legId, restored.RouteLegId);
        Assert.Equal(RouteLegMode.BRouter, restored.Mode);
        Assert.Equal("shortest", restored.BRouterRequestMetadata!.Profile);
        Assert.Empty(restored.Legs);
        Assert.Null(restored.PlanningMode);
    }

    [Fact]
    public async Task ProviderFailure_DoesNotHalfAddPointOrLeg()
    {
        var provider = new FailingProvider();
        var service = new RoutePlanService(Guid.NewGuid(), RoutePlanningMode.BRouter, provider);
        await service.AddPointAsync(new GeoCoordinate(23, 113));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddPointAsync(new GeoCoordinate(23, 113.001)));
        Assert.Single(service.Plan.ControlPoints);
        Assert.Empty(service.Plan.Legs);
    }

    private sealed class FailingProvider : IRoutingProvider
    {
        public Task<RoutingResult> RouteFootShortestAsync(GeoCoordinate start, GeoCoordinate end,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("找不到路由数据");
    }

    private sealed class GapProvider : IRoutingProvider
    {
        private int _calls;
        public Task<RoutingResult> RouteFootShortestAsync(GeoCoordinate start, GeoCoordinate end,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            var routedStart = _calls == 1 ? start : start with { Longitude = start.Longitude + .0001 };
            return Task.FromResult(new RoutingResult([routedStart, end], "shortest", "dataset-1"));
        }
    }
}
