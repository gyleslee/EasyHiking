using System.Text.Json;
using HikingGuide.Core.Calibration;

internal static class BoundaryValidation
{
    public static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        var best = JsonSerializer.Deserialize<CandidateScore[]>(await File.ReadAllTextAsync(Path.Combine(root, "best-models.json")))!;
        var alignment = JsonSerializer.Deserialize<AlignmentDataset[]>(await File.ReadAllTextAsync(Path.Combine(root, "alignment-report.json")))!;
        var coverage = JsonSerializer.Deserialize<CoveragePair[]>(await File.ReadAllTextAsync(Path.Combine(root, "coverage-matrix.json")))!;
        var results = new List<object>();
        foreach (var pair in coverage.Where(p => p.EligibleFullRoutePair))
        {
            var series = JsonSerializer.Deserialize<DemSeriesInput>(await File.ReadAllTextAsync(Path.Combine(root, pair.SeriesFile)))!;
            var aligned = alignment.Single(r => r.Route == pair.Route);
            if (series.CalibrationGpsElevationUsed || series.KmlSha256 != aligned.KmlSha256 || series.Fingerprint != pair.Fingerprint)
                throw new InvalidDataException("区间验证输入来源或指纹不一致。");
            foreach (var candidate in best)
            {
                var continuous = CalibrationCandidateClassifier.Classify(series.Series, candidate.Profile);
                foreach (var segment in aligned.Segments.Where(s => s.EligibleForFit))
                {
                    var start = segment.StartDistanceMeters!.Value;
                    var end = segment.EndDistanceMeters!.Value;
                    var clipped = Clip(series.Series, start, end);
                    var resetIntervals = CalibrationCandidateClassifier.Classify(clipped, candidate.Profile);
                    var a = CalibrationCandidateClassifier.Measure(continuous, start, end);
                    var b = CalibrationCandidateClassifier.Measure(resetIntervals, start, end);
                    if (Math.Abs(a.TotalMeters - b.TotalMeters) > 1e-5) throw new InvalidDataException("区间边界对照不守恒。");
                    double Reference(string field) => segment.Reference.Fields[field].ReferenceValue!.Value * 1000;
                    double Error(CalibrationTerrainDistances d) => Math.Abs(d.UphillMeters - Reference("分段爬升距离KM")) +
                        Math.Abs(d.DownhillMeters - Reference("分段下降距离KM")) + Math.Abs(d.FlatMeters - Reference("平路距离KM"));
                    results.Add(new { pair.Route, pair.Id, pair.Name, Model = candidate.Profile.Model.ToString(), segment.Reference.RowNumber,
                        segment.Reference.Position, Continuous = a, ResetAtReferenceBoundary = b,
                        ContinuousVectorErrorMeters = Error(a), ResetVectorErrorMeters = Error(b),
                        ResetMinusContinuousErrorMeters = Error(b) - Error(a),
                        ReferenceDistanceMeters = Reference("分段距离KM"), CalibrationGpsElevationUsed = false });
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(root, "boundary-validation.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"边界策略对照已完成：{results.Count}条模型×区间×DEM记录；仅使用已导出的DEM缓存序列。");
    }

    private static IReadOnlyList<CalibrationDemPoint> Clip(IReadOnlyList<CalibrationDemPoint> series, double start, double end)
    {
        var output = new List<CalibrationDemPoint>();
        foreach (var group in series.GroupBy(s => s.PartIndex))
        {
            var part = group.ToArray();
            var a = Math.Max(start, part[0].DistanceMeters);
            var b = Math.Min(end, part[^1].DistanceMeters);
            if (b <= a) continue;
            output.Add(At(a));
            output.AddRange(part.Where(p => p.DistanceMeters > a && p.DistanceMeters < b));
            output.Add(At(b));
            CalibrationDemPoint At(double distance)
            {
                var low = 0; var high = part.Length - 1;
                while (low < high) { var m = (low + high) / 2; if (part[m].DistanceMeters < distance) low = m + 1; else high = m; }
                if (part[low].DistanceMeters == distance) return part[low];
                if (low == 0) throw new InvalidDataException("参考边界超出缓存高程序列。");
                var p = part[low - 1]; var q = part[low];
                var fraction = (distance - p.DistanceMeters) / (q.DistanceMeters - p.DistanceMeters);
                return new(group.Key, distance, new(p.Coordinate.Latitude + (q.Coordinate.Latitude - p.Coordinate.Latitude) * fraction,
                    p.Coordinate.Longitude + (q.Coordinate.Longitude - p.Coordinate.Longitude) * fraction),
                    p.DemElevationMeters is double left && q.DemElevationMeters is double right ? left + (right - left) * fraction : null);
            }
        }
        return output;
    }
}
