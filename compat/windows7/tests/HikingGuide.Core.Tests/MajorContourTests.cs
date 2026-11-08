using HikingGuide.Core.Elevation;
using HikingGuide.Core.Models;
namespace HikingGuide.Core.Tests;
public sealed class MajorContourTests
{
    [Theory]
    [InlineData(50,50,true)] [InlineData(100,50,true)] [InlineData(150,50,true)]
    [InlineData(40,50,false)] [InlineData(-50,50,true)] [InlineData(0,50,false)]
    [InlineData(150,100,false)] [InlineData(200,100,true)] [InlineData(50,0,false)]
    public void OnlyConfiguredNonzeroMultiplesAreMajor(double elevation,int interval,bool expected)
    {Assert.Equal(expected,TerrainTileMath.IsMajorContour(elevation,interval));}
    [Fact]
    public void MajorStrokeIsTwiceNormalAndDoesNotChangeGeometryInterval()
    {
        var s=new OfflineTerrainSettings();Assert.True(s.MajorContoursEnabled);Assert.Equal(50,s.MajorContourIntervalMeters);
        Assert.Equal(2*TerrainTileMath.NormalContourStrokeWidth,TerrainTileMath.ContourStrokeWidth(150,true,50));
        Assert.Equal(TerrainTileMath.NormalContourStrokeWidth,TerrainTileMath.ContourStrokeWidth(150,false,50));
        Assert.Equal(TerrainTileMath.NormalContourStrokeWidth,TerrainTileMath.ContourStrokeWidth(140,true,50));
        Assert.Equal(100,TerrainTileMath.ContourInterval(13));Assert.Equal(100,TerrainTileMath.ContourInterval(14));Assert.Equal(10,TerrainTileMath.ContourInterval(15));
        Assert.False((s with{MajorContourIntervalMeters=0}).IsValid);
    }
}
