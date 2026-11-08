namespace HikingGuide.Application.TrackFiles;

public sealed class TrackExportException(string message, Exception? innerException = null)
    : Exception(message, innerException);
