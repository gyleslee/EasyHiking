using HikingGuide.Application.Maps;

namespace HikingGuide.App.ViewModels;

public sealed partial class MainViewModel
{
    internal void AddMarkerForAutomation(MapMarkerEditKind kind, HikingGuide.Core.Geometry.GeoCoordinate coordinate) =>
        OnMapMarkerEditRequested(this,new(kind,null,coordinate));
    internal Task ChangeMainTrackForAutomationAsync(Guid? trackId) =>
        ChangeMainTrackAsync(trackId.HasValue ? CurrentProject!.Tracks.Single(t => t.Id == trackId) : null);
    internal async Task EnterProjectEditForAutomationAsync(Guid trackId)
    {
        await EnterTrackEditForAsync(CurrentProject?.Tracks.FirstOrDefault(track => track.Id == trackId));
        await _trackEditContextChangeTask;
    }

    internal Guid? EditingTrackIdForAutomation => _trackEditTrack?.Id;
    internal Guid? EditingSessionForAutomation => _trackEditSessionId;
    internal Task EndProjectEditForAutomationAsync() => ExitTrackEditAsync();
    internal Task WaitForTrackEditTargetForAutomationAsync() => _trackEditContextChangeTask;
    internal void SendTrackEditEventForAutomation(MapEventMessage value) =>
        OnMapTrackEditRequested(this, value);

    internal async Task SplitFixtureTrackForAutomationAsync(Guid trackId)
    {
        var project = CurrentProject!;
        var track = project.Tracks.Single(item => item.Id == trackId);
        _trackEdits.PrepareSplitSegment(project, trackId, track.Segments[0].Id,
            track.Segments[0].Points.Count / 2 - 1, .5).Apply();
        RefreshProjectView();
        await _mapRefreshTask;
    }
}
