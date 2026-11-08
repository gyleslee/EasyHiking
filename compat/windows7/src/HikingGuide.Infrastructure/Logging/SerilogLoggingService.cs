using HikingGuide.Application.Logging;
using Serilog;
using Serilog.Events;

namespace HikingGuide.Infrastructure.Logging;

public sealed class SerilogLoggingService : ILoggingService
{
    private readonly ILogger _logger;

    public SerilogLoggingService(string logDirectory, bool enableDebugLogging = false)
    {
        HikingGuide.Core.Compatibility.LegacyArgument.ThrowIfNullOrWhiteSpace(logDirectory);
        Directory.CreateDirectory(logDirectory);

        _logger = new LoggerConfiguration()
            .MinimumLevel.Is(enableDebugLogging ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(logDirectory, "hiking-guide-.log"),
                rollingInterval: RollingInterval.Day,
                shared: true,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public void Debug(string message) => _logger.Debug("{Message}", message);
    public void Information(string message) => _logger.Information("{Message}", message);
    public void Warning(string message) => _logger.Warning("{Message}", message);
    public void Error(Exception exception, string message) => _logger.Error(exception, "{Message}", message);

    public void Dispose()
    {
        if (_logger is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
