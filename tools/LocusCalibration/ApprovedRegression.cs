using System.Text.Json;
using HikingGuide.Core.Elevation;

internal static class ApprovedRegression
{
    public static async Task RunAsync(string root)
    {
        var profile = new LocusTerrainDistanceProfile();
        var alignments = JsonSerializer.Deserialize<AlignmentDataset[]>(await File.ReadAllTextAsync(Path.Combine(root, "alignment-report.json")))!;
        var coverage = JsonSerializer.Deserialize<CoveragePair[]>(await File.ReadAllTextAsync(Path.Combine(root, "coverage-matrix.json")))!;
        var totals = new List<object>();
        var errors = new List<double>();
        var rows = new List<object>();
        foreach (var pair in coverage.Where(p => p.EligibleFullRoutePair))
        {
            var input = JsonSerializer.Deserialize<DemSeriesInput>(await File.ReadAllTextAsync(Path.Combine(root, pair.SeriesFile)))!;
            var alignment = alignments.Single(a => a.Route == pair.Route);
            if (input.CalibrationGpsElevationUsed || input.KmlSha256 != alignment.KmlSha256 || input.Fingerprint != pair.Fingerprint)
                throw new InvalidDataException("正式回归验证的高程来源或指纹不一致。");
            var raw = input.Series.Select(p => new TerrainElevationSample(p.PartIndex, p.DistanceMeters, p.Coordinate, p.DemElevationMeters)).ToArray();
            var result = LocusTerrainDistanceClassifier.Classify(raw, profile, input.KmlSha256, input.Fingerprint);
            totals.Add(new { pair.Route, pair.Name, result.UphillDistanceMeters, result.DownhillDistanceMeters,
                result.FlatDistanceMeters, result.UnclassifiedDistanceMeters, result.TotalGeometryDistanceMeters });
            foreach (var segment in alignment.Segments.Where(s => s.EligibleForFit))
            {
                var up = 0d; var down = 0d; var flat = 0d; var missing = 0d;
                foreach (var interval in result.Intervals)
                {
                    var length = Math.Max(0, Math.Min(interval.EndMeters, segment.EndDistanceMeters!.Value) -
                        Math.Max(interval.StartMeters, segment.StartDistanceMeters!.Value));
                    if (interval.SlopePercent is not double slope) missing += length;
                    else if (slope > profile.FlatSlopeThresholdPercent + profile.NumericalSlopeTolerancePercent) up += length;
                    else if (slope < -profile.FlatSlopeThresholdPercent - profile.NumericalSlopeTolerancePercent) down += length;
                    else flat += length;
                }
                double Reference(string key) => segment.Reference.Fields[key].ReferenceValue!.Value * 1000;
                var vectorError = Math.Abs(up - Reference("分段爬升距离KM")) + Math.Abs(down - Reference("分段下降距离KM")) + Math.Abs(flat - Reference("平路距离KM"));
                var normalized = vectorError / Reference("分段距离KM");
                errors.Add(normalized);
                rows.Add(new { pair.Route, pair.Name, segment.Reference.RowNumber, segment.Reference.Position,
                    UphillMeters = up, DownhillMeters = down, FlatMeters = flat, UnclassifiedMeters = missing,
                    ReferenceUphillMeters = Reference("分段爬升距离KM"), ReferenceDownhillMeters = Reference("分段下降距离KM"),
                    ReferenceFlatMeters = Reference("平路距离KM"), VectorErrorMeters = vectorError, NormalizedError = normalized });
            }
            Console.WriteLine($"{pair.Route} × {pair.Name}：上坡{result.UphillDistanceMeters / 1000:F2}、下坡{result.DownhillDistanceMeters / 1000:F2}、平路{result.FlatDistanceMeters / 1000:F2}公里");
        }
        errors.Sort();
        var median = errors.Count % 2 == 0 ? (errors[errors.Count / 2 - 1] + errors[errors.Count / 2]) / 2 : errors[errors.Count / 2];
        var report = new { Profile = profile, Strategy = "全轨迹连续分类后按精确参考端点积分；边界±阈值归平路",
            DemOnly = true, FullCoveragePairs = totals.Count, ValidReferenceComparisons = rows.Count,
            MedianNormalizedVectorError = median, P90NormalizedVectorError = errors[(int)Math.Ceiling(errors.Count * 0.9) - 1],
            Totals = totals, Segments = rows };
        await File.WriteAllTextAsync(Path.Combine(root, "approved-local-slope-regression.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"正式参数回归：{rows.Count}个有效参考区间×DEM，区间归一化分类向量误差中位数{median:P2}。");
    }
}
