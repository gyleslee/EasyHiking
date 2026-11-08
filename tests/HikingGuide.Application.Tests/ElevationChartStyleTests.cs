using HikingGuide.Application.Elevation;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class ElevationChartStyleTests
{
    [Fact]
    public void UsesLiveTrackColorWithoutAnotherChartColor()
    {
        var project = new HikingProject();
        var track = new Track { DisplayColor = "#304FFE" };
        project.AddTrack(track);
        Assert.Equal("#304FFE", ElevationChartStyleResolver.Resolve(project, track.Id).ColorHex);
        track.DisplayColor = "#22AA44";
        Assert.Equal("#22AA44", ElevationChartStyleResolver.Resolve(project, track.Id).ColorHex);
    }

    [Fact]
    public void MissingIndependentSectionColor_InheritsParentTrack()
    {
        var project = new HikingProject();
        var track = new Track { DisplayColor = "#BC3188" };
        project.AddTrack(track);
        Assert.Equal("#BC3188", ElevationChartStyleResolver.Resolve(project, track.Id).ColorHex);
        Assert.Equal(3, ElevationChartStyleResolver.Resolve(project, track.Id).Profile.LineWidth);
    }
}
