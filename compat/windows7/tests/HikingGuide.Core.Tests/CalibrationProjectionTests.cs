using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Tests;

public sealed class CalibrationProjectionTests
{
    [Fact]
    public void OutAndBackRetainsBothPassagesWithoutFixedCandidateCount()
    {
        var track = new Track();
        track.Segments.Add(new TrackSegment { Points = [
            new() { Latitude = 0, Longitude = 0 }, new() { Latitude = 0, Longitude = 0.01 },
            new() { Latitude = 0, Longitude = 0 } ] });
        var result = new TrackSnapService().SnapAllWithinTolerance(track, new GeoCoordinate(0, 0.005), 10, 1);
        Assert.Equal(2, result.Count);
        Assert.True(result[1].DistanceFromTrackStart > result[0].DistanceFromTrackStart + 1000);
        Assert.All(result, p => Assert.True(p.DistanceToTrack < 0.001));
        Assert.Equal(3, track.GetPointCount());
    }

    [Fact]
    public void OutsideToleranceReturnsNoInventedProjection()
    {
        var track = new Track();
        track.Segments.Add(new TrackSegment { Points = [
            new() { Latitude = 0, Longitude = 0 }, new() { Latitude = 0, Longitude = 0.01 } ] });
        Assert.Empty(new TrackSnapService().SnapAllWithinTolerance(track, new GeoCoordinate(1, 0.005), 10, 1));
    }
}
