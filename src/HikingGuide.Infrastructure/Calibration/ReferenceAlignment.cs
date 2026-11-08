using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Infrastructure.Calibration;

public sealed record ReferenceAlignmentProfile(double MaximumProjectionDistanceMeters = 100,
    double NearestExtraToleranceMeters = 15, double AbsoluteDistanceAuditMeters = 50,
    double RelativeDistanceAuditRatio = 0.01, double AmbiguityScoreMargin = 0.1,
    double DistinctPassageMeters = 50)
{
    public void Validate()
    {
        var values = new[] { MaximumProjectionDistanceMeters, NearestExtraToleranceMeters,
            AbsoluteDistanceAuditMeters, RelativeDistanceAuditRatio, AmbiguityScoreMargin, DistinctPassageMeters };
        if (values.Any(v => !double.IsFinite(v) || v < 0) || AbsoluteDistanceAuditMeters == 0 || MaximumProjectionDistanceMeters == 0)
            throw new ArgumentException("参考区间对齐参数无效。");
    }
}
public sealed record AlignedReferenceSegment(LocusReferenceSegment Reference,
    IReadOnlyList<TrackPosition> Candidates, int RawProjectionCount, TrackPosition? Selected,
    double? StartDistanceMeters, double? EndDistanceMeters, double? GeometryDistanceMeters,
    double? DistanceErrorMeters, string Status, bool EligibleForFit, string Reason);

/// <summary>全序列最小代价对齐。名称只查找标注，原始几何投影和参考长度决定顺序。</summary>
public static class ReferenceAlignment
{
    private sealed record PathState(double Score, int[] Choices);

    public static IReadOnlyList<AlignedReferenceSegment> Align(Track track,
        IReadOnlyList<Waypoint> markers, IReadOnlyList<LocusReferenceSegment> references,
        ReferenceAlignmentProfile profile)
    {
        profile.Validate();
        var snap = new TrackSnapService();
        var projections = references.Select(r => markers.Where(m => NormalizeName(m.Name) == NormalizeName(r.Position))
            .SelectMany(m => snap.SnapAllWithinTolerance(track, m.Coordinate,
                profile.MaximumProjectionDistanceMeters, profile.NearestExtraToleranceMeters)).ToArray()).ToArray();
        var candidates = projections.Select(p => SeparatePassages(p, profile.DistinctPassageMeters)).ToArray();
        var result = new AlignedReferenceSegment[references.Count];
        var block = 0;
        while (block < references.Count)
        {
            if (candidates[block].Count == 0)
            {
                result[block] = Build(block, null, null, "Unmatched", "没有名称匹配且在投影容差内的标注。", false);
                block++;
                continue;
            }
            var end = block + 1;
            while (end < references.Count && candidates[end].Count > 0 && references[end].Sheet == references[block].Sheet) end++;
            var states = candidates[block].Select((p, index) => new List<PathState>
                { new(p.DistanceToTrack / profile.MaximumProjectionDistanceMeters, [index]) }).ToArray();
            for (var row = block + 1; row < end; row++)
            {
                var nextStates = candidates[row].Select(_ => new List<PathState>()).ToArray();
                var expected = references[row].Fields["分段距离KM"].ReferenceValue!.Value * 1000;
                var scale = Math.Max(profile.AbsoluteDistanceAuditMeters, expected * profile.RelativeDistanceAuditRatio);
                for (var current = 0; current < candidates[row].Count; current++)
                {
                    var p = candidates[row][current];
                    for (var previous = 0; previous < states.Length; previous++)
                    {
                        var a = candidates[row - 1][previous];
                        var distance = p.DistanceFromTrackStart - a.DistanceFromTrackStart;
                        if (distance < 0) continue; // 不倒置原始轨迹方向，不制造区间。
                        var cost = references[row].IsCompositeSuspect ? 0 : Math.Abs(distance - expected) / scale;
                        foreach (var state in states[previous])
                            nextStates[current].Add(new(state.Score + cost + p.DistanceToTrack / profile.MaximumProjectionDistanceMeters,
                                [.. state.Choices, current]));
                    }
                    // 每个状态保留两条完整历史，不能仅按局部最近点决定。
                    nextStates[current] = nextStates[current].OrderBy(s => s.Score).Take(2).ToList();
                }
                states = nextStates;
            }
            var finalists = states.SelectMany(s => s).OrderBy(s => s.Score).ToArray();
            if (finalists.Length == 0)
            {
                for (var i = block; i < end; i++) result[i] = Build(i, null, null, "Ambiguous", "无法构成单调完整序列，必须人工审计。", false);
                block = end;
                continue;
            }
            var best = finalists[0];
            for (var row = block; row < end; row++)
            {
                var offset = row - block;
                var selected = candidates[row][best.Choices[offset]];
                var previous = offset > 0 ? candidates[row - 1][best.Choices[offset - 1]] : null;
                var alternatives = finalists.Where(s => s.Score <= best.Score + profile.AmbiguityScoreMargin);
                var ambiguous = alternatives.Any(s => Math.Abs(candidates[row][s.Choices[offset]].DistanceFromTrackStart - selected.DistanceFromTrackStart) > profile.DistinctPassageMeters);
                if (offset > 0)
                    ambiguous |= alternatives.Any(s => Math.Abs(candidates[row - 1][s.Choices[offset - 1]].DistanceFromTrackStart - previous!.DistanceFromTrackStart) > profile.DistinctPassageMeters);
                var expected = references[row].Fields["分段距离KM"].ReferenceValue!.Value * 1000;
                var delta = previous is null ? double.NaN : selected.DistanceFromTrackStart - previous.DistanceFromTrackStart - expected;
                var audited = Math.Abs(delta) > Math.Max(profile.AbsoluteDistanceAuditMeters, expected * profile.RelativeDistanceAuditRatio);
                var status = references[row].IsCompositeSuspect ? "Composite" : ambiguous ? "Ambiguous" : previous is null ? "Excluded" : audited ? "Excluded" : Math.Abs(delta) <= 0.001 ? "Exact" : "Rounded";
                var reason = references[row].IsCompositeSuspect ? "组合/往返公式，区间尚未唯一复原，排除拟合。" : ambiguous ? "存在评分接近的不同经过位置。" : previous is null ? "起点或缺失节点后的区块首行，不形成可拟合分段。" : audited ? "实际长度与参考长度差异超过集中配置审计阈值。" : "按名称、完整几何和全序列距离联合对齐。";
                var eligible = previous is not null && !ambiguous && !audited && !references[row].IsCompositeSuspect && references[row].HasCompleteDistanceTargets && expected > 0;
                result[row] = Build(row, selected, previous, status, reason, eligible);
            }
            block = end;
        }
        return result;

        AlignedReferenceSegment Build(int index, TrackPosition? selected, TrackPosition? previous,
            string status, string reason, bool eligible)
        {
            double? distance = selected is not null && previous is not null ? selected.DistanceFromTrackStart - previous.DistanceFromTrackStart : null;
            var expected = references[index].Fields["分段距离KM"].ReferenceValue!.Value * 1000;
            return new(references[index], candidates[index], projections[index].Length, selected,
                previous?.DistanceFromTrackStart, selected?.DistanceFromTrackStart, distance,
                distance - expected, status, eligible, reason);
        }
    }

    public static string NormalizeName(string? name)
    {
        var value = (name ?? "").Trim().Replace('：', ':');
        foreach (var prefix in new[] { "徒步起点:", "起点:", "终点:" })
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return value[prefix.Length..].Trim();
        return value;
    }

    private static IReadOnlyList<TrackPosition> SeparatePassages(TrackPosition[] projections, double maximumChainageSeparation)
    {
        // 同一次经过的相邻线段只保留最精确投影，非相邻重复线段保留各次经过。
        var sorted = projections.OrderBy(p => p.SegmentIndex).ThenBy(p => p.StartPointIndex).ToArray();
        var groups = new List<List<TrackPosition>>();
        foreach (var p in sorted)
        {
            if (groups.Count == 0 || groups[^1][^1].SegmentIndex != p.SegmentIndex ||
                p.StartPointIndex > groups[^1][^1].EndPointIndex ||
                Math.Abs(p.DistanceFromTrackStart - groups[^1][0].DistanceFromTrackStart) > maximumChainageSeparation) groups.Add([]);
            groups[^1].Add(p);
        }
        return groups.Select(g => g.OrderBy(p => p.DistanceToTrack).ThenBy(p => p.DistanceFromTrackStart).First())
            .OrderBy(p => p.DistanceFromTrackStart).ToArray();
    }
}
