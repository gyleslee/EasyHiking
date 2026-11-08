namespace HikingGuide.Core.Elevation;

public sealed record ElevationChartViewportProfile(
    double VerticalPaddingFraction = 0.05,
    double MinimumVisibleElevationRangeMeters = 20,
    double WheelZoomFactor = 1.25)
{
    public void Validate()
    {
        if (!double.IsFinite(VerticalPaddingFraction) || VerticalPaddingFraction < 0 ||
            VerticalPaddingFraction > 0.5 ||
            !double.IsFinite(MinimumVisibleElevationRangeMeters) ||
            MinimumVisibleElevationRangeMeters <= 0 ||
            !double.IsFinite(WheelZoomFactor) || WheelZoomFactor <= 1)
            throw new ArgumentOutOfRangeException(nameof(ElevationChartViewportProfile));
    }
}

public readonly record struct ElevationChartViewportState(double XMinMeters, double XMaxMeters,
    bool IsFullTrack);

public sealed record SharedElevationViewportState(Guid TargetId, string GeometryFingerprint,
    ElevationChartViewportState Range, double? HoverChainageMeters = null);

public readonly record struct ElevationVisibleY(double MinimumMeters, double MaximumMeters,
    bool HasData);

/// <summary>与 WPF/ScottPlot 无关的视口规则；横轴严格限制在当前分析对象内。</summary>
public static class ElevationChartViewport
{
    public static ElevationChartViewportState Full(double totalMeters) =>
        new(0, Math.Max(0, totalMeters), true);

    public static ElevationChartViewportState Clamp(double requestedMin, double requestedMax,
        double totalMeters)
    {
        var end = Math.Max(0, totalMeters);
        if (!double.IsFinite(requestedMin) || !double.IsFinite(requestedMax) ||
            requestedMax <= requestedMin || end <= 0) return Full(end);
        var width = requestedMax - requestedMin;
        if (width >= end) return Full(end);
        var min = Math.Clamp(requestedMin, 0, end - width);
        return new(min, min + width, false);
    }

    public static ElevationChartViewportState Zoom(ElevationChartViewportState current,
        double centerMeters, int wheelDelta, double totalMeters,
        ElevationChartViewportProfile? profile = null)
    {
        profile ??= new();
        profile.Validate();
        if (wheelDelta == 0) return current;
        var width = current.XMaxMeters - current.XMinMeters;
        if (width <= 0) return Full(totalMeters);
        var factor = Math.Pow(profile.WheelZoomFactor, -wheelDelta / 120d);
        var nextWidth = Math.Clamp(width * factor, Math.Min(1, totalMeters), totalMeters);
        var fraction = Math.Clamp((centerMeters - current.XMinMeters) / width, 0, 1);
        return Clamp(centerMeters - nextWidth * fraction,
            centerMeters + nextWidth * (1 - fraction), totalMeters);
    }

    public static ElevationChartViewportState Pan(ElevationChartViewportState current,
        double deltaMeters, double totalMeters) =>
        Clamp(current.XMinMeters + deltaMeters, current.XMaxMeters + deltaMeters, totalMeters);

    public static int NearestIndex(IReadOnlyList<ElevationSeriesPoint> series, double meters)
    {
        if (series.Count == 0) return -1;
        var right = LowerBound(series, meters);
        if (right == 0) return 0;
        if (right == series.Count) return series.Count - 1;
        return meters - series[right - 1].DistanceMeters <=
            series[right].DistanceMeters - meters ? right - 1 : right;
    }

    public static ElevationVisibleY FitVisibleY(IReadOnlyList<ElevationSeriesPoint> series,
        ElevationChartViewportState viewport, ElevationVisibleY previous,
        ElevationChartViewportProfile? profile = null)
    {
        profile ??= new();
        profile.Validate();
        var first = LowerBound(series, viewport.XMinMeters);
        var last = UpperBound(series, viewport.XMaxMeters);
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (var index = first; index < last; index++) Add(series[index]);
        // 视口边缘可能切在两个样本之间，纳入跨越边缘的有效线段端点。
        if (first > 0 && first < series.Count &&
            series[first].DistanceMeters > viewport.XMinMeters &&
            series[first - 1].PartIndex == series[first].PartIndex &&
            series[first - 1].ProcessedElevationMeters.HasValue &&
            series[first].ProcessedElevationMeters.HasValue)
            AddBoundary(series[first - 1], series[first], viewport.XMinMeters);
        if (last > 0 && last < series.Count &&
            series[last - 1].DistanceMeters < viewport.XMaxMeters &&
            series[last - 1].PartIndex == series[last].PartIndex &&
            series[last - 1].ProcessedElevationMeters.HasValue &&
            series[last].ProcessedElevationMeters.HasValue)
            AddBoundary(series[last - 1], series[last], viewport.XMaxMeters);
        if (!double.IsFinite(min) || !double.IsFinite(max))
            return previous.MaximumMeters > previous.MinimumMeters &&
                double.IsFinite(previous.MaximumMeters) && double.IsFinite(previous.MinimumMeters)
                ? previous with { HasData = false } : new(-10, 10, false);
        var center = (min + max) / 2;
        var range = Math.Max(max - min, profile.MinimumVisibleElevationRangeMeters);
        var half = range * (0.5 + profile.VerticalPaddingFraction);
        return new(center - half, center + half, true);

        void Add(ElevationSeriesPoint point)
        {
            if (point.ProcessedElevationMeters is not double value || !double.IsFinite(value)) return;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        void AddBoundary(ElevationSeriesPoint a, ElevationSeriesPoint b, double distance)
        {
            var span = b.DistanceMeters - a.DistanceMeters;
            if (span <= 0) return;
            var fraction = Math.Clamp((distance - a.DistanceMeters) / span, 0, 1);
            var value = a.ProcessedElevationMeters!.Value +
                (b.ProcessedElevationMeters!.Value - a.ProcessedElevationMeters.Value) * fraction;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
    }

    private static int LowerBound(IReadOnlyList<ElevationSeriesPoint> series, double meters)
    {
        var low = 0;
        var high = series.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (series[mid].DistanceMeters < meters) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private static int UpperBound(IReadOnlyList<ElevationSeriesPoint> series, double meters)
    {
        var low = 0;
        var high = series.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (series[mid].DistanceMeters <= meters) low = mid + 1;
            else high = mid;
        }
        return low;
    }
}
