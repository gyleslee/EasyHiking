using HikingGuide.Core.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Tests;

public sealed class RasterTileReprojectionTests
{
    [Fact] public void KnownBeijingCoordinateMatchesReference()
    {
        var gcj=MapCoordinateTransform.FromWgs84(116.404,39.915,MapSourceCoordinateSystem.Gcj02);
        Assert.Equal(116.41024449916938,gcj.Longitude,10);Assert.Equal(39.91640428150164,gcj.Latitude,10);
        var bd=MapCoordinateTransform.FromWgs84(116.404,39.915,MapSourceCoordinateSystem.Bd09);
        Assert.InRange(bd.Longitude,116.4165,116.4168);Assert.InRange(bd.Latitude,39.9225,39.9229);
    }
    [Theory]
    [InlineData(2.35,48.85)] [InlineData(-122.42,37.77)] [InlineData(151.21,-33.87)]
    public void WgsAndOutsideGcjAreNotShifted(double longitude,double latitude)
    {
        Assert.Equal((longitude,latitude),MapCoordinateTransform.FromWgs84(longitude,latitude,MapSourceCoordinateSystem.Wgs84));
        Assert.Equal((longitude,latitude),MapCoordinateTransform.FromWgs84(longitude,latitude,MapSourceCoordinateSystem.Gcj02));
    }
    [Fact]public void GcjRasterControlPointMovesToWgsLocationAcrossTileBoundaries()
    {
        const int z=17;var expected=MapCoordinateTransform.ToTile(116.404,39.915,z);
        var gcj=MapCoordinateTransform.ToTile(116.41024449916938,39.91640428150164,z);
        var x=(int)expected.X;var y=(int)expected.Y;var plan=new RasterTileReprojection(z,x,y,MapSourceCoordinateSystem.Gcj02);
        var images=plan.RequiredTiles.ToDictionary(a=>a,a=>Image(a,(px,py)=>Math.Pow(px-gcj.X*256,2)+Math.Pow(py-gcj.Y*256,2)<25 ? (255,0,0,255):(0,0,0,255)));
        var rgba=plan.Render(images);var col=(int)((expected.X-x)*256);var row=(int)((expected.Y-y)*256);
        Assert.True(rgba[(row*256+col)*4]>220,"GCJ图中已知位置应落到WGS控制点上");
        Assert.True(plan.RequiredTiles.Count>1);Assert.All(Enumerable.Range(0,256*256),i=>Assert.Equal(255,rgba[i*4+3]));
    }
    [Theory] [InlineData(0,0,0)] [InlineData(16,53940,26421)] [InlineData(24,16777215,0)]
    public void IdentityPreservesPixelValuesAndWorldEdges(int z,int x,int y)
    {
        var plan=new RasterTileReprojection(z,x,y,MapSourceCoordinateSystem.Wgs84);
        var images=plan.RequiredTiles.ToDictionary(a=>a,a=>Image(a,(px,py)=>((int)px%251,(int)py%239,100,255)));
        var output=plan.Render(images);var original=images[new(z,x,y)].Rgba;
        Assert.Equal(original,output);
    }
    [Fact]public void MissingNeighborFailsInsteadOfShowingUncorrectedImage()
    {
        var p=MapCoordinateTransform.ToTile(116.404,39.915,16);var plan=new RasterTileReprojection(16,(int)p.X,(int)p.Y,MapSourceCoordinateSystem.Gcj02);
        Assert.Throws<ArgumentException>(()=>plan.Render(new Dictionary<RasterTileAddress,RasterTileImage>()));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(()=>new RasterTileReprojection(16,(int)p.X,(int)p.Y,MapSourceCoordinateSystem.Gcj02,cancelled.Token));
    }
    [Theory] [InlineData(-1,0,0)] [InlineData(25,0,0)] [InlineData(2,4,0)]
    public void InvalidTileRejected(int z,int x,int y)=>Assert.Throws<ArgumentOutOfRangeException>(()=>new RasterTileReprojection(z,x,y,MapSourceCoordinateSystem.Gcj02));
    private static RasterTileImage Image(RasterTileAddress tile,Func<double,double,(int R,int G,int B,int A)> color)
    {
        var bytes=new byte[256*256*4];
        for(var y=0;y<256;y++)for(var x=0;x<256;x++)
        {
            var c=color(tile.X*256d+x+.5,tile.Y*256d+y+.5);var i=(y*256+x)*4;
            bytes[i]=(byte)c.R;bytes[i+1]=(byte)c.G;bytes[i+2]=(byte)c.B;bytes[i+3]=(byte)c.A;
        }
        return new(256,256,bytes);
    }
}
