using HikingGuide.Core.Elevation;

namespace HikingGuide.Core.Guides;

/// <summary>P10只消费P9已生成的坡度区间，不建立另一套采样、平滑或累计高程算法。</summary>
public static class GuideTimingService
{
    public const double SteepUphillThresholdPercent = 35;
    public const double RestMultiplier = 65d / 60;

    public static GuideTerrainBreakdown SplitTerrain(TerrainDistanceClassificationResult classification,
        double startMeters, double endMeters)
    {
        if (!double.IsFinite(startMeters) || !double.IsFinite(endMeters) || startMeters < 0 || endMeters < startMeters)
            throw new ArgumentException("攻略分段链程范围无效。");
        var up = 0d; var down = 0d; var flat = 0d; var gentle = 0d; var steep = 0d; var missing = 0d;
        var threshold = classification.Profile.FlatSlopeThresholdPercent;
        var tolerance = classification.Profile.NumericalSlopeTolerancePercent;
        foreach (var interval in classification.Intervals)
        {
            var length = Math.Max(0, Math.Min(endMeters, interval.EndMeters) - Math.Max(startMeters, interval.StartMeters));
            if (length == 0) continue;
            if (interval.SlopePercent is not double slope || !double.IsFinite(slope)) { missing += length; continue; }
            if (slope > threshold + tolerance)
            {
                up += length;
                if (slope >= SteepUphillThresholdPercent) steep += length; else gentle += length;
            }
            else if (slope < -threshold - tolerance) down += length;
            else flat += length;
        }
        missing += Math.Max(0, endMeters - startMeters - (up + down + flat + missing));
        return new(up, down, flat, gentle, steep, missing);
    }

    public static double? AverageHours(GuideSegmentStatistics segment, GuideCalculationSettings settings)
    {
        settings.Validate();
        if (!segment.IsComplete || settings.FlatDownSpeedKmh is not double flatSpeed ||
            settings.GentleUpSpeedKmh is not double gentleSpeed || settings.SteepUpSpeedKmh is not double steepSpeed ||
            segment.DownhillDistanceMeters is not double down || segment.FlatDistanceMeters is not double flat ||
            segment.GentleUphillDistanceMeters is not double gentle || segment.SteepUphillDistanceMeters is not double steep) return null;
        return ((down + flat) / flatSpeed + gentle / gentleSpeed + steep / steepSpeed) / 1000 * RestMultiplier;
    }

    public static double? NaismithHours(GuideSegmentStatistics segment) => segment.IsComplete && segment.AscentMeters is double ascent
        ? segment.DistanceMeters / 1000 / 5 + ascent / 600 : null;

    public static GuidePlanTimingResult CalculatePlan(Guid trackId, IReadOnlyList<Guid> orderedNodeIds,
        GuideTravelPlan plan, GuideManualDataStore manual, IReadOnlyDictionary<string, GuideSegmentStatistics>? statistics = null)
    {
        if (orderedNodeIds.Count < 2) throw new ArgumentException("每日行程至少需要两个攻略节点。");
        if (orderedNodeIds.Distinct().Count() != orderedNodeIds.Count) throw new ArgumentException("攻略节点标识重复。");
        var index = orderedNodeIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var invalid = plan.NeedsReview || plan.DailyStartNodeIds.Any(id => !index.ContainsKey(id) || id == orderedNodeIds[^1]);
        var starts = plan.DailyStartNodeIds.Where(index.ContainsKey).Select(id => index[id])
            .Where(i => i < orderedNodeIds.Count - 1).Append(0).Distinct().Order().ToArray();
        var arrivals = orderedNodeIds.ToDictionary(id => id, _ => (GuidePlannedArrival?)null);
        var days = new List<GuideDailyItinerary>();
        var departures = new Dictionary<Guid, GuidePlannedArrival>();
        for (var day = 0; day < starts.Length; day++)
        {
            var start = starts[day]; var end = day + 1 < starts.Length ? starts[day + 1] : orderedNodeIds.Count - 1;
            var startId = orderedNodeIds[start]; var departure = plan.DepartureTimes.GetValueOrDefault(startId);
            if (!invalid && departure is TimeOnly startTime) departures[startId] = new(day + 1, startTime, 0, 0);
            double? elapsed = invalid || departure is null ? null : 0;
            double? totalHours = 0, totalDistance = statistics is null ? null : 0, ascent = statistics is null ? null : 0, descent = statistics is null ? null : 0;
            var segments = new List<GuideSegmentKey>();
            for (var i = start + 1; i <= end; i++)
            {
                var key = new GuideSegmentKey(trackId, orderedNodeIds[i - 1], orderedNodeIds[i]); segments.Add(key);
                var hours = manual.Segments.GetValueOrDefault(key.StorageKey)?.PlannedDurationHours;
                if (hours is double h && (!double.IsFinite(h) || h < 0)) throw new ArgumentException("计划用时不能为负数或无效数值。");
                totalHours = Add(totalHours, hours); elapsed = Add(elapsed, hours);
                var stat = statistics?.GetValueOrDefault(key.StorageKey);
                totalDistance = Add(totalDistance, stat?.DistanceMeters); ascent = Add(ascent, stat?.AscentMeters); descent = Add(descent, stat?.DescentMeters);
                if (elapsed is double elapsedHours && departure is TimeOnly time)
                {
                    var minutes = time.ToTimeSpan().TotalMinutes + elapsedHours * 60;
                    if (!double.IsFinite(minutes) || minutes / 1440 > int.MaxValue) throw new ArgumentException("计划用时超过支持的日期范围。");
                    var offset = (int)Math.Floor(minutes / 1440);
                    arrivals[orderedNodeIds[i]] = new(day + 1, TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes % 1440)), offset, elapsedHours);
                }
            }
            days.Add(new(day + 1, startId, orderedNodeIds[end], segments, departure, totalDistance, ascent, descent, totalHours)
            { Notes = plan.DailyNotes.GetValueOrDefault(startId) ?? "" });
        }
        return new(plan.PlanId, days, arrivals, invalid) { Departures = departures };
    }

    private static double? Add(double? a, double? b) => a.HasValue && b.HasValue ? a.Value + b.Value : null;
}
