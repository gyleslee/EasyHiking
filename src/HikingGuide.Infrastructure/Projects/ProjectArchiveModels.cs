using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Projects;

internal sealed class ProjectManifest
{
    public string Format { get; set; } = ProjectFileService.FormatName;
    public int ProjectVersion { get; set; }
    public string ApplicationVersion { get; set; } = ProjectFileService.ApplicationVersion;
    public Guid ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public int TrackCount { get; set; }
    public int WaypointCount { get; set; }
}

internal sealed class ProjectDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public int ProjectVersion { get; set; }
    public Guid? ActiveTrackId { get; set; }
    public Guid? MainGuideTrackId { get; set; }
    public bool HasMainGuideTrackSelection { get; set; }
    public bool IsAnalysisResultStale { get; set; }
    public List<Guid> TrackIds { get; set; } = [];
}

internal sealed class ProjectArchiveSnapshot
{
    public required ProjectManifest Manifest { get; init; }
    public required ProjectDocument Project { get; init; }
    public required IReadOnlyDictionary<Guid, byte[]> Tracks { get; init; }
    public required byte[] Waypoints { get; init; }
    public required byte[] GuideNodes { get; init; }
    public required byte[] GuideSegments { get; init; }
    public required byte[] RetiredGuideSegments { get; init; }
    public required byte[] Routing { get; init; }
    public required byte[] ManualData { get; init; }
    public required byte[] AnalysisProfile { get; init; }
    public required byte[] AnalysisResult { get; init; }
    public required byte[] ElevationAnalyses { get; init; }
    public required byte[] Settings { get; init; }
    public required IReadOnlyDictionary<string, byte[]> GuideData { get; init; }
    public required byte[] MediaManifest { get; init; }
    public required IReadOnlyList<MediaBlobCopySource> MediaSources { get; init; }
}

internal sealed class ProjectMediaManifest
{
    public List<MediaBlob> Blobs { get; set; } = [];
    public List<MediaAttachment> Attachments { get; set; } = [];
    public List<ExternalMediaReference> ExternalReferences { get; set; } = [];
}

internal sealed record MediaBlobCopySource(string EntryPath, byte[]? PendingBytes, string? ArchiveFilePath);
