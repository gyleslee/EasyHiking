namespace HikingGuide.Application.Media;

public sealed class ExternalMediaDownloadProfile
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public long MaximumDownloadBytes { get; init; } = 25L * 1024 * 1024;
    public int MaximumRedirects { get; init; } = 5;
    public IReadOnlyList<string> AllowedSchemes { get; init; } = ["http", "https"];
    public string UserAgent { get; init; } = "HikingGuide/0.5.1";
}

public sealed record ExternalMediaDownloadResult(
    string CachePath, string MimeType, string Extension, long ByteLength,
    string Sha256, int Width, int Height, bool FromCache);

public interface IExternalMediaDownloadService
{
    Task<ExternalMediaDownloadResult?> TryGetCachedAsync(string url,
        CancellationToken cancellationToken = default);

    Task<ExternalMediaDownloadResult> DownloadAsync(string url, bool forceRefresh = false,
        IProgress<(long Received, long? Total)>? progress = null,
        CancellationToken cancellationToken = default);
}
