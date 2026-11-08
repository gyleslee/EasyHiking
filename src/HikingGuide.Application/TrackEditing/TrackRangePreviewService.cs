using HikingGuide.Application.Guides;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.TrackEditing;

public sealed record TrackRangePreview(IReadOnlyList<IReadOnlyList<MapCoordinate>> DisplayPieces,
    double StartMeters, double EndMeters, int SegmentCount, int PointCount);

/// <summary>仅为地图生成有界显示副本；裁剪计算始终读取完整 TrackPosition。</summary>
public sealed class TrackRangePreviewService(TrackEditDisplayProfile? profile = null)
{
    private readonly TrackEditDisplayProfile _profile = profile ?? new TrackEditDisplayProfile();
    private readonly GuideStructureService _guide = new();
    private readonly DouglasPeuckerTrackDisplaySimplifier _simplifier = new();

    public TrackRangePreview Build(Track track, TrackRange selected, double zoom,
        CancellationToken cancellationToken = default)
    {
        _profile.Validate();
        var start = selected.Start.DistanceFromTrackStart <= selected.End.DistanceFromTrackStart
            ? selected.Start : selected.End;
        var end = ReferenceEquals(start, selected.Start) ? selected.End : selected.Start;
        var pieces = _guide.ExtractRange(track, start, end);
        var temporary = new Track();
        var count = 0;
        foreach (var piece in pieces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count += piece.Count;
            temporary.Segments.Add(new TrackSegment { Points = piece.Select(coordinate =>
                new TrackPoint { Latitude = coordinate.Latitude, Longitude = coordinate.Longitude }).ToList() });
        }
        var simplified = _simplifier.Simplify(temporary, false, zoom, cancellationToken).Segments;
        var total = simplified.Sum(piece => piece.Count);
        if (total <= _profile.MaxRangePreviewPoints)
            return new TrackRangePreview(simplified, start.DistanceFromTrackStart,
                end.DistanceFromTrackStart, pieces.Count, count);
        var budget = _profile.MaxRangePreviewPoints;
        var bounded = new List<IReadOnlyList<MapCoordinate>>(simplified.Count);
        foreach (var piece in simplified)
        {
            var allocation = Math.Max(2, (int)Math.Round((double)piece.Count / total * budget));
            if (piece.Count <= allocation) { bounded.Add(piece); continue; }
            var sample = new List<MapCoordinate>(allocation);
            for (var i = 0; i < allocation; i++)
                sample.Add(piece[(int)Math.Round((double)i / (allocation - 1) * (piece.Count - 1))]);
            bounded.Add(sample);
        }
        return new TrackRangePreview(bounded, start.DistanceFromTrackStart,
            end.DistanceFromTrackStart, pieces.Count, count);
    }
}
