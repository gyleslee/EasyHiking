using System.Security.Cryptography;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Elevation;

/// <summary>P8 基础算法；来源采样与统计处理分离，不宣称 Locus 一致。</summary>
public sealed class StandardElevationAnalysisV1
{
    private readonly IGeoDistanceService _distance;
    public StandardElevationAnalysisV1(IGeoDistanceService? distance = null) =>
        _distance = distance ?? GeoDistanceCalculator.Instance;

    public string GeometryFingerprint(ElevationTargetDescriptor target)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in target.GeometryParts)
        {
            hash.AppendData(part.Id.ToByteArray());
            foreach (var point in part.Points)
            {
                hash.AppendData(BitConverter.GetBytes(point.Latitude));
                hash.AppendData(BitConverter.GetBytes(point.Longitude));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public string GpsFingerprint(ElevationTargetDescriptor target)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in target.GeometryParts)
        {
            hash.AppendData(part.Id.ToByteArray());
            foreach (var point in part.Points)
            {
                hash.AppendData(BitConverter.GetBytes(point.SourceElevation ?? double.NaN));
                hash.AppendData([point.IsCoordinateEdited ? (byte)1 : (byte)0]);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public IReadOnlyList<ElevationGridPoint> BuildGrid(ElevationTargetDescriptor target,
        ElevationAnalysisProfile profile)
    {
        profile.Validate();
        var grid = new List<ElevationGridPoint>();
        var total = 0d;
        for (var partIndex = 0; partIndex < target.GeometryParts.Count; partIndex++)
        {
            var points = target.GeometryParts[partIndex].Points;
            if (points.Count == 0) continue;
            var chainages = new double[points.Count];
            for (var i = 1; i < points.Count; i++)
                chainages[i] = chainages[i - 1] + _distance.CalculateDistance(
                    points[i - 1].Coordinate, points[i].Coordinate);
            var partLength = chainages[^1];
            var locations = new SortedSet<double>();
            for (var local = 0d; local < partLength; local += profile.SampleSpacingMeters)
                locations.Add(local);
            locations.Add(partLength);
            // 采样间隔不能掩盖短于 10m 的 GPS 缺失点：额外放入缺失边界，
            // DEM 与 GPS 仍共用完全相同的里程轴。
            for (var i = 0; i < points.Count; i++)
                if (points[i].IsCoordinateEdited || !Finite(points[i].SourceElevation).HasValue)
                    locations.Add(chainages[i]);
            var cursor = 0;
            foreach (var local in locations)
            {
                while (cursor + 1 < chainages.Length && chainages[cursor + 1] < local)
                    cursor++;
                grid.Add(Interpolate(points, chainages, cursor, local, partIndex, total));
            }
            total += partLength; // 不加物理段之间的直线距离。
        }
        return grid;
    }

    private static ElevationGridPoint Interpolate(IReadOnlyList<TrackPoint> points,
        double[] distances, int index, double local, int part, double offset)
    {
        var a = points[index];
        if (index + 1 >= points.Count || local <= distances[index] + 1e-9)
            return new(part, offset + local, a.Coordinate,
                a.IsCoordinateEdited ? null : Finite(a.SourceElevation));
        var b = points[index + 1];
        var span = distances[index + 1] - distances[index];
        if (span <= 0) return new(part, offset + local, b.Coordinate,
            b.IsCoordinateEdited ? null : Finite(b.SourceElevation));
        var fraction = Math.Clamp((local - distances[index]) / span, 0, 1);
        var coordinate = new GeoCoordinate(
            a.Latitude + (b.Latitude - a.Latitude) * fraction,
            a.Longitude + (b.Longitude - a.Longitude) * fraction);
        var left = a.IsCoordinateEdited ? null : Finite(a.SourceElevation);
        var right = b.IsCoordinateEdited ? null : Finite(b.SourceElevation);
        double? gps = fraction < 1e-9 ? left : fraction > 1 - 1e-9 ? right :
            left.HasValue && right.HasValue ? left.Value +
                (right.Value - left.Value) * fraction : null;
        return new(part, offset + local, coordinate, gps);
    }

    private static double? Finite(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;

    public async Task<ElevationAnalysisResult> AnalyzeAsync(ElevationTargetDescriptor target,
        ElevationSourceDescriptor source, ElevationAnalysisProfile profile,
        IElevationProvider? demProvider = null, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var grid = BuildGrid(target, profile);
        if (source.SourceType == ElevationSourceType.Dem &&
            (source.DemDatabaseId is null || demProvider is null))
            throw new ArgumentException("DEM高程分析需要已挂载的数据库及采样服务。", nameof(source));
        var raw = new double?[grid.Count];
        for (var i = 0; i < grid.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            raw[i] = source.SourceType == ElevationSourceType.Gps
                ? grid[i].GpsElevationMeters
                : await demProvider!.SampleAsync(source.DemDatabaseId!.Value,
                    grid[i].Coordinate, cancellationToken);
            raw[i] = Finite(raw[i]);
            if (i % 100 == 0 || i == grid.Count - 1) progress?.Report((i + 1, grid.Count));
        }
        var processed = Smooth(grid, raw, profile.SmoothingWindowMeters);
        var slopes = CalculateSlopes(grid, processed, profile.SlopeWindowMeters);
        var series = new ElevationSeriesPoint[grid.Count];
        for (var i = 0; i < grid.Count; i++)
            series[i] = new(grid[i].PartIndex, grid[i].DistanceMeters,
                grid[i].Coordinate, raw[i], processed[i], slopes[i]);
        return new(Guid.NewGuid(), target.ProjectId, target.TargetType, target.TargetId,
            target.TrackId, GeometryFingerprint(target), source, profile.Version, profile,
            series, Summarize(series), DateTimeOffset.UtcNow);
    }

    private static double?[] Smooth(IReadOnlyList<ElevationGridPoint> grid,
        IReadOnlyList<double?> raw, double window)
    {
        var result = new double?[raw.Count];
        for (var start = 0; start < raw.Count;)
        {
            if (raw[start] is null) { start++; continue; }
            var end = start + 1;
            while (end < raw.Count && raw[end].HasValue &&
                   grid[end].PartIndex == grid[start].PartIndex) end++;
            var left = start;
            var right = start;
            var sum = 0d;
            for (var i = start; i < end; i++)
            {
                while (right < end && grid[right].DistanceMeters <=
                       grid[i].DistanceMeters + window / 2 + 1e-9)
                    sum += raw[right++]!.Value;
                while (left < right && grid[left].DistanceMeters <
                       grid[i].DistanceMeters - window / 2 - 1e-9)
                    sum -= raw[left++]!.Value;
                result[i] = sum / (right - left);
            }
            start = end;
        }
        return result;
    }

    private static double?[] CalculateSlopes(IReadOnlyList<ElevationGridPoint> grid,
        IReadOnlyList<double?> elevation, double window)
    {
        var slopes = new double?[grid.Count];
        for (var start = 0; start < grid.Count;)
        {
            if (elevation[start] is null) { start++; continue; }
            var end = start + 1;
            while (end < grid.Count && elevation[end].HasValue &&
                   grid[end].PartIndex == grid[start].PartIndex) end++;
            var left = start;
            var right = start;
            for (var i = start; i < end; i++)
            {
                while (left + 1 < i && grid[left + 1].DistanceMeters <=
                       grid[i].DistanceMeters - window / 2) left++;
                if (right < i) right = i;
                while (right + 1 < end && grid[right + 1].DistanceMeters <=
                       grid[i].DistanceMeters + window / 2) right++;
                if (right > left)
                {
                    var distance = grid[right].DistanceMeters - grid[left].DistanceMeters;
                    if (distance > 0) slopes[i] =
                        (elevation[right]!.Value - elevation[left]!.Value) / distance * 100;
                }
            }
            start = end;
        }
        return slopes;
    }

    public static ElevationSummary Summarize(IReadOnlyList<ElevationSeriesPoint> points)
    {
        var valid = points.Where(point => point.ProcessedElevationMeters.HasValue).ToArray();
        var coverage = points.Count == 0 ? 0 : (double)valid.Length / points.Count;
        var availability = valid.Length == 0 ? ElevationAvailability.Unavailable :
            valid.Length == points.Count ? ElevationAvailability.Complete : ElevationAvailability.Partial;
        var ascent = 0d;
        var descent = 0d;
        var ascentDistance = 0d;
        var descentDistance = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            if (a.PartIndex != b.PartIndex || a.ProcessedElevationMeters is null ||
                b.ProcessedElevationMeters is null) continue;
            var distance = b.DistanceMeters - a.DistanceMeters;
            if (distance <= 0) continue;
            var delta = b.ProcessedElevationMeters.Value - a.ProcessedElevationMeters.Value;
            if (delta > 0) { ascent += delta; ascentDistance += distance; }
            else if (delta < 0) { descent -= delta; descentDistance += distance; }
        }
        var highest = valid.MaxBy(point => point.ProcessedElevationMeters);
        var lowest = valid.MinBy(point => point.ProcessedElevationMeters);
        var complete = availability == ElevationAvailability.Complete;
        var distanceTotal = points.Count == 0 ? 0 : points[^1].DistanceMeters;
        return new(distanceTotal, points.FirstOrDefault()?.ProcessedElevationMeters,
            points.LastOrDefault()?.ProcessedElevationMeters,
            highest?.ProcessedElevationMeters, lowest?.ProcessedElevationMeters,
            valid.Length == 0 ? null : valid.Average(point => point.ProcessedElevationMeters!.Value),
            complete ? ascent : null, complete ? descent : null,
            complete && points.Count > 0 ? points[^1].ProcessedElevationMeters -
                points[0].ProcessedElevationMeters : null,
            complete && ascentDistance > 0 ? ascent / ascentDistance * 100 :
                complete ? 0 : null,
            complete && descentDistance > 0 ? descent / descentDistance * 100 :
                complete ? 0 : null,
            valid.Select(point => point.SlopePercent).Where(value => value > 0)
                .DefaultIfEmpty().Max(),
            valid.Select(point => point.SlopePercent).Where(value => value < 0)
                .Select(value => -value).DefaultIfEmpty().Max(),
            highest?.DistanceMeters, lowest?.DistanceMeters, coverage, points.Count, availability);
    }

    public static ElevationComparisonSummary Compare(ElevationAnalysisResult left,
        ElevationAnalysisResult right)
    {
        if (left.Series.Count != right.Series.Count ||
            left.Series.Where((point, i) => point.PartIndex != right.Series[i].PartIndex ||
                Math.Abs(point.DistanceMeters - right.Series[i].DistanceMeters) > 0.001).Any())
            throw new InvalidOperationException("两个高程来源未使用同一分析里程轴。");
        var differences = left.Series.Zip(right.Series)
            .Where(pair => pair.First.ProcessedElevationMeters.HasValue &&
                           pair.Second.ProcessedElevationMeters.HasValue)
            .Select(pair => pair.Second.ProcessedElevationMeters!.Value -
                pair.First.ProcessedElevationMeters!.Value).ToArray();
        return new(differences.Length,
            differences.Length == 0 ? null : differences.Average(),
            differences.Length == 0 ? null : differences.Average(Math.Abs),
            differences.Length == 0 ? null : differences.Max(value => Math.Abs(value)),
            differences.Length == 0 ? null : Math.Sqrt(differences.Average(value => value * value)));
    }
}
