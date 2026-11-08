using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public interface IGlobalSettingsService
{
    GlobalSettings Current { get; }
    string SettingsFilePath { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    MapSourceDefinition ResolveMapSource(Guid? requestedId, out bool usedFallback);
    event EventHandler? MapSettingsChanged { add { } remove { } }
    string? MapSettingsWarning => null;
    void NotifyMapSettingsChanged() { }
}

public interface IMapSourceTestService
{
    Task<MapSourceTestResult> TestAsync(MapSourceDefinition source, CancellationToken cancellationToken = default);
}

public sealed record MapSourceTestResult(bool IsSuccess, int? HttpStatusCode, string Message, TimeSpan Duration)
{
    public IReadOnlyList<MapServerTestResult> Servers { get; init; } = [];
}
public sealed record MapServerTestResult(string ServerToken,bool IsSuccess,int? HttpStatusCode,string Message,TimeSpan Duration);
