using HikingGuide.Infrastructure.Logging;

namespace HikingGuide.Infrastructure.Tests;

public sealed class SerilogLoggingServiceTests
{
    [Fact]
    public void Information_WritesChineseMessageToLogFile()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "HikingGuide.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);

        try
        {
            using (var logger = new SerilogLoggingService(logDirectory, enableDebugLogging: true))
            {
                logger.Information("日志中文测试");
            }

            var logFile = Assert.Single(Directory.GetFiles(logDirectory, "hiking-guide-*.log"));
            var content = File.ReadAllText(logFile);
            Assert.Contains("日志中文测试", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }
}
