namespace HikingGuide.Application.Logging;

public interface ILoggingService : IDisposable
{
    void Debug(string message);
    void Information(string message);
    void Warning(string message);
    void Error(Exception exception, string message);
}
