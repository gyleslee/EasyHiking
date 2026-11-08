using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Tests;

public sealed class LocusTerrainDistanceTests
{
    private static TerrainElevationSample[] Slope(double slope) => Enumerable.Range(0, 201)
        .Select(i => new TerrainElevationSample(0, i * 5, new GeoCoordinate(0, i * 0.00005), 100 + i * 5 * slope / 100)).ToArray();

    [Theory]
    [InlineData(2.1)]
    [InlineData(-2.1)]
    [InlineData(0)]
    public void ExactBoundaryRemainsFlat(double slope)
    {
        var result = LocusTerrainDistanceClassifier.Classify(Slope(slope), new(), "geometry", "source");
        Assert.Equal(1000, result.FlatDistanceMeters);
        Assert.Equal(0, result.UphillDistanceMeters);
        Assert.Equal(0, result.DownhillDistanceMeters);
    }

    [Theory]
    [InlineData(2.2, true)]
    [InlineData(-2.2, false)]
    public void StrictlyBeyondBoundaryIsClassified(double slope, bool uphill)
    {
        var result = LocusTerrainDistanceClassifier.Classify(Slope(slope), new(), "geometry", "source");
        Assert.True((uphill ? result.UphillDistanceMeters : result.DownhillDistanceMeters) > 900);
        Assert.Equal(1000, result.TotalGeometryDistanceMeters);
    }

    [Fact]
    public void ThresholdOnlyChangeReusesRawSeries()
    {
        var raw = Slope(2.5);
        var a = LocusTerrainDistanceClassifier.Classify(raw, new(), "g", "s");
        var b = LocusTerrainDistanceClassifier.Classify(a.RawSeries, a.Profile with { FlatSlopeThresholdPercent = 3 }, "g", "s");
        Assert.Same(a.RawSeries, b.RawSeries);
        Assert.Equal(1000, b.FlatDistanceMeters);
        Assert.True(a.UphillDistanceMeters > 900);
    }

    [Fact]
    public void MissingDataAndMultipartGapAreNotClassified()
    {
        TerrainElevationSample[] raw = [new(0, 0, new(0, 0), 1), new(0, 5, new(0, 0), null), new(0, 10, new(0, 0), 10),
            new(1, 10, new(10, 10), 100), new(1, 15, new(10, 10), 100)];
        var result = LocusTerrainDistanceClassifier.Classify(raw, new(), "g", "s");
        Assert.Equal(10, result.UnclassifiedDistanceMeters);
        Assert.Equal(5, result.FlatDistanceMeters);
        Assert.Equal(15, result.TotalGeometryDistanceMeters);
    }

    [Fact]
    public void FingerprintChangesInvalidateClassification()
    {
        var result = LocusTerrainDistanceClassifier.Classify(Slope(0), new(), "geometry", "dem");
        Assert.True(result.IsCurrent("geometry", "dem"));
        Assert.False(result.IsCurrent("changed", "dem"));
        Assert.False(result.IsCurrent("geometry", "changed"));
    }
}
