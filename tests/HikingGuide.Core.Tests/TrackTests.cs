using HikingGuide.Core.Models;
using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Tests;

public sealed class TrackTests
{
    [Fact]
    public void RecalculateDistances_ProducesIncreasingCumulativeDistances()
    {
        var track = CreateFourPointTrack();

        track.RecalculateDistances();
        var points = track.Segments[0].Points;

        Assert.Equal(0, points[0].DistanceFromStart);
        Assert.True(points[1].DistanceFromStart > points[0].DistanceFromStart);
        Assert.True(points[2].DistanceFromStart > points[1].DistanceFromStart);
        Assert.True(points[3].DistanceFromStart > points[2].DistanceFromStart);
        Assert.Equal(track.GetTotalLength(), points[3].DistanceFromStart, 6);
    }

    [Fact]
    public void Reverse_ReversesPointOrderAndRecalculatesDistances()
    {
        var track = CreateFourPointTrack();
        var originalIds = track.Segments[0].Points.Select(point => point.Id).ToArray();
        var originalLength = track.GetTotalLength();

        track.Reverse();
        var reversed = track.Segments[0].Points;

        Assert.Equal(originalIds.Reverse(), reversed.Select(point => point.Id));
        Assert.Equal(0, reversed[0].DistanceFromStart);
        Assert.Equal(originalLength, track.GetTotalLength(), 6);
        Assert.Equal(originalLength, reversed[^1].DistanceFromStart, 6);
    }

    [Fact]
    public void BoundingBox_UsesEveryPoint()
    {
        var track = CreateFourPointTrack();

        var box = Assert.IsType<GeoBoundingBox>(track.GetBoundingBox());

        Assert.Equal(30, box.MinLatitude);
        Assert.Equal(30.03, box.MaxLatitude);
        Assert.Equal(110, box.MinLongitude);
        Assert.Equal(110.03, box.MaxLongitude);
    }

    [Fact]
    public void NoSegments_IsEmptyAndSafe()
    {
        var track = new Track();

        track.RecalculateDistances();
        track.Reverse();

        Assert.True(track.IsEmpty);
        Assert.Equal(0, track.GetPointCount());
        Assert.Equal(0, track.GetTotalLength());
        Assert.Null(track.GetBoundingBox());
        Assert.Null(track.StartPoint);
        Assert.Null(track.EndPoint);
    }

    [Fact]
    public void EmptySegment_IsEmptyAndSafe()
    {
        var track = new Track { Segments = [new TrackSegment()] };

        track.RecalculateDistances();
        track.Reverse();

        Assert.True(track.IsEmpty);
        Assert.Equal(0, track.GetTotalLength());
        Assert.Null(track.GetBoundingBox());
    }

    [Fact]
    public void SinglePoint_HasZeroLengthAndValidBounds()
    {
        var point = Point(30, 110);
        var track = new Track { Segments = [new TrackSegment { Points = [point] }] };

        track.RecalculateDistances();

        Assert.False(track.IsEmpty);
        Assert.Equal(1, track.GetPointCount());
        Assert.Equal(0, track.GetTotalLength());
        Assert.Equal(0, point.DistanceFromStart);
        Assert.NotNull(track.GetBoundingBox());
    }

    [Fact]
    public void MultipleSegments_DoNotAddFictitiousConnectionDistance()
    {
        var first = new TrackSegment
        {
            Points = [Point(30, 110), Point(30, 110.01), Point(30, 110.02)]
        };
        var second = new TrackSegment
        {
            Points = [Point(40, 120), Point(40, 120.01)]
        };
        var track = new Track { Segments = [first, second] };

        var expectedLength = first.GetLength() + second.GetLength();
        track.RecalculateDistances();

        Assert.Equal(5, track.GetPointCount());
        Assert.Equal(expectedLength, track.GetTotalLength(), 6);
        Assert.Equal(first.GetLength(), second.Points[0].DistanceFromStart, 6);
        Assert.Equal(expectedLength, second.Points[^1].DistanceFromStart, 6);
    }

    [Fact]
    public void MultipleSegments_ReverseSegmentAndPointOrder()
    {
        var a = Point(30, 110);
        var b = Point(30, 110.01);
        var c = Point(31, 111);
        var d = Point(31, 111.01);
        var track = new Track
        {
            Segments =
            [
                new TrackSegment { Points = [a, b] },
                new TrackSegment { Points = [c, d] }
            ]
        };

        track.Reverse();

        Assert.Equal(new[] { d.Id, c.Id }, track.Segments[0].Points.Select(point => point.Id));
        Assert.Equal(new[] { b.Id, a.Id }, track.Segments[1].Points.Select(point => point.Id));
        Assert.Equal(0, d.DistanceFromStart);
        Assert.Equal(track.Segments[0].GetLength(), b.DistanceFromStart, 6);
    }

    private static Track CreateFourPointTrack() => new()
    {
        Segments =
        [
            new TrackSegment
            {
                Points =
                [
                    Point(30, 110),
                    Point(30.01, 110.01),
                    Point(30.02, 110.02),
                    Point(30.03, 110.03)
                ]
            }
        ]
    };

    private static TrackPoint Point(double latitude, double longitude) => new()
    {
        Latitude = latitude,
        Longitude = longitude
    };
}
