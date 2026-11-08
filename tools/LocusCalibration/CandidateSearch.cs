using System.Globalization;
using System.Text;
using System.Text.Json;
using HikingGuide.Core.Calibration;
using HikingGuide.Infrastructure.Calibration;

internal sealed record CalibrationSearchProfile(double[] SamplingMeters, double[] SmoothingMeters,
    double[] SlopeWindowsMeters, double[] EventThresholdsMeters, double MinimumSlopePercent,
    double MaximumSlopePercent, double CoarseSlopeStepPercent, double FineSlopeStepPercent,
    double FineSlopeRadiusPercent, double FineEventStepMeters, double FineEventRadiusMeters)
{
    public static CalibrationSearchProfile ExperimentalGrid => new([5, 10, 20, 30], [0, 10, 20, 30, 50, 100],
        [10, 20, 30, 50, 100], [0, 1, 2, 3, 4, 5, 6, 7, 8], 0.5, 5, 0.1, 0.025, 0.2, 0.25, 0.5);
}
internal sealed record AlignmentDataset(string Route, string KmlSha256, string ExcelSha256,
    IReadOnlyList<AlignedReferenceSegment> Segments);
internal sealed record CoveragePair(string Route, Guid Id, string Name, string Fingerprint,
    bool EligibleFullRoutePair, bool CalibrationGpsElevationUsed, string SeriesFile);
internal sealed record DemSeriesInput(string Route, string KmlSha256, Guid Id, string Name,
    string Fingerprint, bool CalibrationGpsElevationUsed, IReadOnlyList<CalibrationDemPoint> Series);
internal sealed record SegmentError(string Route, Guid DemId, string DemName, int Row, string Position,
    double ReferenceDistanceMeters, double ReferenceUpMeters, double ReferenceDownMeters, double ReferenceFlatMeters,
    double PredictedUpMeters, double PredictedDownMeters, double PredictedFlatMeters,
    double UpErrorMeters, double DownErrorMeters, double FlatErrorMeters, double VectorErrorMeters,
    double NormalizedError);
internal sealed record PairScore(string Route, Guid DemId, string DemName, int Segments,
    double MedianNormalizedError, double P90NormalizedError, double UpMaeMeters, double DownMaeMeters,
    double FlatMaeMeters, double UpMedianAbsoluteMeters, double DownMedianAbsoluteMeters,
    double FlatMedianAbsoluteMeters, double UpP90AbsoluteMeters, double DownP90AbsoluteMeters,
    double FlatP90AbsoluteMeters, double UpMaxAbsoluteMeters, double DownMaxAbsoluteMeters, double FlatMaxAbsoluteMeters);
internal sealed record CandidateScore(string Phase, CalibrationCandidateProfile Profile,
    double RobustScore, double P90NormalizedError, double WorstRouteMedianError, IReadOnlyList<PairScore> Pairs);

internal static class CandidateSearch
{
    private sealed record Histogram(double[] Slopes, double[] Prefix, double Total)
    {
        public (double Up, double Down, double Flat) Predict(double threshold)
        {
            var downIndex = Upper(-threshold);
            var upIndex = Lower(threshold);
            var down = Prefix[downIndex];
            var up = Total - Prefix[upIndex];
            return (up, down, Math.Max(0, Total - up - down));
        }
        private int Lower(double value)
        {
            var a = 0; var b = Slopes.Length;
            while (a < b) { var m = (a + b) / 2; if (Slopes[m] < value) a = m + 1; else b = m; }
            return a;
        }
        private int Upper(double value)
        {
            var a = 0; var b = Slopes.Length;
            while (a < b) { var m = (a + b) / 2; if (Slopes[m] <= value) a = m + 1; else b = m; }
            return a;
        }
    }
    private sealed record PairData(CoveragePair Coverage, AlignmentDataset Alignment, DemSeriesInput Input);
    private sealed record PreparedSegment(PairData Pair, AlignedReferenceSegment Segment, Histogram Histogram);

    public static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var alignment = JsonSerializer.Deserialize<AlignmentDataset[]>(await File.ReadAllTextAsync(Path.Combine(root, "alignment-report.json")))!;
        var coverage = JsonSerializer.Deserialize<CoveragePair[]>(await File.ReadAllTextAsync(Path.Combine(root, "coverage-matrix.json")))!;
        var pairs = new List<PairData>();
        foreach (var pair in coverage.Where(p => p.EligibleFullRoutePair))
        {
            var input = JsonSerializer.Deserialize<DemSeriesInput>(await File.ReadAllTextAsync(Path.Combine(root, pair.SeriesFile)))!;
            var aligned = alignment.Single(a => a.Route == pair.Route);
            if (pair.CalibrationGpsElevationUsed || input.CalibrationGpsElevationUsed || input.Id != pair.Id ||
                input.KmlSha256 != aligned.KmlSha256 || input.Fingerprint != pair.Fingerprint || input.Series.Any(s => s.DemElevationMeters is null))
                throw new InvalidDataException("校准输入存在高程来源、原文件哈希、DEM指纹或缺测不一致，禁止拟合。");
            pairs.Add(new(pair, aligned, input));
        }
        if (pairs.Count == 0) throw new InvalidDataException("没有可用的完整DEM校准组合。");
        var grid = CalibrationSearchProfile.ExperimentalGrid;
        await File.WriteAllTextAsync(Path.Combine(root, "search-profile.json"), JsonSerializer.Serialize(grid, options));
        var all = new List<CandidateScore>();
        var structuralCount = 0;
        using (var writer = new StreamWriter(Path.Combine(root, "candidate-results.csv"), false, new UTF8Encoding(true)))
        {
            await writer.WriteLineAsync("Phase,Model,SamplingMeters,SmoothingMeters,WindowMeters,EventMeters,Tail,Direction,ThresholdPercent,RobustMedianNormalizedError,P90NormalizedError,WorstRouteMedianError");
            foreach (var model in Enum.GetValues<CalibrationModel>())
            foreach (var sampling in grid.SamplingMeters)
            foreach (var smoothing in model == CalibrationModel.ElevationEvent ? new[] { 0d } : grid.SmoothingMeters)
            foreach (var window in model == CalibrationModel.LocalSlope ? grid.SlopeWindowsMeters : new[] { 10d })
            foreach (var eventThreshold in model == CalibrationModel.LocalSlope ? new[] { 0d } : grid.EventThresholdsMeters)
            foreach (var tail in model == CalibrationModel.LocalSlope ? new[] { CalibrationTailPolicy.ActualSlope } : Enum.GetValues<CalibrationTailPolicy>())
            foreach (var direction in model == CalibrationModel.LocalSlope ? new[] { CalibrationDirectionPolicy.KeepAnchor } : Enum.GetValues<CalibrationDirectionPolicy>())
            {
                var basis = new CalibrationCandidateProfile(model, sampling, smoothing, window, 0, eventThreshold, tail, direction);
                var prepared = Prepare(pairs, basis);
                for (var step = 0; ; step++)
                {
                    var threshold = Math.Round(grid.MinimumSlopePercent + step * grid.CoarseSlopeStepPercent, 6);
                    if (threshold > grid.MaximumSlopePercent) break;
                    await Add("Coarse", basis with { SlopeThresholdPercent = threshold }, prepared);
                }
                structuralCount++;
                if (structuralCount % 50 == 0) Console.WriteLine($"已搜索结构 {structuralCount}，候选 {all.Count}。");
            }
            var coarseBest = Enum.GetValues<CalibrationModel>().Select(model => Rank(all.Where(c => c.Profile.Model == model)).First()).ToArray();
            foreach (var seed in coarseBest)
            {
                var firstEvent = seed.Profile.Model == CalibrationModel.LocalSlope ? 0 : Math.Max(0, seed.Profile.EventThresholdMeters - grid.FineEventRadiusMeters);
                var lastEvent = seed.Profile.Model == CalibrationModel.LocalSlope ? 0 : Math.Min(grid.EventThresholdsMeters.Max(), seed.Profile.EventThresholdMeters + grid.FineEventRadiusMeters);
                for (var h = firstEvent; h <= lastEvent + 1e-8; h += grid.FineEventStepMeters)
                {
                    var basis = seed.Profile with { EventThresholdMeters = h };
                    var prepared = Prepare(pairs, basis);
                    for (var t = Math.Max(grid.MinimumSlopePercent, seed.Profile.SlopeThresholdPercent - grid.FineSlopeRadiusPercent);
                        t <= Math.Min(grid.MaximumSlopePercent, seed.Profile.SlopeThresholdPercent + grid.FineSlopeRadiusPercent) + 1e-8; t += grid.FineSlopeStepPercent)
                        await Add("Fine", basis with { SlopeThresholdPercent = Math.Round(t, 6) }, prepared);
                }
            }
            async Task Add(string phase, CalibrationCandidateProfile profile, List<PreparedSegment> prepared)
            {
                var errors = Errors(prepared, profile.SlopeThresholdPercent);
                var scores = Scores(errors);
                var candidate = new CandidateScore(phase, profile, Median(scores.Select(p => p.MedianNormalizedError)),
                    Percentile(errors.Select(e => e.NormalizedError), 0.9), scores.GroupBy(p => p.Route).Max(g => Median(g.Select(p => p.MedianNormalizedError))), scores);
                all.Add(candidate);
                await writer.WriteLineAsync(string.Join(',', phase, profile.Model, N(profile.SamplingMeters), N(profile.SmoothingMeters), N(profile.SlopeWindowMeters),
                    N(profile.EventThresholdMeters), profile.Tail, profile.Direction, N(profile.SlopeThresholdPercent), N(candidate.RobustScore), N(candidate.P90NormalizedError), N(candidate.WorstRouteMedianError)));
            }
        }
        var bestModels = Enum.GetValues<CalibrationModel>().Select(model => Rank(all.Where(c => c.Profile.Model == model)).First()).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, "best-models.json"), JsonSerializer.Serialize(bestModels, options));
        var cross = new List<object>();
        foreach (var testRoute in pairs.Select(p => p.Coverage.Route).Distinct())
        foreach (var model in Enum.GetValues<CalibrationModel>())
        {
            // 严格仅在训练路线评分选参数；验证路线完全不参与选优。
            // 全数据细化区域会含验证集信息；留一验证只使用预先固定的粗网格，防止区域选择泄漏。
            var selected = all.Where(c => c.Phase == "Coarse" && c.Profile.Model == model).OrderBy(c => Median(c.Pairs.Where(p => p.Route != testRoute).Select(p => p.MedianNormalizedError)))
                .ThenBy(c => Percentile(c.Pairs.Where(p => p.Route != testRoute).Select(p => p.P90NormalizedError), 0.9)).First();
            cross.Add(new { TestRoute = testRoute, Model = model, SelectionPhase = "FixedCoarseGridOnly_NoFineRegionLeakage", selected.Profile,
                TrainingPairs = selected.Pairs.Where(p => p.Route != testRoute), TestPairs = selected.Pairs.Where(p => p.Route == testRoute) });
        }
        await File.WriteAllTextAsync(Path.Combine(root, "cross-validation.json"), JsonSerializer.Serialize(cross, options));
        var detailed = bestModels.Select(c => new { c.Profile, c.RobustScore,
            Errors = Errors(Prepare(pairs, c.Profile), c.Profile.SlopeThresholdPercent) }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, "segment-errors.json"), JsonSerializer.Serialize(detailed, options));
        await File.WriteAllTextAsync(Path.Combine(root, "worst-10-segments.json"), JsonSerializer.Serialize(detailed.Select(d => new
            { d.Profile, Segments = d.Errors.OrderByDescending(e => e.NormalizedError).Take(10) }), options));
        var sensitivity = bestModels.Select(c =>
        {
            var prepared = Prepare(pairs, c.Profile);
            return new { c.Profile, Values = new[] { 1.5, 1.8, 2, 2.2, 2.5, 3, c.Profile.SlopeThresholdPercent }.Distinct().Order()
                .Select(t => { var errors = Errors(prepared, t); return new { ThresholdPercent = t,
                    MedianNormalizedError = Median(errors.Select(e => e.NormalizedError)), FlatMaeMeters = errors.Average(e => Math.Abs(e.FlatErrorMeters)),
                    FlatDistanceBiasMeters = errors.Average(e => e.FlatErrorMeters) }; }).ToArray() };
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, "sensitivity-flat-threshold.json"), JsonSerializer.Serialize(sensitivity, options));
        await WriteCsv("segment-errors.csv", detailed.SelectMany(d => d.Errors.Select(e => new
            { Model = d.Profile.Model.ToString(), e.Route, e.DemName, e.Row, e.Position, e.ReferenceDistanceMeters,
                e.ReferenceUpMeters, e.ReferenceDownMeters, e.ReferenceFlatMeters, e.PredictedUpMeters, e.PredictedDownMeters,
                e.PredictedFlatMeters, e.UpErrorMeters, e.DownErrorMeters, e.FlatErrorMeters, e.VectorErrorMeters, e.NormalizedError })));
        var byDem = all.SelectMany(c => c.Pairs.Select(p => p.DemId)).Distinct().SelectMany(id => Enum.GetValues<CalibrationModel>().Select(model =>
        {
            var best = all.Where(c => c.Profile.Model == model && c.Pairs.Any(p => p.DemId == id))
                .OrderBy(c => Median(c.Pairs.Where(p => p.DemId == id).Select(p => p.MedianNormalizedError))).First();
            return new { DemId = id, DemName = best.Pairs.First(p => p.DemId == id).DemName, Model = model.ToString(),
                best.Profile.SamplingMeters, best.Profile.SmoothingMeters, best.Profile.SlopeThresholdPercent, best.Profile.EventThresholdMeters,
                Score = Median(best.Pairs.Where(p => p.DemId == id).Select(p => p.MedianNormalizedError)) };
        }));
        await WriteCsv("best-by-dem.csv", byDem);
        await WriteCsv("best-by-route.csv", pairs.Select(p => p.Coverage.Route).Distinct().SelectMany(route => Enum.GetValues<CalibrationModel>().Select(model =>
        {
            var best = all.Where(c => c.Profile.Model == model).OrderBy(c => Median(c.Pairs.Where(p => p.Route == route).Select(p => p.MedianNormalizedError))).First();
            return new { Route = route, Model = model.ToString(), best.Profile.SamplingMeters, best.Profile.SmoothingMeters,
                best.Profile.SlopeThresholdPercent, best.Profile.EventThresholdMeters,
                Score = Median(best.Pairs.Where(p => p.Route == route).Select(p => p.MedianNormalizedError)) };
        })));
        // CSV 交叉验证由与 JSON 同一记录生成，参数选择仅用训练路线。
        var crossRows = new List<object>();
        foreach (var route in pairs.Select(p => p.Coverage.Route).Distinct())
        foreach (var model in Enum.GetValues<CalibrationModel>())
        {
            var selected = all.Where(c => c.Phase == "Coarse" && c.Profile.Model == model)
                .OrderBy(c => Median(c.Pairs.Where(p => p.Route != route).Select(p => p.MedianNormalizedError)))
                .ThenBy(c => Percentile(c.Pairs.Where(p => p.Route != route).Select(p => p.P90NormalizedError), 0.9)).First();
            crossRows.Add(new { TestRoute = route, Model = model.ToString(), selected.Profile.SamplingMeters,
                selected.Profile.SmoothingMeters, selected.Profile.SlopeThresholdPercent, selected.Profile.EventThresholdMeters,
                TrainScore = Median(selected.Pairs.Where(p => p.Route != route).Select(p => p.MedianNormalizedError)),
                TestScore = Median(selected.Pairs.Where(p => p.Route == route).Select(p => p.MedianNormalizedError)),
                SelectionPhase = "FixedCoarseGridOnly_NoFineRegionLeakage" });
        }
        await WriteCsv("cross-validation.csv", crossRows);
        await WriteCsv("sensitivity-flat-threshold.csv", sensitivity.SelectMany(s => s.Values.Select(v => new
            { Model = s.Profile.Model.ToString(), v.ThresholdPercent, v.MedianNormalizedError, v.FlatMaeMeters, v.FlatDistanceBiasMeters })));
        var smoothedHybrid = Rank(all.Where(c => c.Profile.Model == CalibrationModel.Hybrid && c.Profile.SmoothingMeters > 0)).First();
        await File.WriteAllTextAsync(Path.Combine(root, "best-nonzero-smoothed-hybrid.json"), JsonSerializer.Serialize(smoothedHybrid, options));
        var text = new StringBuilder("# P9-A候选算法首轮验证（不是阶段完成报告）\n\n");
        text.AppendLine($"粗网格结构 {structuralCount}，总候选 {all.Count}；可用路线×DEM组合 {pairs.Count}，逐组合分段评分样本 {detailed[0].Errors.Count}。原始海拔未使用，正式统计算法未变更。\n");
        text.AppendLine("评分：每段 (|上坡误差|+|下坡误差|+|平路误差|)/参考长度；每组合取段中位数，再对组合取中位数。不是全程总距离偏差，也不是单个字段的百分比。P90为全部逐段样本的归一化误差第90百分位。\n");
        text.AppendLine("|模型|采样m|平滑m|窗口m|坡度阈值%|事件阈值m|稳健评分|P90|\n|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var best in bestModels)
        {
            var p = best.Profile;
            var label = p.Model == CalibrationModel.LocalSlope ? "A 局部坡度" : p.Model == CalibrationModel.ElevationEvent ? "B 高程事件" : "C 混合模型";
            string Display(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
            text.AppendLine($"|{label}|{Display(p.SamplingMeters)}|{Display(p.SmoothingMeters)}|{Display(p.SlopeWindowMeters)}|{Display(p.SlopeThresholdPercent)}|{Display(p.EventThresholdMeters)}|{best.RobustScore:P2}|{best.P90NormalizedError:P2}|");
        }
        text.AppendLine("\n模型 A：均匀采样，按距离居中窗口作算术均值平滑，用每个相邻采样区间中点的固定长度窗口计算平均坡度，分类整个采样区间。窗口在连续有效区间端点截短。\n");
        text.AppendLine("模型 B：不平滑，累计锚点高程变化达到 H 后形成事件，按事件平均坡度分类。方向策略已比较保持锚点、方向变化即时重置、等待从极值反向达到H。尾段已比较平路、实际平均坡度、沿用上一事件坡度。\n");
        text.AppendLine("模型 C：在事件模型前增加距离平滑。首轮最佳平滑为0，退化成模型B，不能据此声称复杂模型有优势；另存非零平滑最佳结果。\n");
        text.AppendLine("边界：坡度 >= +T 为上坡，<= -T 为下坡，其余为平路；零坡度为平路。事件阈值为 >=H；所有已纳入参考区间满足分类里程守恒。模型B/C最佳尾段使用实际坡度，方向策略为等待高程滞回。\n");
        text.AppendLine("本轮先按完整路线连续分类，再裁取参考区间，不在每个参考节点重置事件；独立分段重置另由boundary-validation命令检验。当前仅对每模型全数据粗网格最优区域细化；留一验证为预先固定粗网格，仅训练路线选参数，验证路线不参与细化区域选择。不能把全数据细化结果当作无泄漏的留一结论。\n");
        text.AppendLine("尚待复核：组合段真实范围、异常误差原因、区间边界策略、细化区域稳定性和更严格泛化判断。收藏点系统尚未完成，因此这不是P9-A完成报告，不要求用户现在采纳候选。\n");
        text.AppendLine("以上仅为反推候选标准，尚未写入软件正式Locus距离分类算法，等待用户确认或修改。");
        await File.WriteAllTextAsync(Path.Combine(root, "calibration-summary.md"), text.ToString());
        Console.WriteLine($"搜索完成：结构 {structuralCount}，候选 {all.Count}。仅供反推实验，未采纳正式默认算法。");
        foreach (var best in bestModels) Console.WriteLine($"{best.Profile.Model}：阈值 {best.Profile.SlopeThresholdPercent}%，H={best.Profile.EventThresholdMeters}m，稳健评分 {best.RobustScore:P2}，P90 {best.P90NormalizedError:P2}。");

        async Task WriteCsv<T>(string file, IEnumerable<T> rows)
        {
            var lines = new List<string>();
            foreach (var row in rows)
            {
                var element = JsonSerializer.SerializeToElement(row);
                if (lines.Count == 0) lines.Add(string.Join(',', element.EnumerateObject().Select(p => Quote(p.Name))));
                lines.Add(string.Join(',', element.EnumerateObject().Select(p => Quote(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ToString()))));
            }
            await File.WriteAllLinesAsync(Path.Combine(root, file), lines, new UTF8Encoding(true));
        }
    }

    private static List<PreparedSegment> Prepare(List<PairData> pairs, CalibrationCandidateProfile basis)
    {
        var result = new List<PreparedSegment>();
        foreach (var pair in pairs)
        {
            var intervals = CalibrationCandidateClassifier.Classify(pair.Input.Series, basis);
            foreach (var segment in pair.Alignment.Segments.Where(s => s.EligibleForFit))
            {
                var start = segment.StartDistanceMeters!.Value;
                var end = segment.EndDistanceMeters!.Value;
                var a = 0; var b = intervals.Count;
                while (a < b) { var m = (a + b) / 2; if (intervals[m].EndMeters <= start) a = m + 1; else b = m; }
                var histogram = new List<(double Slope, double Distance)>();
                for (var i = a; i < intervals.Count && intervals[i].StartMeters < end; i++)
                {
                    var interval = intervals[i];
                    if (interval.Terrain == CalibrationTerrain.Unclassified) throw new InvalidDataException("待评分分段包含NoData，不能作为全程拟合目标。");
                    var distance = Math.Max(0, Math.Min(end, interval.EndMeters) - Math.Max(start, interval.StartMeters));
                    if (distance > 0) histogram.Add((interval.EffectiveSlopePercent ?? 0, distance));
                }
                histogram.Sort((x, y) => x.Slope.CompareTo(y.Slope));
                var prefix = new double[histogram.Count + 1];
                for (var i = 0; i < histogram.Count; i++) prefix[i + 1] = prefix[i] + histogram[i].Distance;
                if (Math.Abs(prefix[^1] - (end - start)) > 1e-5) throw new InvalidDataException("分类里程不守恒。");
                result.Add(new(pair, segment, new(histogram.Select(h => h.Slope).ToArray(), prefix, prefix[^1])));
            }
        }
        return result;
    }

    private static List<SegmentError> Errors(List<PreparedSegment> prepared, double threshold)
    {
        return prepared.Select(p =>
        {
            var reference = p.Segment.Reference;
            double Value(string field) => reference.Fields[field].ReferenceValue!.Value * 1000;
            var predicted = p.Histogram.Predict(threshold);
            var up = predicted.Up - Value("分段爬升距离KM");
            var down = predicted.Down - Value("分段下降距离KM");
            var flat = predicted.Flat - Value("平路距离KM");
            var vector = Math.Abs(up) + Math.Abs(down) + Math.Abs(flat);
            return new SegmentError(p.Pair.Coverage.Route, p.Pair.Coverage.Id, p.Pair.Coverage.Name,
                reference.RowNumber, reference.Position, Value("分段距离KM"), Value("分段爬升距离KM"), Value("分段下降距离KM"), Value("平路距离KM"),
                predicted.Up, predicted.Down, predicted.Flat, up, down, flat, vector, vector / Value("分段距离KM"));
        }).ToList();
    }

    private static IReadOnlyList<PairScore> Scores(List<SegmentError> errors) => errors.GroupBy(e => (e.Route, e.DemId, e.DemName))
        .Select(g => new PairScore(g.Key.Route, g.Key.DemId, g.Key.DemName, g.Count(), Median(g.Select(e => e.NormalizedError)),
            Percentile(g.Select(e => e.NormalizedError), 0.9), g.Average(e => Math.Abs(e.UpErrorMeters)), g.Average(e => Math.Abs(e.DownErrorMeters)),
            g.Average(e => Math.Abs(e.FlatErrorMeters)), Median(g.Select(e => Math.Abs(e.UpErrorMeters))), Median(g.Select(e => Math.Abs(e.DownErrorMeters))),
            Median(g.Select(e => Math.Abs(e.FlatErrorMeters))), Percentile(g.Select(e => Math.Abs(e.UpErrorMeters)), 0.9),
            Percentile(g.Select(e => Math.Abs(e.DownErrorMeters)), 0.9), Percentile(g.Select(e => Math.Abs(e.FlatErrorMeters)), 0.9),
            g.Max(e => Math.Abs(e.UpErrorMeters)), g.Max(e => Math.Abs(e.DownErrorMeters)), g.Max(e => Math.Abs(e.FlatErrorMeters)))).ToArray();
    private static IOrderedEnumerable<CandidateScore> Rank(IEnumerable<CandidateScore> scores) => scores.OrderBy(c => c.RobustScore)
        .ThenBy(c => c.P90NormalizedError).ThenBy(c => c.WorstRouteMedianError);
    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);
    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new InvalidDataException("评分集合为空。");
        var position = (sorted.Length - 1) * percentile;
        var a = (int)position; var b = Math.Min(a + 1, sorted.Length - 1);
        return sorted[a] + (sorted[b] - sorted[a]) * (position - a);
    }
    private static string N(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
