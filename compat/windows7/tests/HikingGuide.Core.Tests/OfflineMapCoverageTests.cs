using HikingGuide.Core.Maps;
namespace HikingGuide.Core.Tests;
public sealed class OfflineMapCoverageTests
{
    private static OfflineMapPoint World(double x,double y)=>new(x*360-180,Math.Atan(Math.Sinh(Math.PI*(1-2*y)))*180/Math.PI);
    [Theory][InlineData(0,0,0,0)][InlineData(1,0,0,1)][InlineData(1,0,1,2)][InlineData(1,1,1,3)][InlineData(1,1,0,4)][InlineData(12,3423,1763,19078479)]
    public void HilbertIdsMatchPmTilesSpecification(int z,int x,int y,long expected)=>Assert.Equal(expected,OfflineMapCoverage.TileId(z,x,y));
    [Fact] public void PolygonUsesIntersectionRatherThanBoundingRectangle()
    {
        var region=new OfflineMapRegion([World(.4,.4),World(.6,.4),World(.4,.6)]);
        var count=OfflineMapCoverage.Count(region,2,2);var tiles=OfflineMapCoverage.Enumerate(region,2,2).ToArray();Assert.Equal(3,count.Total);Assert.Equal(count.Total,tiles.LongLength);Assert.DoesNotContain(new OfflineTileAddress(2,2,2),tiles);
    }
    [Fact] public void DateLineSelectionWrapsWithoutDuplicatingLowZoomTiles()
    {
        var region=new OfflineMapRegion([new(179.8,20),new(-179.8,20),new(-179.8,19),new(179.8,19)]);
        var count=OfflineMapCoverage.Count(region,0,5);var tiles=OfflineMapCoverage.Enumerate(region,0,5).ToArray();Assert.Equal(count.Total,tiles.LongLength);Assert.Equal(tiles.Length,tiles.Distinct().Count());Assert.Contains(tiles,t=>t.X==0&&t.Zoom==5);Assert.Contains(tiles,t=>t.X==31&&t.Zoom==5);
    }
    [Fact] public void RejectsCrossingAndDegenerateOrInvalidRegions()
    {
        Assert.Throws<ArgumentException>(()=>OfflineMapCoverage.CreateGeometry(new([new(0,0),new(1,1),new(0,1),new(1,0)])));
        Assert.Throws<ArgumentException>(()=>OfflineMapCoverage.CreateGeometry(new([new(0,0),new(1,0),new(2,0)])));
        Assert.Throws<ArgumentException>(()=>OfflineMapCoverage.CreateGeometry(new([new(0,86),new(1,80),new(2,80)])));
    }
    [Fact] public void HugeSelectionIsBoundedAndCancellationWorks()
    {
        var region=new OfflineMapRegion([new(0,0),new(10,0),new(10,10),new(0,10)]);var count=OfflineMapCoverage.Count(region,24,24);Assert.False(count.Exact);Assert.True(count.Total>OfflineMapCoverage.MaximumDownloadTiles);
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>OfflineMapCoverage.Count(region,1,10,cancel.Token));
    }
}
