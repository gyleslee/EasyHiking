using HikingGuide.Core.Geometry;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Tests;

public sealed class GeoDistanceCalculatorTests
{
    private readonly GeoDistanceCalculator _calculator = new();

    [Fact]
    public void SamePoint_ReturnsZero()
    {
        var point = new GeoCoordinate(39.9042, 116.4074);

        Assert.Equal(0, _calculator.CalculateDistance(point, point));
    }

    [Fact]
    public void ShortDistance_AtEquator_IsAbout111Meters()
    {
        var distance = _calculator.CalculateDistance(
            new GeoCoordinate(0, 0),
            new GeoCoordinate(0, 0.001));

        Assert.InRange(distance, 111.0, 111.4);
    }

    [Fact]
    public void HikingDistance_AtEquator_IsAbout5Point56Kilometers()
    {
        var distance = _calculator.CalculateDistance(
            new GeoCoordinate(0, 0),
            new GeoCoordinate(0, 0.05));

        Assert.InRange(distance, 5_550, 5_570);
    }

    [Fact]
    public void LongDistance_AtEquator_IsAbout55Point6Kilometers()
    {
        var distance = _calculator.CalculateDistance(
            new GeoCoordinate(0, 0),
            new GeoCoordinate(0, 0.5));

        Assert.InRange(distance, 55_500, 55_700);
    }
}
