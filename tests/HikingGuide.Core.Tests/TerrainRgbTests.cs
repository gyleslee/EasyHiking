using HikingGuide.Core.Elevation;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Tests;
public sealed class TerrainRgbTests
{
    [Theory]
    [InlineData(-100)] [InlineData(0)] [InlineData(123.4)] [InlineData(1000)] [InlineData(5000)] [InlineData(-9999.9)]
    public void EncodingRoundTrips(double h){var (r,g,b)=TerrainRgb.Encode(h);Assert.InRange(Math.Abs(TerrainRgb.Decode(r,g,b)-h),0,.1);}
    [Fact]
    public void InvalidNumbersCannotBecomeFakeTerrain()
    {Assert.Throws<ArgumentOutOfRangeException>(()=>TerrainRgb.Encode(double.NaN));Assert.Throws<ArgumentOutOfRangeException>(()=>TerrainRgb.Encode(-10001));}
    [Fact]
    public void GridSharesWorldCoordinatesAcrossEdges()
    {
        Assert.Equal(TerrainRgb.Coordinate(9,417,222,512,17.5),TerrainRgb.Coordinate(9,418,222,0,17.5));
        Assert.Equal(TerrainRgb.Coordinate(9,417,222,25.5,512),TerrainRgb.Coordinate(9,417,223,25.5,0));
        Assert.InRange(TerrainRgb.MaxNativeZoom(30,24),10,12);Assert.True(TerrainRgb.MaxNativeZoom(30,24)<18);
        Assert.True(TerrainRgb.MaxNativeZoom(90,24)<TerrainRgb.MaxNativeZoom(30,24));
    }
    [Fact]
    public void DefaultsAndLimitsAreExplicit()
    {
        var s=new Terrain3DSettings();Assert.False(s.Enabled);Assert.Null(s.TerrainDemDatabaseId);Assert.Equal(4096,s.CacheLimitMb);Assert.Equal(55,s.DefaultPitch);Assert.True(s.IsValid);
        Assert.False((s with{Exaggeration=3.01}).IsValid);Assert.False((s with{DefaultPitch=double.NaN}).IsValid);Assert.False((s with{CacheLimitMb=0}).IsValid);
    }
    [Theory]
    [InlineData(0.75,0.8)] [InlineData(1.25,1.3)] [InlineData(1.37,1.4)] [InlineData(2.25,2.3)]
    public void FractionalExaggerationRoundsToOneDecimal(double value,double expected)
    {var settings=new Terrain3DSettings{Exaggeration=value};Assert.True(settings.IsValid);Assert.Equal(expected,Terrain3DSettings.RoundExaggeration(value));}
}
