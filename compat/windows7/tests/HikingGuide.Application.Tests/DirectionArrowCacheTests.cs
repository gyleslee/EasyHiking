using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class DirectionArrowCacheTests
{
    [Fact]
    public void HundredMeterLine_HasRealTwelveMeterSpacing_AndReversesDirection()
    {
        var track = new Track { Segments = [new TrackSegment { Points =
        [
            new TrackPoint { Latitude = 0, Longitude = 0 },
            new TrackPoint { Latitude = 0, Longitude = 100d / 111_319.49 }
        ] }] };
        var cache = new DirectionArrowCache();
        var forward = cache.Get(track);
        Assert.InRange(forward.Count, 8, 9);
        Assert.Same(forward, cache.Get(track));
        Assert.All(forward, arrow =>
        {
            Assert.Equal(0, arrow.Coordinate.Latitude, 9);
            Assert.InRange(arrow.BearingDegrees, 89.99, 90.01);
        });
        for (var index = 1; index < forward.Count; index++)
            Assert.Equal(12, forward[index].DistanceFromPartStart - forward[index - 1].DistanceFromPartStart, 8);
        track.Reverse();
        var reversed = cache.Get(track);
        Assert.NotSame(forward, reversed);
        Assert.All(reversed, arrow => Assert.InRange(arrow.BearingDegrees, 269.99, 270.01));
    }

    [Fact]
    public void GapHasNoArrows_ConnectorIsFormalGeometryAndHasArrows()
    {
        var track = new Track { Segments =
        [
            new TrackSegment { Points =
            [new TrackPoint { Latitude = 0, Longitude = 0 },
             new TrackPoint { Latitude = 0, Longitude = .0002 }] },
            new TrackSegment { Points =
            [new TrackPoint { Latitude = 0, Longitude = .01 },
             new TrackPoint { Latitude = 0, Longitude = .0102 }] }
        ] };
        var without = DirectionArrowCache.Sample(track);
        Assert.DoesNotContain(without, arrow => arrow.Coordinate.Longitude > .0002 &&
            arrow.Coordinate.Longitude < .01);
        var connector = new TrackSegment { Name = "人工直线连接", Points =
        [new TrackPoint { Latitude = 0, Longitude = .0002, IsInsertedByEditor = true },
         new TrackPoint { Latitude = 0, Longitude = .01, IsInsertedByEditor = true }] };
        track.Segments.Insert(1, connector);
        var with = DirectionArrowCache.Sample(track);
        Assert.Contains(with, arrow => arrow.SegmentId == connector.Id);
    }
}
