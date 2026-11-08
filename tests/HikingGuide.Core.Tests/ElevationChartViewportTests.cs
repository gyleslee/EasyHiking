using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Tests;

public sealed class ElevationChartViewportTests
{
    private static ElevationSeriesPoint Point(double distance, double? height, int part = 0) =>
        new(part, distance, new GeoCoordinate(0, distance / 100000), height, height, 0);

    [Fact]
    public void NewProfile_InvalidatesOldDefault()
    {
        var current = new ElevationAnalysisProfile();
        Assert.Equal((10, 20, 10), (current.SampleSpacingMeters,
            current.SmoothingWindowMeters, current.SlopeWindowMeters));
        Assert.Equal("StandardElevationAnalysisV2", current.Version);
        Assert.NotEqual(current, new ElevationAnalysisProfile(10, 30, 30,
            "StandardElevationAnalysisV1"));
    }

    [Fact]
    public void FullZoomOutAndPan_StayInsideTargetDistance()
    {
        var full = ElevationChartViewport.Full(10_000);
        Assert.Equal((0, 10_000), (full.XMinMeters, full.XMaxMeters));
        Assert.Equal(full, ElevationChartViewport.Zoom(full, 5_000, -120, 10_000));
        var zoom = ElevationChartViewport.Zoom(full, 5_000, 120, 10_000);
        Assert.True(zoom.XMaxMeters - zoom.XMinMeters < 10_000);
        var left = ElevationChartViewport.Pan(zoom, -100_000, 10_000);
        var right = ElevationChartViewport.Pan(zoom, 100_000, 10_000);
        Assert.Equal(0, left.XMinMeters);
        Assert.Equal(10_000, right.XMaxMeters);
    }

    [Fact]
    public void VisibleY_UsesLocalSliceAndMinimumRange()
    {
        var points = new[] { Point(0, 0), Point(1_000, 500), Point(5_000, 100),
            Point(6_000, 150), Point(10_000, 0) };
        var full = ElevationChartViewport.FitVisibleY(points,
            ElevationChartViewport.Full(10_000), default);
        var local = ElevationChartViewport.FitVisibleY(points,
            ElevationChartViewport.Clamp(5_000, 6_000, 10_000), full);
        Assert.True(local.MinimumMeters < 100);
        Assert.True(local.MaximumMeters > 150);
        Assert.True(local.MaximumMeters < full.MaximumMeters);
        var flat = ElevationChartViewport.FitVisibleY(
            [Point(0, 378), Point(1_000, 379)], ElevationChartViewport.Full(1_000), default);
        Assert.True(flat.MaximumMeters - flat.MinimumMeters >= 20);
    }

    [Fact]
    public void NoDataSlice_KeepsPreviousYButReportsNoData()
    {
        var points = new[] { Point(0, 100), Point(100, 120), Point(200, null),
            Point(300, null), Point(400, 300) };
        var previous = ElevationChartViewport.FitVisibleY(points,
            ElevationChartViewport.Clamp(0, 100, 400), default);
        var empty = ElevationChartViewport.FitVisibleY(points,
            ElevationChartViewport.Clamp(210, 290, 400), previous);
        Assert.False(empty.HasData);
        Assert.Equal(previous.MinimumMeters, empty.MinimumMeters);
        Assert.Equal(previous.MaximumMeters, empty.MaximumMeters);
    }

    [Fact]
    public void NearestIndex_BinarySearchIncludesMissingSample()
    {
        var points = new[] { Point(0, 100), Point(500, null), Point(750, 379),
            Point(1_000, 390) };
        Assert.Equal(2, ElevationChartViewport.NearestIndex(points, 748));
        Assert.Equal(1, ElevationChartViewport.NearestIndex(points, 500));
        Assert.Equal(0, ElevationChartViewport.NearestIndex(points, -10));
        Assert.Equal(3, ElevationChartViewport.NearestIndex(points, 2_000));
    }

    [Fact]
    public void ChartRuns_BreakAtGeometryAndNoDataGaps()
    {
        var points = new[] { Point(0, 100), Point(10, 110), Point(20, null),
            Point(30, 120), Point(40, 130), Point(40, 900, 1), Point(50, 910, 1) };
        var runs = ElevationChartRuns.Build(points);
        Assert.Equal(3, runs.Count);
        Assert.Equal(new[] { 2, 2, 2 }, runs.Select(run => run.Count));
        Assert.DoesNotContain(runs, run => run.Any(point => point.ProcessedElevationMeters is null));
        Assert.Equal(40, runs[2][0].DistanceMeters);
    }
}
