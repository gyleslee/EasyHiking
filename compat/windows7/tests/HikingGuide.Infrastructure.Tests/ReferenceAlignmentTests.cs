using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Calibration;

namespace HikingGuide.Infrastructure.Tests;

public sealed class ReferenceAlignmentTests
{
    [Theory]
    [InlineData(" 起点：后山村 ", "后山村")]
    [InlineData("徒步起点:冷达", "冷达")]
    [InlineData("终点：安久村", "安久村")]
    public void NormalizesKnownPrefixes(string name, string expected) => Assert.Equal(expected, ReferenceAlignment.NormalizeName(name));

    [Fact]
    public void SequenceDistanceChoosesLaterOccurrenceAndDoesNotMutateTrack()
    {
        var track = new Track();
        track.Segments.Add(new() { Points = [
            new() { Latitude = 0, Longitude = 0 }, new() { Latitude = 0, Longitude = 0.01 },
            new() { Latitude = 0, Longitude = 0 } ] });
        var length = track.GetTotalLength();
        var markers = new Waypoint[] { new() { Name = "起点：甲", Latitude = 0, Longitude = 0 },
            new() { Name = "乙", Latitude = 0, Longitude = 0.01 }, new() { Name = "终点：甲", Latitude = 0, Longitude = 0 } };
        var rows = new[] { Row("甲", 0), Row("乙", length / 2000), Row("甲", length / 2000) };
        var aligned = ReferenceAlignment.Align(track, markers, rows, new());
        Assert.True(aligned[1].EligibleForFit);
        Assert.True(aligned[2].EligibleForFit);
        Assert.Equal(length, aligned[2].EndDistanceMeters!.Value, 6);
        Assert.Equal(3, track.GetPointCount());
    }

    [Fact]
    public void MissingMarkerAndCompositeNeverEnterFit()
    {
        var track = new Track();
        track.Segments.Add(new() { Points = [new() { Latitude = 0, Longitude = 0 }, new() { Latitude = 0, Longitude = 0.01 }] });
        var markers = new Waypoint[] { new() { Name = "甲", Latitude = 0, Longitude = 0 }, new() { Name = "向巴措", Latitude = 0, Longitude = 0.01 } };
        var aligned = ReferenceAlignment.Align(track, markers, [Row("甲", 0), Row("向巴措", 2, true), Row("不存在", 1)], new());
        Assert.Equal("Composite", aligned[1].Status);
        Assert.False(aligned[1].EligibleForFit);
        Assert.Equal("Unmatched", aligned[2].Status);
    }

    private static LocusReferenceSegment Row(string name, double km, bool composite = false) => new("参考", 1, name,
        new Dictionary<string, CachedReferenceCell> { ["分段距离KM"] = new("A1", km.ToString(), km, null, "0.00", null) }, 0, true, composite);
}
