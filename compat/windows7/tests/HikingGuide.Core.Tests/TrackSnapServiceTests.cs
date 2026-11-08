using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Tests;

public sealed class TrackSnapServiceTests
{
    private readonly TrackSnapService _snap = new();

    [Fact]
    public void MidpointProjectsOntoLineRatherThanVertex()
    {
        var track = Track((0, 0), (0, 0.02));
        var position = _snap.Snap(track, new GeoCoordinate(0.001, 0.01)).Position!;
        Assert.Equal(0.5, position.Fraction, 3);
        Assert.Equal(0.01, position.SnappedCoordinate.Longitude, 5);
        Assert.Equal(0, position.SnappedCoordinate.Latitude, 5);
        Assert.Equal(track.GetTotalLength() / 2, position.DistanceFromTrackStart, 2);
    }

    [Fact]
    public void TwoSegmentsDoNotGainImaginaryConnectingLine()
    {
        var track = Track((0, 0), (0, 0.01));
        track.Segments.Add(new TrackSegment { Points = [Point(0, 0.1), Point(0, 0.11)] });
        var position = _snap.Snap(track, new GeoCoordinate(0, 0.055)).Position!;
        Assert.True(position.DistanceToTrack > 4000);
        Assert.Equal(0, position.SegmentIndex);
    }

    [Fact]
    public void RepeatedGeometryUsesEarlierMileageWhenDistancesTie()
    {
        var track = Track((0, 0), (0, 0.01), (0, 0));
        var position = _snap.Snap(track, new GeoCoordinate(0, 0.005)).Position!;
        Assert.Equal(0, position.StartPointIndex);
        Assert.InRange(position.DistanceFromTrackStart, 500, 600);
    }

    [Fact]
    public void DegeneratePointAndFarCoordinateReturnValidResults()
    {
        var track = Track((20, 110), (20, 110));
        var position = _snap.Snap(track, new GeoCoordinate(21, 111)).Position!;
        Assert.Equal(0, position.Fraction);
        Assert.True(position.DistanceToTrack > 100000);
    }

    [Fact]
    public void GeometryFingerprintIgnoresNameButChangesOnReverse()
    {
        var track = Track((0, 0), (0, 0.01));
        var before = _snap.GetGeometryFingerprint(track);
        track.Name = "新名称";
        Assert.Equal(before, _snap.GetGeometryFingerprint(track));
        track.Reverse();
        Assert.NotEqual(before, _snap.GetGeometryFingerprint(track));
    }

    [Fact]
    public void CandidateListIsOrderedAndStable()
    {
        var track = Track((0, 0), (0, 0.01), (0.01, 0.01));
        var candidates = _snap.SnapCandidates(track, new GeoCoordinate(0.001, 0.009), 2);
        Assert.Equal(2, candidates.Count);
        Assert.True(candidates[0].Position!.DistanceToTrack <= candidates[1].Position!.DistanceToTrack);
        Assert.Equal(candidates[0], _snap.SnapCandidates(track, new GeoCoordinate(0.001, 0.009), 2)[0]);
    }

    [Theory]
    [InlineData(0, 0.002, 0, 0.002, 0)]
    [InlineData(0, 0.008, 0, 0.008, 0)]
    [InlineData(0.001, 0.005, 0, 0.005, 100)]
    [InlineData(-0.001, 0.005, 0, 0.005, 100)]
    [InlineData(0, -0.001, 0, 0, 100)]
    [InlineData(0, 0.011, 0, 0.01, 100)]
    public void HorizontalProjectionCoversInteriorEndpointsAndOutside(
        double latitude, double longitude, double expectedLat, double expectedLon, double minimumDistance)
    {
        var position = _snap.Snap(Track((0, 0), (0, 0.01)), new GeoCoordinate(latitude, longitude)).Position!;
        Assert.Equal(expectedLat, position.SnappedCoordinate.Latitude, 7);
        Assert.Equal(expectedLon, position.SnappedCoordinate.Longitude, 7);
        Assert.True(position.DistanceToTrack >= minimumDistance);
    }

    [Theory]
    [InlineData(0.002, 0, 0.002, 0)]
    [InlineData(0.008, 0, 0.008, 0)]
    [InlineData(0.005, 0.001, 0.005, 0)]
    [InlineData(0.005, -0.001, 0.005, 0)]
    [InlineData(-0.001, 0, 0, 0)]
    [InlineData(0.011, 0, 0.01, 0)]
    public void VerticalProjectionCoversInteriorEndpointsAndOutside(
        double latitude, double longitude, double expectedLat, double expectedLon)
    {
        var position = _snap.Snap(Track((0, 0), (0.01, 0)), new GeoCoordinate(latitude, longitude)).Position!;
        Assert.Equal(expectedLat, position.SnappedCoordinate.Latitude, 7);
        Assert.Equal(expectedLon, position.SnappedCoordinate.Longitude, 7);
    }

    [Fact]
    public void MultiSegmentMileageExcludesJumpBetweenSegments()
    {
        var track = Track((0, 0), (0, 0.01));
        track.Segments.Add(new TrackSegment { Points = [Point(1, 1), Point(1, 1.01)] });
        var position = _snap.Snap(track, new GeoCoordinate(1, 1.005)).Position!;
        Assert.Equal(1, position.SegmentIndex);
        Assert.InRange(position.DistanceFromSegmentStart, 500, 600);
        Assert.InRange(position.DistanceFromTrackStart, 1600, 1700);
    }

    [Fact]
    public void ParallelSegmentsExposeAlternativeCandidate()
    {
        var track = Track((0, 0), (0, 0.01), (0.00005, 0.01), (0.00005, 0));
        var candidates = _snap.SnapCandidates(track, new GeoCoordinate(0.000025, 0.005), 4);
        Assert.Contains(candidates, c => c.Position!.StartPointIndex == 0);
        Assert.Contains(candidates, c => c.Position!.StartPointIndex == 2);
    }

    [Fact]
    public void RepeatedPointsDoNotProduceNaNOrInfinity()
    {
        var track = Track((0, 0), (0, 0), (0, 0), (0, 0.01));
        var position = _snap.Snap(track, new GeoCoordinate(0.001, 0.005)).Position!;
        Assert.True(double.IsFinite(position.DistanceToTrack));
        Assert.True(double.IsFinite(position.Fraction));
        Assert.Equal(2, position.StartPointIndex);
    }

    [Fact]
    public void EmptyTrackReturnsStructuredFailure()
    {
        var result = _snap.Snap(new Track(), new GeoCoordinate(1, 1));
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void SnapDoesNotModifyOriginalTrackPoints()
    {
        var track = Track((0, 0), (0.002, 0.005), (0.004, 0.01));
        var before = _snap.GetGeometryFingerprint(track);
        _ = _snap.SnapCandidates(track, new GeoCoordinate(0.003, 0.006), 3);
        Assert.Equal(before, _snap.GetGeometryFingerprint(track));
    }

    [Fact]
    public void ReversalInvalidatesCachedMileageWithoutMovingGeometry()
    {
        var track = Track((0, 0), (0, 0.01));
        var coordinate = new GeoCoordinate(0, 0.002);
        var first = _snap.Snap(track, coordinate).Position!;
        track.Reverse();
        var second = _snap.Snap(track, coordinate).Position!;
        Assert.Equal(first.SnappedCoordinate, second.SnappedCoordinate);
        Assert.True(second.DistanceFromTrackStart > first.DistanceFromTrackStart);
    }

    [Theory]
    [InlineData(-40, -120)]
    [InlineData(20, 100)]
    [InlineData(60, 10)]
    [InlineData(-5, 170)]
    [InlineData(0, 0)]
    [InlineData(80, -179)]
    public void IndexedNearestAgreesWithUnprunedCandidatesEvenForFarQueries(double latitude, double longitude)
    {
        var track = Track(Enumerable.Range(0, 100).Select(index =>
            (Lat: 10d + Math.Sin(index * .43) * 4, Lon: 80d + index * .3)).ToArray());
        var coordinate = new GeoCoordinate(latitude, longitude);
        var indexed = _snap.Snap(track, coordinate).Position!;
        var unpruned = _snap.SnapCandidates(track, coordinate, 200)[0].Position!;
        Assert.Equal(unpruned.StartPointIndex, indexed.StartPointIndex);
        Assert.Equal(unpruned.DistanceToTrack, indexed.DistanceToTrack, 6);
    }

    private static Track Track(params (double Lat, double Lon)[] coords) => new()
    {
        Segments = [new TrackSegment { Points = coords.Select(c => Point(c.Lat, c.Lon)).ToList() }]
    };
    private static TrackPoint Point(double lat, double lon) => new() { Latitude = lat, Longitude = lon };
}
