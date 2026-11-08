using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Elevation;

/// <summary>用户2026-09-30批准的局部坡度里程定义；独立于P8高度累计及图表参数。</summary>
public sealed record LocusTerrainDistanceProfile(double FlatSlopeThresholdPercent = 2.1,
    double SampleSpacingMeters = 5, double SmoothingDistanceMeters = 20,
    double SlopeWindowMeters = 10, double NumericalSlopeTolerancePercent = 1e-9,
    string ProfileId = "UserApprovedLocalSlope", string ProfileVersion = "20260930-v1")
{
    public void Validate()
    {
        if (!double.IsFinite(FlatSlopeThresholdPercent) || FlatSlopeThresholdPercent < 0 || FlatSlopeThresholdPercent > 100 ||
            !double.IsFinite(SampleSpacingMeters) || SampleSpacingMeters <= 0 || SampleSpacingMeters > 1000 ||
            !double.IsFinite(SmoothingDistanceMeters) || SmoothingDistanceMeters < 0 || SmoothingDistanceMeters > 1000 ||
            !double.IsFinite(SlopeWindowMeters) || SlopeWindowMeters <= 0 || SlopeWindowMeters > 1000 ||
            !double.IsFinite(NumericalSlopeTolerancePercent) || NumericalSlopeTolerancePercent < 0 || NumericalSlopeTolerancePercent > 1e-6)
            throw new ArgumentException("里程分类参数无效；正式定义为5米采样、20米平滑、10米坡度窗口，仅平路阈值可调整。");
    }
}
public sealed record TerrainElevationSample(int PartIndex, double DistanceMeters,
    GeoCoordinate Coordinate, double? ElevationMeters);
public sealed record TerrainDistanceInterval(int PartIndex, double StartMeters, double EndMeters, double? SlopePercent);
public sealed record TerrainDistanceClassificationResult(LocusTerrainDistanceProfile Profile,
    string GeometryFingerprint, string SourceFingerprint, IReadOnlyList<TerrainElevationSample> RawSeries,
    double UphillDistanceMeters, double DownhillDistanceMeters, double FlatDistanceMeters,
    double UnclassifiedDistanceMeters, DateTimeOffset CreatedAt, bool IsStale = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<TerrainDistanceInterval> Intervals { get; init; } = [];
    public double TotalGeometryDistanceMeters => UphillDistanceMeters + DownhillDistanceMeters + FlatDistanceMeters + UnclassifiedDistanceMeters;
    public bool IsCurrent(string geometryFingerprint, string sourceFingerprint) => !IsStale &&
        GeometryFingerprint == geometryFingerprint && SourceFingerprint == sourceFingerprint;
}

public static class LocusTerrainDistanceClassifier
{
    public static TerrainDistanceClassificationResult Classify(IReadOnlyList<TerrainElevationSample> raw,
        LocusTerrainDistanceProfile profile, string geometryFingerprint, string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        profile.Validate();
        var up = 0d; var down = 0d; var flat = 0d; var missing = 0d;
        var intervals = new List<TerrainDistanceInterval>();
        foreach (var group in raw.GroupBy(p => p.PartIndex))
        {
            var points = group.ToArray();
            if (points.Length > 0 && (!double.IsFinite(points[0].DistanceMeters) || points[0].DistanceMeters < 0))
                throw new ArgumentException("里程分类高程序列必须包含有效单调里程。");
            for (var i = 1; i < points.Length; i++)
            {
                if (!double.IsFinite(points[i].DistanceMeters) || points[i].DistanceMeters < points[i - 1].DistanceMeters)
                    throw new ArgumentException("里程分类高程序列必须包含有效单调里程。");
                if (!Valid(points[i - 1]) || !Valid(points[i]))
                {
                    missing += points[i].DistanceMeters - points[i - 1].DistanceMeters;
                    intervals.Add(new(group.Key, points[i - 1].DistanceMeters, points[i].DistanceMeters, null));
                }
            }
            for (var start = 0; start < points.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Valid(points[start])) { start++; continue; }
                var end = start + 1;
                while (end < points.Length && Valid(points[end])) end++;
                var prefix = new double[end - start + 1];
                for (var i = start; i < end; i++) prefix[i - start + 1] = prefix[i - start] + points[i].ElevationMeters!.Value;
                var smoothed = new double[end - start];
                var left = start; var right = start;
                for (var i = start; i < end; i++)
                {
                    while (left < i && points[left].DistanceMeters < points[i].DistanceMeters - profile.SmoothingDistanceMeters / 2) left++;
                    while (right + 1 < end && points[right + 1].DistanceMeters <= points[i].DistanceMeters + profile.SmoothingDistanceMeters / 2) right++;
                    smoothed[i - start] = (prefix[right - start + 1] - prefix[left - start]) / (right - left + 1);
                }
                double HeightAt(double distance)
                {
                    var a = start; var b = end - 1;
                    while (a < b) { var mid = (a + b) / 2; if (points[mid].DistanceMeters < distance) a = mid + 1; else b = mid; }
                    if (a == start || points[a].DistanceMeters == distance) return smoothed[a - start];
                    var span = points[a].DistanceMeters - points[a - 1].DistanceMeters;
                    return span <= 0 ? smoothed[a - start] : smoothed[a - 1 - start] + (smoothed[a - start] - smoothed[a - 1 - start]) *
                        (distance - points[a - 1].DistanceMeters) / span;
                }
                for (var i = start + 1; i < end; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = points[i].DistanceMeters - points[i - 1].DistanceMeters;
                    var center = (points[i].DistanceMeters + points[i - 1].DistanceMeters) / 2;
                    var a = Math.Max(points[start].DistanceMeters, center - profile.SlopeWindowMeters / 2);
                    var b = Math.Min(points[end - 1].DistanceMeters, center + profile.SlopeWindowMeters / 2);
                    var slope = b > a ? (HeightAt(b) - HeightAt(a)) / (b - a) * 100 : 0;
                    intervals.Add(new(group.Key, points[i - 1].DistanceMeters, points[i].DistanceMeters, slope));
                    // 仅消除浮点运算噪声，不能把数学边界±T计入上下坡。
                    if (slope > profile.FlatSlopeThresholdPercent + profile.NumericalSlopeTolerancePercent) up += length;
                    else if (slope < -profile.FlatSlopeThresholdPercent - profile.NumericalSlopeTolerancePercent) down += length;
                    else flat += length;
                }
                start = end;
            }
        }
        return new(profile, geometryFingerprint, sourceFingerprint, raw, up, down, flat, missing, DateTimeOffset.UtcNow)
            { Intervals = intervals };
    }

    private static bool Valid(TerrainElevationSample p) => p.ElevationMeters is double value && double.IsFinite(value);
}
