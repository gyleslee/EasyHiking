using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
namespace HikingGuide.Core.Tests;
public sealed class KilometerDirectionTests
{
    private static TrackSegment Part(params double[] lng)=>new(){Points=lng.Select(x=>new TrackPoint{Latitude=0,Longitude=x}).ToList()};
    [Fact] public void EndToEndConflictRequiresExplicitDirectionAndNeverEditsPoints()
    {
        var t=new Track{Segments=[Part(0,.01),Part(.02,.0101)]};var before=System.Text.Json.JsonSerializer.Serialize(t.Segments);
        Assert.True(KilometerRouteDirections.NeedsConfirmation(t));Assert.Empty(new TrackKilometerService().Generate(t));
        var parts=KilometerRouteDirections.Suggest(t);Assert.NotEqual(parts[0].Reversed,parts[1].Reversed);KilometerRouteDirections.Set(t,parts);
        Assert.False(KilometerRouteDirections.NeedsConfirmation(t));Assert.InRange(new TrackKilometerService().Generate(t)[^1].DistanceMeters,2220,2230);
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(t.Segments));
        KilometerRouteDirections.Set(t,KilometerRouteDirections.Reverse(parts));Assert.True(new TrackKilometerService().Generate(t)[0].Coordinate.Longitude>.01);
    }
    [Fact] public void LongOrderedGapHasNoFixedLengthLimit()
    {
        var t=new Track{Segments=[Part(0,.01),Part(.1,.11)]};Assert.False(new TrackKilometerService().Assess(t).Eligible);t.Metadata[TrackKilometerService.RouteKindKey]="route";Assert.True(new TrackKilometerService().Assess(t).Eligible);Assert.False(KilometerRouteDirections.NeedsConfirmation(t));Assert.InRange(new TrackKilometerService().Generate(t)[^1].DistanceMeters,12200,12250);
    }
    [Fact] public void SingleClosedTrackKeepsItsExistingStartAndEnd()
    {
        var t=new Track{Segments=[Part(0,.01,0)]};Assert.True(new TrackKilometerService().Assess(t).Eligible);Assert.False(KilometerRouteDirections.NeedsConfirmation(t));Assert.Equal(0,new TrackKilometerService().Generate(t)[^1].Coordinate.Longitude);
    }
    [Fact] public void RemovedSegmentInvalidatesVirtualDirectionConfiguration()
    {
        var t=new Track{Segments=[Part(0,.01),Part(.02,.0101)]};KilometerRouteDirections.Set(t,KilometerRouteDirections.Suggest(t));t.Segments.RemoveAt(1);Assert.Null(KilometerRouteDirections.Read(t));
    }
    [Fact] public void GeometryEditInvalidatesPreviousDirectionAndSingleRouteOverridesGapLimit()
    {
        var t=new Track{Segments=[Part(0,.01),Part(.1,.11)]};Assert.False(new TrackKilometerService().Assess(t).Eligible);
        t.Metadata[TrackKilometerService.RouteKindKey]="route";Assert.True(new TrackKilometerService().Assess(t).Eligible);
        KilometerRouteDirections.Set(t,KilometerRouteDirections.Natural(t));Assert.NotNull(KilometerRouteDirections.Read(t));t.NotifyGeometryChanged();Assert.Null(KilometerRouteDirections.Read(t));
    }
}
