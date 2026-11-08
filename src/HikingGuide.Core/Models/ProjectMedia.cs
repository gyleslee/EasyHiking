using System.Text.Json.Serialization;

namespace HikingGuide.Core.Models;

public enum MediaOwnerType { Waypoint, GuideNode }
public enum MediaSourceKind { KmzEmbedded, ExternalUrl }

public sealed class MediaBlob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Sha256 { get; set; } = "";
    public string MimeType { get; set; } = "";
    public string FileExtension { get; set; } = "";
    public long ByteLength { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string StoredPath { get; set; } = "";

    // Runtime-only source. Never serialized as JSON or exposed to the map page.
    [JsonIgnore] public byte[]? PendingBytes { get; set; }
    [JsonIgnore] public string? ArchiveFilePath { get; set; }
}

public sealed class MediaAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MediaOwnerType OwnerType { get; set; }
    public Guid OwnerId { get; set; }
    public Guid MediaBlobId { get; set; }
    public string MediaType { get; set; } = "Image";
    public string? OriginalFileName { get; set; }
    public string? Caption { get; set; }
    public MediaSourceKind SourceKind { get; set; }
    public string? SourceReference { get; set; }
    public int DisplayOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ExternalMediaReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MediaOwnerType OwnerType { get; set; }
    public Guid OwnerId { get; set; }
    public string Url { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Caption { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceField { get; set; }
    public int DisplayOrder { get; set; }
    public string? LastKnownMimeType { get; set; }
    public long? LastKnownByteLength { get; set; }
    public DateTimeOffset? LastSuccessfulFetchAt { get; set; }
}
