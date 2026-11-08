namespace HikingGuide.Core.Elevation;

/// <summary>任何物理 GeometryPart 或高程缺失都切断图线与填充。</summary>
public static class ElevationChartRuns
{
    public static IReadOnlyList<IReadOnlyList<ElevationSeriesPoint>> Build(
        IReadOnlyList<ElevationSeriesPoint> series, int start = 0, int? end = null)
    {
        var runs = new List<IReadOnlyList<ElevationSeriesPoint>>();
        var current = new List<ElevationSeriesPoint>();
        var lastPart = -1;
        void EndRun()
        {
            if (current.Count > 0) runs.Add(current.ToArray());
            current.Clear();
        }
        for (var i = Math.Max(0, start); i < Math.Min(series.Count, end ?? series.Count); i++)
        {
            var point = series[i];
            if (point.PartIndex != lastPart || point.ProcessedElevationMeters is not double value ||
                !double.IsFinite(value)) EndRun();
            if (point.ProcessedElevationMeters is double valid && double.IsFinite(valid))
                current.Add(point);
            lastPart = point.PartIndex;
        }
        EndRun();
        return runs;
    }
}
