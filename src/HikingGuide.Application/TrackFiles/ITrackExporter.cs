namespace HikingGuide.Application.TrackFiles;

public interface ITrackExporter
{
    TrackFileFormat Format { get; }

    Task ExportAsync(
        TrackExportRequest request,
        string filePath,
        bool overwrite = false,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
