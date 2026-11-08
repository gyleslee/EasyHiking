using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class TrackPointEditMultiSegmentTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CompleteLogicalSection_PicksRealPhysicalPartWithoutChangingItsIndex(int physicalPartIndex)
    {
        var sectionId = Guid.NewGuid();
        var track = new Track { Name = "一条逻辑分段，两个真实连续 Part" };
        for (var part = 0; part < 2; part++)
            track.Segments.Add(new TrackSegment
            {
                SplitSectionId = sectionId,
                SplitSectionName = "完整分段",
                Points = [
                    new TrackPoint { Latitude = 23 + part * .02, Longitude = 113 },
                    new TrackPoint { Latitude = 23 + part * .02, Longitude = 113.01 },
                    new TrackPoint { Latitude = 23 + part * .02, Longitude = 113.02 }
                ]
            });
        track.RecalculateDistances();
        var completeSection = Assert.Single(track.GetSplitSections());
        Assert.Equal(track.Segments.Select(part => part.Id), completeSection.GeometryParts.Select(part => part.Id));
        var expectedPart = track.Segments[physicalPartIndex];
        var pick = new TrackPointEditIndex().Pick(track, expectedPart.Points[1].Coordinate, 18);
        Assert.NotNull(pick);
        Assert.Equal(physicalPartIndex, pick.SelectedPoint.SegmentIndex);
        Assert.Equal(physicalPartIndex, pick.LinePosition.SegmentIndex);
        Assert.Equal(expectedPart.Id, pick.SelectedPoint.SegmentId);
        Assert.Equal(expectedPart.Id, pick.LinePosition.TrackSegmentId);
        Assert.Equal(expectedPart.Points[1].Id, pick.SelectedPoint.PointId);
        Assert.All(pick.VisibleHandles, handle =>
        {
            Assert.Equal(physicalPartIndex, handle.SegmentIndex);
            Assert.Equal(expectedPart.Id, handle.SegmentId);
            Assert.Contains(handle.PointId, expectedPart.Points.Select(point => point.Id));
        });
        Assert.Equal(expectedPart.Points[1].DistanceFromStart, pick.LinePosition.DistanceFromTrackStart, 6);
    }
}
