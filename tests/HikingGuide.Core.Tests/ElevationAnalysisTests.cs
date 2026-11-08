using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Tests;

public sealed class ElevationAnalysisTests
{
    private sealed class TestDistance : IGeoDistanceService
    {
        public double CalculateDistance(GeoCoordinate a, GeoCoordinate b) =>
            Math.Abs(b.Longitude - a.Longitude);
    }

    private static ElevationTargetDescriptor Target(params TrackSegment[] parts) =>
        new(Guid.NewGuid(), ElevationTargetType.Track, Guid.NewGuid(), Guid.NewGuid(),
            "测试轨迹", parts);

    private static TrackSegment Part(params (double X, double? Elevation)[] values) =>
        new() { Points = values.Select(value => new TrackPoint
            { Latitude = 0, Longitude = value.X, SourceElevation = value.Elevation }).ToList() };

    private static readonly ElevationAnalysisProfile RawProfile = new(10, 0, 30);
    private static readonly ElevationSourceDescriptor Gps =
        new(ElevationSourceType.Gps, null, "GPS海拔", "gps-source");

    [Fact]
    public async Task KnownProfile_ProducesAscent25AndDescent5()
    {
        var engine = new StandardElevationAnalysisV1(new TestDistance());
        var target = Target(Part((0, 100), (10, 110), (20, 105), (30, 120)));
        var result = await engine.AnalyzeAsync(target, Gps, RawProfile);
        Assert.Equal(4, result.Series.Count);
        Assert.Equal(25, result.Summary.AscentMeters);
        Assert.Equal(5, result.Summary.DescentMeters);
        Assert.Equal(30, result.Summary.DistanceMeters);
        Assert.Equal(ElevationAvailability.Complete, result.Summary.Availability);
    }

    [Fact]
    public async Task MultiPartGap_NeitherDistanceNorAscentIncludesGap()
    {
        var engine = new StandardElevationAnalysisV1(new TestDistance());
        var target = Target(Part((0, 100), (10, 110)),
            Part((100, 900), (110, 910)));
        var result = await engine.AnalyzeAsync(target, Gps, RawProfile);
        Assert.Equal(20, result.Summary.DistanceMeters);
        Assert.Equal(20, result.Summary.AscentMeters);
        Assert.Equal(new[] { 0, 0, 1, 1 }, result.Series.Select(item => item.PartIndex));
    }

    [Fact]
    public async Task MissingGps_DoesNotFillFromNeighborOrDem()
    {
        var engine = new StandardElevationAnalysisV1(new TestDistance());
        var target = Target(Part((0, 100), (10, null), (20, 120)));
        var result = await engine.AnalyzeAsync(target, Gps, RawProfile);
        Assert.Equal(ElevationAvailability.Partial, result.Summary.Availability);
        Assert.Null(result.Summary.AscentMeters);
        Assert.Null(result.Series[1].RawElevationMeters);
        Assert.Equal(2d / 3d, result.Summary.CoverageRatio);
    }

    [Fact]
    public async Task ShortGpsGap_InsideSamplingInterval_RemainsVisible()
    {
        var engine = new StandardElevationAnalysisV1(new TestDistance());
        var target = Target(Part((0, 100), (5, null), (6, 106), (10, 110)));
        var result = await engine.AnalyzeAsync(target, Gps, RawProfile);
        Assert.Contains(result.Series, point => point.DistanceMeters == 5 &&
            point.RawElevationMeters is null);
        Assert.Equal(ElevationAvailability.Partial, result.Summary.Availability);
        Assert.Null(result.Summary.AscentMeters);
    }

    [Fact]
    public async Task BRouterTrackWithoutGps_IsUnavailable()
    {
        var engine = new StandardElevationAnalysisV1(new TestDistance());
        var result = await engine.AnalyzeAsync(Target(Part((0, null), (10, null))),
            Gps, RawProfile);
        Assert.Equal(ElevationAvailability.Unavailable, result.Summary.Availability);
        Assert.Null(result.Summary.AscentMeters);
    }

    [Fact]
    public async Task Comparison_UsesCommonGridAndDifferenceStatistics()
    {
        var engine = new StandardElevationAnalysisV1(new TestDistance());
        var target = Target(Part((0, 100), (10, 110)));
        var gps = await engine.AnalyzeAsync(target, Gps, RawProfile);
        var dem = await engine.AnalyzeAsync(target,
            new(ElevationSourceType.Dem, Guid.NewGuid(), "DEM A", "fingerprint"),
            RawProfile, new ConstantDem(105));
        var difference = StandardElevationAnalysisV1.Compare(gps, dem);
        Assert.Equal(2, difference.CommonValidSamples);
        Assert.Equal(0, difference.MeanDifferenceMeters);
        Assert.Equal(5, difference.MeanAbsoluteDifferenceMeters);
        Assert.Equal(5, difference.RmseMeters);
    }

    private sealed class ConstantDem(double elevation) : IElevationProvider
    {
        public ValueTask<double?> SampleAsync(Guid id, GeoCoordinate coordinate,
            CancellationToken cancellationToken = default) => new(elevation);
    }
}
