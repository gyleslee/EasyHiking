using HikingGuide.Core.Elevation;

namespace HikingGuide.Core.Tests;

public sealed class HillshadeOverlayTests
{
    [Theory]
    [InlineData(0)] [InlineData(500)] [InlineData(-50)]
    public void FlatTerrainNeverCoversBasemap(double elevation) =>
        Assert.Equal(0, HillshadeOverlay.Opacity(elevation, elevation, elevation, elevation, 10));

    [Fact]
    public void SunFacingSlopeIsTransparentAndOppositeSlopeHasShadow()
    {
        Assert.Equal(0, HillshadeOverlay.Opacity(0, 20, 0, 0, 10));
        Assert.InRange(HillshadeOverlay.Opacity(20, 0, 0, 0, 10), .5, 1);
    }

    [Fact]
    public void BackSlopeDarkensContinuouslyWithoutStepAtFlatTerrain()
    {
        var previous=0d;
        for(var i=0;i<=100;i++)
        {
            var alpha=HillshadeOverlay.Opacity(i*.2,0,0,0,10);
            Assert.InRange(alpha,previous,1);
            Assert.InRange(alpha-previous,0,.03);
            previous=alpha;
        }
        Assert.InRange(HillshadeOverlay.Opacity(.001,0,0,0,10),0,1e-8);
    }

    [Fact]
    public void DeepShadowSaturatesAndIsIndependentOfAbsoluteElevation()
    {
        Assert.Equal(1,HillshadeOverlay.Opacity(100,0,0,0,10));
        Assert.Equal(HillshadeOverlay.Opacity(20,0,0,0,10),HillshadeOverlay.Opacity(1020,1000,1000,1000,10),12);
    }

    [Theory]
    [InlineData(double.NaN,0,0,0,10)] [InlineData(0,double.PositiveInfinity,0,0,10)]
    [InlineData(0,0,0,0,0)] [InlineData(0,0,0,0,-1)] [InlineData(0,0,0,0,double.NaN)]
    public void InvalidSamplesStayTransparent(double west,double east,double north,double south,double step) =>
        Assert.Equal(0,HillshadeOverlay.Opacity(west,east,north,south,step));
}
