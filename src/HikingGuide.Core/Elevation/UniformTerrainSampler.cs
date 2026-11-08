using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Elevation;

/// <summary>原始几何均匀采样，仅委托既有高程Provider读取DEM，不读取来源文件海拔。</summary>
public static class UniformTerrainSampler
{
    public static async Task<IReadOnlyList<TerrainElevationSample>> SampleDemAsync(
        IReadOnlyList<TrackSegment> parts, Guid databaseId, IElevationProvider provider,
        double spacing, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(spacing) || spacing <= 0 || spacing > 1000) throw new ArgumentException("里程采样间距无效。");
        var output = new List<TerrainElevationSample>();
        var offset = 0d;
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var points = parts[partIndex].Points;
            if (points.Count == 0) continue;
            var distances = new double[points.Count];
            for (var i = 1; i < points.Count; i++) distances[i] = distances[i - 1] + GeoDistanceCalculator.Instance.CalculateDistance(points[i - 1].Coordinate, points[i].Coordinate);
            var length = distances[^1];
            var cursor = 0;
            for (var local = 0d; ; local = Math.Min(local + spacing, length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (cursor + 1 < points.Count && distances[cursor + 1] < local) cursor++;
                var coordinate = points[cursor].Coordinate;
                if (cursor + 1 < points.Count && distances[cursor + 1] > distances[cursor])
                {
                    var fraction = Math.Clamp((local - distances[cursor]) / (distances[cursor + 1] - distances[cursor]), 0, 1);
                    var next = points[cursor + 1].Coordinate;
                    coordinate = new GeoCoordinate(coordinate.Latitude + (next.Latitude - coordinate.Latitude) * fraction,
                        coordinate.Longitude + (next.Longitude - coordinate.Longitude) * fraction);
                }
                var height = await provider.SampleAsync(databaseId, coordinate, cancellationToken);
                output.Add(new(partIndex, offset + local, coordinate, height is double h && double.IsFinite(h) ? h : null));
                if (local >= length) break;
            }
            offset += length;
        }
        return output;
    }
}
