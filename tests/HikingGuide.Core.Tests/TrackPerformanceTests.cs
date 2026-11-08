using System.Diagnostics;
using HikingGuide.Core.Models;
using Xunit.Abstractions;

namespace HikingGuide.Core.Tests;

public sealed class TrackPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void CoreOperations_AreLinearAndComplete(int pointCount)
    {
        var stopwatch = Stopwatch.StartNew();
        var points = new List<TrackPoint>(pointCount);
        for (var index = 0; index < pointCount; index++)
        {
            points.Add(new TrackPoint
            {
                Latitude = 30 + (index % 1_000) * 0.00001,
                Longitude = 110 + index * 0.000001
            });
        }

        var track = new Track { Segments = [new TrackSegment { Points = points }] };
        var createElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        track.RecalculateDistances();
        var recalculateElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var totalLength = track.GetTotalLength();
        var lengthElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var boundingBox = track.GetBoundingBox();
        var boundsElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        track.Reverse();
        var reverseElapsed = stopwatch.Elapsed;

        output.WriteLine("点数：{0:N0}", pointCount);
        output.WriteLine("创建耗时：{0:F3} ms", createElapsed.TotalMilliseconds);
        output.WriteLine("累计距离计算耗时：{0:F3} ms", recalculateElapsed.TotalMilliseconds);
        output.WriteLine("总长度计算耗时：{0:F3} ms", lengthElapsed.TotalMilliseconds);
        output.WriteLine("范围计算耗时：{0:F3} ms", boundsElapsed.TotalMilliseconds);
        output.WriteLine("反转耗时：{0:F3} ms", reverseElapsed.TotalMilliseconds);

        Assert.Equal(pointCount, track.GetPointCount());
        Assert.True(totalLength > 0);
        Assert.NotNull(boundingBox);
        Assert.Equal(0, track.StartPoint?.DistanceFromStart);
        Assert.True(recalculateElapsed < TimeSpan.FromSeconds(30));
        Assert.True(lengthElapsed < TimeSpan.FromSeconds(30));
        Assert.True(boundsElapsed < TimeSpan.FromSeconds(30));
        Assert.True(reverseElapsed < TimeSpan.FromSeconds(30));
    }
}
