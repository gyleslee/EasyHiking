using HikingGuide.Application.Routing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class WorldTileSelectionTests
{
    [Fact]
    public void PointAndRectangle_OnlySelectAvailableTiles()
    {
        var available = new HashSet<BRouterTile>
        {
            new(20, 110), new(20, 115), new(25, 110)
        };
        var selection = new WorldTileSelection(available);
        Assert.True(selection.Toggle(new GeoCoordinate(22, 112)));
        Assert.Contains(new BRouterTile(20, 110), selection.Selected);
        Assert.True(selection.Toggle(new GeoCoordinate(22, 112)));
        Assert.Empty(selection.Selected);
        Assert.False(selection.Toggle(new GeoCoordinate(22, 122)));
        Assert.Equal(3, selection.SelectIntersecting(new GeoBoundingBox(21, 26, 111, 116)));
        Assert.Equal(3, selection.Selected.Count);
        Assert.Equal(1, selection.SelectIntersecting(new GeoBoundingBox(20, 24, 115, 120), true));
        Assert.DoesNotContain(new BRouterTile(20, 115), selection.Selected);
    }
}
