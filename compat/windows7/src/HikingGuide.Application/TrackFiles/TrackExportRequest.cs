using HikingGuide.Core.Models;

namespace HikingGuide.Application.TrackFiles;

public sealed class TrackExportRequest
{
    public string? DocumentName { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<Track> Tracks { get; init; } = [];

    public IReadOnlyList<Waypoint> Waypoints { get; init; } = [];

    public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
        new Dictionary<string, string?>();
}
