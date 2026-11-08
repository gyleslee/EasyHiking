namespace HikingGuide.Application.TrackFiles;

public interface ITrackImporter
{
    TrackFileFormat Format { get; }

    Task<TrackImportResult> ImportAsync(
        string filePath,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
