using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.TrackFiles;

public sealed class TrackImportResult
{
    public List<Track> Tracks { get; } = [];

    public List<Waypoint> Waypoints { get; } = [];

    public List<MediaBlob> MediaBlobs { get; } = [];

    public List<MediaAttachment> MediaAttachments { get; } = [];

    public List<ExternalMediaReference> ExternalMediaReferences { get; } = [];

    public Dictionary<string, string?> Metadata { get; } = [];

    public List<ImportWarning> Warnings { get; } = [];

    public List<ImportError> Errors { get; } = [];

    public TrackFileFormat SourceFormat { get; set; }

    public string? SourceFile { get; set; }

    public bool IsSuccess => Errors.Count == 0;

    public int TrackSegmentCount => Tracks.Sum(track => track.Segments.Count);

    public int TrackPointCount => Tracks.Sum(track => track.GetPointCount());

    public double TotalDistance => Tracks.Sum(track => track.GetTotalLength());

    public int TrackPointsWithElevation => Tracks
        .SelectMany(track => track.Segments)
        .SelectMany(segment => segment.Points)
        .Count(point => point.SourceElevation.HasValue);

    public int TrackPointsWithTimestamp => Tracks
        .SelectMany(track => track.Segments)
        .SelectMany(segment => segment.Points)
        .Count(point => point.Timestamp.HasValue);

    public GeoBoundingBox? GetBoundingBox()
    {
        GeoBoundingBox? result = null;
        foreach (var track in Tracks)
        {
            var trackBounds = track.GetBoundingBox();
            if (trackBounds is null)
            {
                continue;
            }

            if (result is null)
            {
                result = new GeoBoundingBox(
                    trackBounds.MinLatitude,
                    trackBounds.MaxLatitude,
                    trackBounds.MinLongitude,
                    trackBounds.MaxLongitude);
                continue;
            }

            result.ExpandToInclude(new GeoCoordinate(trackBounds.MinLatitude, trackBounds.MinLongitude));
            result.ExpandToInclude(new GeoCoordinate(trackBounds.MaxLatitude, trackBounds.MaxLongitude));
        }

        return result;
    }
}
