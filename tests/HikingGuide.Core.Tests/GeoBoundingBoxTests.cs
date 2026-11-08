using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Tests;

public sealed class GeoBoundingBoxTests
{
    [Fact]
    public void ExpandToInclude_UpdatesAllBoundaries()
    {
        var box = new GeoBoundingBox(30, 30, 110, 110);
        box.ExpandToInclude(new GeoCoordinate(29.5, 111.5));
        box.ExpandToInclude(new GeoCoordinate(31.5, 109.5));

        Assert.Equal(29.5, box.MinLatitude);
        Assert.Equal(31.5, box.MaxLatitude);
        Assert.Equal(109.5, box.MinLongitude);
        Assert.Equal(111.5, box.MaxLongitude);
        Assert.Equal(2, box.LatitudeSpan);
        Assert.Equal(2, box.LongitudeSpan);
    }

    [Fact]
    public void Contains_ReturnsExpectedResult()
    {
        var box = new GeoBoundingBox(29, 31, 109, 111);

        Assert.True(box.Contains(new GeoCoordinate(30, 110)));
        Assert.False(box.Contains(new GeoCoordinate(32, 110)));
    }

    [Fact]
    public void ToEnvelope_UsesLongitudeAsXAndLatitudeAsY()
    {
        var envelope = new GeoBoundingBox(29, 31, 109, 111).ToEnvelope();

        Assert.Equal(109, envelope.MinX);
        Assert.Equal(111, envelope.MaxX);
        Assert.Equal(29, envelope.MinY);
        Assert.Equal(31, envelope.MaxY);
    }
}
