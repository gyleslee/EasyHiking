using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Elevation;

public sealed record ElevationChartStyleProfile(float LineWidth = 3,
    byte FillTopAlpha = 72, byte FillBottomAlpha = 3);

public sealed record ElevationChartStyle(string ColorHex,
    ElevationChartStyleProfile Profile);

public static class ElevationChartStyleResolver
{
    public static ElevationChartStyle Resolve(HikingProject project, Guid trackId,
        ElevationChartStyleProfile? profile = null)
    {
        var track = project.Tracks.FirstOrDefault(item => item.Id == trackId);
        var color = TrackDisplayColor.IsValid(track?.DisplayColor) ?
            track!.DisplayColor! : "#2877C7";
        return new(color, profile ?? new());
    }
}
