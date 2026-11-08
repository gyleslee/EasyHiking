using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Calibration;

public sealed record CalibrationSamplingProfile(double SampleSpacingMeters = 5,
    double MinimumFullCoverageRatio = 0.99)
{
    public void Validate()
    {
        if (!double.IsFinite(SampleSpacingMeters) || SampleSpacingMeters <= 0 || SampleSpacingMeters > 1000 ||
            !double.IsFinite(MinimumFullCoverageRatio) || MinimumFullCoverageRatio is <= 0 or > 1)
            throw new ArgumentException("校准采样参数超出允许范围。");
    }
}

public sealed record CalibrationDemPoint(int PartIndex, double DistanceMeters,
    GeoCoordinate Coordinate, double? DemElevationMeters);

/// <summary>仅几何坐标参与校准采样。源海拔不参与采样位置、覆盖判断或输出。</summary>
public sealed class CalibrationDemSampler
{
    public async Task<IReadOnlyList<CalibrationDemPoint>> SampleAsync(
        IReadOnlyList<TrackSegment> parts, Guid databaseId, IElevationProvider provider,
        CalibrationSamplingProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        var result = new List<CalibrationDemPoint>();
        var offset = 0d;
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var points = parts[partIndex].Points;
            if (points.Count == 0) continue;
            var distances = new double[points.Count];
            for (var index = 1; index < points.Count; index++)
                distances[index] = distances[index - 1] + GeoDistanceCalculator.Instance.CalculateDistance(
                    points[index - 1].Coordinate, points[index].Coordinate);
            var length = distances[^1];
            var cursor = 0;
            for (var local = 0d; ; local = Math.Min(local + profile.SampleSpacingMeters, length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (cursor + 1 < points.Count && distances[cursor + 1] < local) cursor++;
                var coordinate = points[cursor].Coordinate;
                if (cursor + 1 < points.Count && distances[cursor + 1] > distances[cursor])
                {
                    var fraction = Math.Clamp((local - distances[cursor]) /
                        (distances[cursor + 1] - distances[cursor]), 0, 1);
                    var next = points[cursor + 1].Coordinate;
                    coordinate = new(coordinate.Latitude + (next.Latitude - coordinate.Latitude) * fraction,
                        coordinate.Longitude + (next.Longitude - coordinate.Longitude) * fraction);
                }
                var height = await provider.SampleAsync(databaseId, coordinate, cancellationToken);
                result.Add(new(partIndex, offset + local, coordinate,
                    height.HasValue && double.IsFinite(height.Value) ? height : null));
                if (local >= length) break;
            }
            offset += length; // 不跨真实轨迹段拼接Gap。
        }
        return result;
    }
}
