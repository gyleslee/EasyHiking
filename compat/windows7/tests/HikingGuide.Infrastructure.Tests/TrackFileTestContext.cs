using HikingGuide.Application.Logging;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Infrastructure.TrackFiles;

namespace HikingGuide.Infrastructure.Tests;

internal sealed class TrackFileTestContext : IDisposable
{
    public TrackFileTestContext()
    {
        TemporaryDirectory = Path.Combine(Path.GetTempPath(), "HikingGuide.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TemporaryDirectory);
        Service = new TrackFileService(
            [new GpxTrackImporter(), new KmlTrackImporter(), new KmzTrackImporter()],
            [new GpxTrackExporter(), new KmlTrackExporter()],
            new NullLoggingService());
    }

    public ITrackFileService Service { get; }

    public string TemporaryDirectory { get; }

    public static string DataPath(string format, string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", format, fileName);

    public string OutputPath(string fileName) => Path.Combine(TemporaryDirectory, fileName);

    public void Dispose()
    {
        if (Directory.Exists(TemporaryDirectory))
        {
            Directory.Delete(TemporaryDirectory, recursive: true);
        }
    }

    private sealed class NullLoggingService : ILoggingService
    {
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(Exception exception, string message) { }
        public void Dispose() { }
    }
}
