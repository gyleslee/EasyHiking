using HikingGuide.Core.Calibration;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Tests;

public sealed class CalibrationDemSamplerTests
{
    private sealed class Provider(double? height) : IElevationProvider
    {
        public ValueTask<double?> SampleAsync(Guid id, GeoCoordinate coordinate, CancellationToken token = default)
            => ValueTask.FromResult(height);
    }

    private static TrackSegment Part(double start, double? source = null) => new()
    {
        Points = [new() { Latitude = 0, Longitude = start, SourceElevation = source },
            new() { Latitude = 0, Longitude = start + 0.001, SourceElevation = source }]
    };

    [Fact]
    public async Task SourceElevationNeverAffectsGridOrResults()
    {
        var sampler = new CalibrationDemSampler();
        var id = Guid.NewGuid();
        var a = await sampler.SampleAsync([Part(0, 99999)], id, new Provider(123), new());
        var b = await sampler.SampleAsync([Part(0, null)], id, new Provider(123), new());
        Assert.Equal(a, b);
        Assert.All(a, p => Assert.Equal(123, p.DemElevationMeters));
    }

    [Fact]
    public async Task MissingDemIsNotFilledWithSourceElevation()
    {
        var result = await new CalibrationDemSampler().SampleAsync([Part(0, 123)],
            Guid.NewGuid(), new Provider(null), new());
        Assert.All(result, p => Assert.Null(p.DemElevationMeters));
    }

    [Fact]
    public async Task MultipartNeverIncludesGeographicGap()
    {
        var sampler = new CalibrationDemSampler();
        var id = Guid.NewGuid();
        var a = await sampler.SampleAsync([Part(0)], id, new Provider(10), new());
        var b = await sampler.SampleAsync([Part(0), Part(20)], id, new Provider(10), new());
        Assert.Equal(2 * a[^1].DistanceMeters, b[^1].DistanceMeters, 6);
        Assert.Equal(a[^1].DistanceMeters, b.First(p => p.PartIndex == 1).DistanceMeters, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void RejectsInvalidSpacing(double spacing) =>
        Assert.Throws<ArgumentException>(() => new CalibrationSamplingProfile(spacing).Validate());
}
