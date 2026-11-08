namespace HikingGuide.Core.Calibration;

public enum CalibrationModel { LocalSlope, ElevationEvent, Hybrid }
public enum CalibrationTailPolicy { Flat, ActualSlope, MergePrevious }
public enum CalibrationDirectionPolicy { KeepAnchor, ResetOnDirectionChange, WaitHysteresis }
public enum CalibrationTerrain { Uphill, Downhill, Flat, Unclassified }
public sealed record CalibrationCandidateProfile(CalibrationModel Model, double SamplingMeters,
    double SmoothingMeters, double SlopeWindowMeters, double SlopeThresholdPercent,
    double EventThresholdMeters, CalibrationTailPolicy Tail, CalibrationDirectionPolicy Direction)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Model) || !Enum.IsDefined(Tail) || !Enum.IsDefined(Direction) ||
            new[] { SamplingMeters, SmoothingMeters, SlopeWindowMeters, SlopeThresholdPercent, EventThresholdMeters }
                .Any(v => !double.IsFinite(v) || v < 0) || SamplingMeters == 0 || SlopeWindowMeters == 0)
            throw new ArgumentException("候选分类参数无效。");
    }
}
public sealed record CalibrationTerrainInterval(int PartIndex, double StartMeters,
    double EndMeters, CalibrationTerrain Terrain, double? EffectiveSlopePercent = null);
public sealed record CalibrationTerrainDistances(double UphillMeters, double DownhillMeters,
    double FlatMeters, double UnclassifiedMeters)
{
    public double TotalMeters => UphillMeters + DownhillMeters + FlatMeters + UnclassifiedMeters;
}

/// <summary>仅供 P9-A 反推实验，不属于软件正式 Locus 默认算法。</summary>
public static class CalibrationCandidateClassifier
{
    public static IReadOnlyList<CalibrationTerrainInterval> Classify(IReadOnlyList<CalibrationDemPoint> input,
        CalibrationCandidateProfile profile)
    {
        profile.Validate();
        var output = new List<CalibrationTerrainInterval>();
        foreach (var part in input.GroupBy(p => p.PartIndex))
        {
            var original = part.ToArray();
            if (original.Any(p => !double.IsFinite(p.DistanceMeters) || p.DistanceMeters < 0))
                throw new ArgumentException("高程序列包含无效里程。");
            for (var i = 1; i < original.Length; i++)
                if (original[i].DistanceMeters < original[i - 1].DistanceMeters)
                    throw new ArgumentException("高程序列里程必须单调递增。");
            // 保留 NoData 及其相邻边界，降低采样密度不能掩盖缺测。
            var points = new List<CalibrationDemPoint>();
            if (original.Length == 0) continue;
            var locations = new SortedSet<double>();
            for (var distance = original[0].DistanceMeters; distance < original[^1].DistanceMeters; distance += profile.SamplingMeters)
            {
                locations.Add(distance);
                if (distance + profile.SamplingMeters <= distance) throw new ArgumentException("采样间距过小，不能可靠表示。");
            }
            locations.Add(original[^1].DistanceMeters);
            for (var i = 0; i < original.Length; i++)
            {
                if (!Valid(original[i]))
                {
                    locations.Add(original[i].DistanceMeters);
                    if (i > 0) locations.Add(original[i - 1].DistanceMeters);
                    if (i + 1 < original.Length) locations.Add(original[i + 1].DistanceMeters);
                }
            }
            var cursor = 0;
            foreach (var distance in locations)
            {
                while (cursor + 1 < original.Length && original[cursor + 1].DistanceMeters < distance) cursor++;
                var a = original[cursor];
                if (a.DistanceMeters == distance || cursor + 1 == original.Length) { points.Add(a); continue; }
                var b = original[cursor + 1];
                if (b.DistanceMeters == distance) { points.Add(b); continue; }
                var fraction = (distance - a.DistanceMeters) / (b.DistanceMeters - a.DistanceMeters);
                points.Add(new(a.PartIndex, distance, new(a.Coordinate.Latitude + (b.Coordinate.Latitude - a.Coordinate.Latitude) * fraction,
                    a.Coordinate.Longitude + (b.Coordinate.Longitude - a.Coordinate.Longitude) * fraction),
                    Valid(a) && Valid(b) ? a.DemElevationMeters!.Value + (b.DemElevationMeters!.Value - a.DemElevationMeters!.Value) * fraction : null));
            }
            var heights = points.Select(p => p.DemElevationMeters is double h && double.IsFinite(h) ? (double?)h : null).ToArray();
            var start = 0;
            while (start < points.Count)
            {
                if (heights[start] is null) { start++; continue; }
                var end = start + 1;
                while (end < points.Count && heights[end] is not null) end++;
                if (end - start >= 2) ProcessRun(start, end);
                start = end;
            }
            for (var i = 1; i < points.Count; i++)
                if (heights[i - 1] is null || heights[i] is null)
                    Add(i - 1, i, CalibrationTerrain.Unclassified);

            void Add(int left, int right, CalibrationTerrain terrain, double? slope = null)
            {
                if (points[right].DistanceMeters > points[left].DistanceMeters)
                    output.Add(new(part.Key, points[left].DistanceMeters, points[right].DistanceMeters, terrain, slope));
            }
            void ProcessRun(int from, int to)
            {
                var smooth = profile.Model == CalibrationModel.ElevationEvent ? 0 : profile.SmoothingMeters;
                var values = new double[to - from];
                var prefix = new double[values.Length + 1];
                for (var i = from; i < to; i++) prefix[i - from + 1] = prefix[i - from] + heights[i]!.Value;
                var left = from;
                var right = from;
                for (var i = from; i < to; i++)
                {
                    while (left < i && points[left].DistanceMeters < points[i].DistanceMeters - smooth / 2) left++;
                    while (right + 1 < to && points[right + 1].DistanceMeters <= points[i].DistanceMeters + smooth / 2) right++;
                    values[i - from] = smooth == 0 ? heights[i]!.Value : (prefix[right - from + 1] - prefix[left - from]) / (right - left + 1);
                }
                double HeightAt(double distance)
                {
                    var low = from;
                    var high = to - 1;
                    while (low < high) { var mid = (low + high) / 2; if (points[mid].DistanceMeters < distance) low = mid + 1; else high = mid; }
                    if (low == from || points[low].DistanceMeters == distance) return values[low - from];
                    var span = points[low].DistanceMeters - points[low - 1].DistanceMeters;
                    return span == 0 ? values[low - from] : values[low - 1 - from] +
                        (values[low - from] - values[low - 1 - from]) * (distance - points[low - 1].DistanceMeters) / span;
                }
                if (profile.Model == CalibrationModel.LocalSlope)
                {
                    for (var i = from + 1; i < to; i++)
                    {
                        var midpoint = (points[i].DistanceMeters + points[i - 1].DistanceMeters) / 2;
                        var a = Math.Max(points[from].DistanceMeters, midpoint - profile.SlopeWindowMeters / 2);
                        var b = Math.Min(points[to - 1].DistanceMeters, midpoint + profile.SlopeWindowMeters / 2);
                        var delta = HeightAt(b) - HeightAt(a);
                        Add(i - 1, i, Terrain(delta, b - a), b > a ? delta / (b - a) * 100 : 0);
                    }
                    return;
                }
                var anchor = from;
                var previousDirection = 0;
                var peak = from;
                double? previousSlope = null;
                void Emit(int until, bool tail)
                {
                    if (until <= anchor) return;
                    var span = points[until].DistanceMeters - points[anchor].DistanceMeters;
                    var slope = span > 0 ? (values[until - from] - values[anchor - from]) / span * 100 : 0;
                    if (tail) slope = profile.Tail == CalibrationTailPolicy.Flat ? 0 :
                        profile.Tail == CalibrationTailPolicy.MergePrevious ? previousSlope ?? slope : slope;
                    Add(anchor, until, Terrain(slope, 100), slope);
                    previousSlope = slope;
                    anchor = until;
                }
                for (var i = from + 1; i < to; i++)
                {
                    var stepDirection = Math.Sign(values[i - from] - values[i - 1 - from]);
                    if (profile.Direction == CalibrationDirectionPolicy.ResetOnDirectionChange && stepDirection != 0 && previousDirection != 0 && stepDirection != previousDirection)
                        Emit(i - 1, true);
                    if (profile.Direction == CalibrationDirectionPolicy.WaitHysteresis)
                    {
                        if (previousDirection == 0) previousDirection = stepDirection;
                        if (previousDirection * (values[i - from] - values[peak - from]) >= 0) peak = i;
                        else if (Math.Abs(values[i - from] - values[peak - from]) >= profile.EventThresholdMeters)
                        { Emit(peak, true); previousDirection = stepDirection; peak = i; }
                    }
                    else if (stepDirection != 0) previousDirection = stepDirection;
                    if (Math.Abs(values[i - from] - values[anchor - from]) >= profile.EventThresholdMeters)
                    { Emit(i, false); peak = i; }
                }
                Emit(to - 1, true);
            }
        }
        return output.OrderBy(p => p.StartMeters).ThenBy(p => p.PartIndex).ToArray();

        CalibrationTerrain Terrain(double delta, double distance)
        {
            var slope = distance > 0 ? delta / distance * 100 : 0;
            if (slope == 0) return CalibrationTerrain.Flat;
            return slope >= profile.SlopeThresholdPercent ? CalibrationTerrain.Uphill :
                slope <= -profile.SlopeThresholdPercent ? CalibrationTerrain.Downhill : CalibrationTerrain.Flat;
        }
    }

    private static bool Valid(CalibrationDemPoint point) => point.DemElevationMeters is double value && double.IsFinite(value);

    public static CalibrationTerrainDistances Measure(IReadOnlyList<CalibrationTerrainInterval> intervals,
        double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end < start) throw new ArgumentException("分类区间无效。");
        var lengths = new double[4];
        foreach (var interval in intervals)
            lengths[(int)interval.Terrain] += Math.Max(0, Math.Min(end, interval.EndMeters) - Math.Max(start, interval.StartMeters));
        return new(lengths[0], lengths[1], lengths[2], lengths[3]);
    }
}
