using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using Xunit;

namespace HikingGuide.Core.Tests;

public sealed class BRouterTileTests
{
    [Theory]
    [InlineData("N24E113", "E110_N20.rd5")]
    [InlineData("N25E115", "E115_N25.rd5")]
    [InlineData("S01W001", "W5_S5.rd5")]
    [InlineData("S05W005", "W5_S5.rd5")]
    [InlineData("N47E007", "E5_N45.rd5")]
    public void OneDegreeCell_MapsByFloorToFiveDegreeTile(string cell, string expected) =>
        Assert.Equal(expected, BRouterTile.ParseOneDegreeCell(cell).FileName);

    [Fact]
    public void TileAt_PreservesExactBoundary()
    {
        var tile = BRouterTile.At(new GeoCoordinate(20, 110));
        Assert.Equal("E110_N20.rd5", tile.FileName);
        Assert.Equal(20, tile.Bounds.MinLatitude);
        Assert.Equal(25, tile.Bounds.MaxLatitude);
        Assert.Equal(110, tile.Bounds.MinLongitude);
        Assert.Equal(115, tile.Bounds.MaxLongitude);
    }

    [Theory]
    [InlineData("../E110_N20.rd5")]
    [InlineData("E111_N20.rd5")]
    [InlineData("E110_N20.rd5.part")]
    [InlineData("W200_N20.rd5")]
    public void FileNameParser_RejectsTraversalAndInvalidGrid(string fileName) =>
        Assert.Throws<FormatException>(() => BRouterTile.ParseFileName(fileName));
}
