namespace HikingGuide.Application.TrackFiles;

public interface ITrackFileService
{
    Task<TrackImportResult> ImportAsync(
        string filePath,
        TrackFileFormat? expectedFormat = null,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task ExportAsync(
        TrackFileFormat format,
        TrackExportRequest request,
        string filePath,
        bool overwrite = false,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
