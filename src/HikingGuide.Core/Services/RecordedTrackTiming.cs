using HikingGuide.Core.Models;

namespace HikingGuide.Core.Services;

/// <summary>Recorded time, independently of planned itinerary time. Calibrated against five 2026 samples.</summary>
public sealed record RecordedTimingProfile(double MaximumMovingIntervalSeconds = 20)
{
    public void Validate()
    {
        if (!double.IsFinite(MaximumMovingIntervalSeconds) || MaximumMovingIntervalSeconds <= 0)
            throw new ArgumentException("运动时间间隔上限必须大于0。");
    }
}

public sealed record RecordedTimingResult(DateTimeOffset? Start, DateTimeOffset? End,
    TimeSpan? Moving, TimeSpan? Elapsed, TimeSpan? Rest, int TimedPoints, int TotalPoints,
    int InvalidIntervals)
{
    public double? MovingSpeed(double distanceMeters) => Moving?.TotalHours > 0 ? distanceMeters / 1000 / Moving.Value.TotalHours : null;
    public double? ElapsedSpeed(double distanceMeters) => Elapsed?.TotalHours > 0 ? distanceMeters / 1000 / Elapsed.Value.TotalHours : null;
}

public static class RecordedTrackTiming
{
    public static RecordedTimingResult Calculate(Track track, RecordedTimingProfile? profile = null)
    {
        profile ??= new(); profile.Validate();
        var points = track.Segments.SelectMany(s => s.Points).ToArray();
        var times = points.Where(p => p.Timestamp.HasValue).Select(p => p.Timestamp!.Value).ToArray();
        if (times.Length < 2) return new(times.FirstOrDefaultNullable(), times.LastOrDefaultNullable(), null, null, null, times.Length, points.Length, 0);
        var intervals = new List<(long Start, long End)>(); var invalid = 0;
        foreach (var segment in track.Segments)
        {
            var diffs = segment.Points.Zip(segment.Points.Skip(1)).Where(p => p.First.Timestamp.HasValue && p.Second.Timestamp.HasValue)
                .Select(p => (p.Second.Timestamp!.Value - p.First.Timestamp!.Value).Ticks).ToArray();
            var reversed = diffs.Count(d => d < 0) > diffs.Count(d => d > 0);
            foreach (var (a, b) in segment.Points.Zip(segment.Points.Skip(1)))
            {
                if (a.Timestamp is not { } t || b.Timestamp is not { } u) continue;
                var delta = (u - t).Ticks;
                if (delta == 0) continue;
                if ((delta < 0) != reversed) { invalid++; continue; }
                var start = Math.Min(t.UtcTicks, u.UtcTicks);
                intervals.Add((start, start + Math.Min(Math.Abs(delta), TimeSpan.FromSeconds(profile.MaximumMovingIntervalSeconds).Ticks)));
            }
        }
        // Avoid double counting duplicated/overlapping segments.
        long movingTicks = 0, end = long.MinValue;
        foreach (var interval in intervals.OrderBy(p => p.Start))
        { movingTicks += Math.Max(0, interval.End - Math.Max(end, interval.Start)); end = Math.Max(end, interval.End); }
        var first = times.Min(); var last = times.Max(); var elapsed = last - first;
        var moving = TimeSpan.FromTicks(Math.Min(elapsed.Ticks, movingTicks));
        return new(first, last, moving, elapsed, elapsed - moving, times.Length, points.Length, invalid);
    }

    public static DateTimeOffset? TimeAtDistance(Track track, double distance)
    {
        foreach (var segment in track.Segments)
        {
            var points = segment.Points;
            if (points.Count == 0 || distance < points[0].DistanceFromStart || distance > points[^1].DistanceFromStart) continue;
            var low = 0; var high = points.Count - 1;
            while (low < high) { var mid = (low + high) / 2; if (points[mid].DistanceFromStart < distance) low = mid + 1; else high = mid; }
            var p = points[low];
            if (Math.Abs(p.DistanceFromStart - distance) < 1e-6) return p.Timestamp;
            if (low == 0) continue; var a = points[low - 1];
            return a.Timestamp is { } t && p.Timestamp is { } u && p.DistanceFromStart > a.DistanceFromStart
                ? t + TimeSpan.FromTicks((long)((u-t).Ticks * (distance-a.DistanceFromStart)/(p.DistanceFromStart-a.DistanceFromStart))) : null;
        }
        return null;
    }
    private static DateTimeOffset? FirstOrDefaultNullable(this DateTimeOffset[] values) => values.Length > 0 ? values[0] : null;
    private static DateTimeOffset? LastOrDefaultNullable(this DateTimeOffset[] values) => values.Length > 0 ? values[^1] : null;
}
