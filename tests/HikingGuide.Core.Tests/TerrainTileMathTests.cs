using HikingGuide.Core.Elevation;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Tests;

public sealed class TerrainTileMathTests
{
    [Theory]
    [InlineData(12,0)] [InlineData(13,100)] [InlineData(14,100)] [InlineData(15,10)] [InlineData(22,10)]
    public void ZoomIntervals(int zoom,int interval)=>Assert.Equal(interval,TerrainTileMath.ContourInterval(zoom));

    [Fact]
    public void PlaneContours_Stitch_AndSkipZero()
    {
        var grid=new double?[4,4];
        for(var y=0;y<4;y++)for(var x=0;x<4;x++)grid[y,x]=-15+x*10;
        var lines=TerrainTileMath.Contours(grid,10);
        Assert.Equal(new[]{-10d,10d},lines.Select(l=>l.Elevation));
        Assert.All(lines,line=>{Assert.Equal(4,line.Points.Count);Assert.Equal(3,Math.Abs(line.Points[^1].Y-line.Points[0].Y));});
    }

    [Fact]
    public void NoData_IsNeverZeroFilledOrBridged()
    {
        var grid=new double?[3,3]{{0,20,40},{null,null,null},{0,20,40}};
        Assert.Empty(TerrainTileMath.Contours(grid,10));
        Assert.Empty(TerrainTileMath.Contours(new double?[2,2]{{10,10},{10,10}},10));
    }

    [Fact]
    public void SaddleProducesTwoSeparatePaths()
    {
        var lines=TerrainTileMath.Contours(new double?[2,2]{{20,0},{0,20}},10).Where(l=>l.Elevation==10).ToArray();
        Assert.Equal(2,lines.Length);Assert.All(lines,l=>Assert.Equal(2,l.Points.Count));
    }

    [Fact]
    public void WorldGridHasIdenticalTileEdges()
    {
        for(var y=0;y<=128;y++)Assert.Equal(TerrainTileMath.Coordinate(15,12345,23456,128,y),TerrainTileMath.Coordinate(15,12346,23456,0,y));
        Assert.True(TerrainTileMath.GroundStep(15,60)<TerrainTileMath.GroundStep(15,0));
    }

    [Fact]
    public void ShadeHasCorrectLightOrientationAndFlatValue()
    {
        Assert.Equal(Math.Sqrt(.5),TerrainTileMath.Shade(5,5,5,5,1),10);
        Assert.True(TerrainTileMath.Shade(0,2,0,0,1)>TerrainTileMath.Shade(2,0,0,0,1));
    }

    [Fact]
    public void GenerationCanCancel_AndSettingsValidate()
    {
        using var stop=new CancellationTokenSource();stop.Cancel();
        Assert.Throws<OperationCanceledException>(()=>TerrainTileMath.Contours(new double?[2,2],10,stop.Token));
        Assert.True(new OfflineTerrainSettings().IsValid);
        Assert.False((new OfflineTerrainSettings{ContourCacheMb=-1}).IsValid);
        Assert.False((new OfflineTerrainSettings{ContourColor="red"}).IsValid);
        Assert.False((new OfflineTerrainSettings{HillshadeOpacity=double.NaN}).IsValid);
    }
}
