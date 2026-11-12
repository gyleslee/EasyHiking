using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Core.Geometry;
namespace HikingGuide.Core.Tests;
public sealed class TrackKilometerServiceTests
{
    private static TrackSegment Part(params double[] lng)=>new(){Points=lng.Select(x=>new TrackPoint{Latitude=0,Longitude=x}).ToList()};
    [Fact] public void ShortRouteHasOnlyFinalMarkAndDoesNotMutateRawPoints()
    {
        var t=new Track{Segments=[Part(0,.003)]};var json=System.Text.Json.JsonSerializer.Serialize(t);
        var marks=new TrackKilometerService().Generate(t);Assert.Single(marks);Assert.True(marks[0].IsEndpoint);Assert.InRange(marks[0].DistanceMeters,330,335);
        Assert.Equal(json,System.Text.Json.JsonSerializer.Serialize(t));Assert.False(TrackKilometerService.IsEnabled(t));
    }
    [Fact] public void SmallGapIsCountedAsStraightLineOnlyForMarkers()
    {
        var t=new Track{Segments=[Part(0,.0089),Part(.0091,.022)]};var length=t.GetTotalLength();var marks=new TrackKilometerService().Generate(t);
        Assert.Equal(new[]{1000d,2000d},marks.Where(m=>!m.IsEndpoint).Select(m=>m.DistanceMeters));
        Assert.InRange(marks[0].Coordinate.Longitude,.0089,.0091);
        Assert.Equal(length+GeoDistanceCalculator.Instance.CalculateDistance(new(0,.0089),new(0,.0091)),marks[^1].DistanceMeters,5);
        Assert.Equal(length,t.GetTotalLength());
    }
    [Fact] public void IndependentCollectionHasNoMarkers()
    {var t=new Track{Segments=[Part(0,.01),Part(.1,.11)]};t.Metadata[TrackKilometerService.RouteKindKey]="collection";Assert.False(new TrackKilometerService().Assess(t).Eligible);Assert.Empty(new TrackKilometerService().Generate(t));}
    [Fact] public void GapLimitIsExplicitAndAdjustable()
    {var t=new Track{Segments=[Part(0,.01),Part(.011,.02)]};Assert.True(new TrackKilometerService().Assess(t).Eligible);Assert.False(new TrackKilometerService(new(50)).Assess(t).Eligible);}
    [Fact] public void ClosedCollectionsAreRejected()
    {var t=new Track{Segments=[Part(0,.01,0),Part(0,.02,0)]};t.Metadata[TrackKilometerService.RouteKindKey]="collection";Assert.False(new TrackKilometerService().Assess(t).Eligible);}
    [Fact] public void DatelineAndRepeatedPointsPreserveCorrectDirection()
    {var t=new Track{Segments=[Part(179.99,179.99,-179.99)]};var marks=new TrackKilometerService().Generate(t);Assert.True(marks[0].Coordinate.Longitude>179.99);Assert.True(marks[1].Coordinate.Longitude< -179.99);Assert.InRange(marks[^1].DistanceMeters,2220,2230);}
    [Fact] public void ReverseRestartsFromNewStartAndFlagsSurviveDeepCopy()
    {var t=new Track{Segments=[Part(0,.03)]};TrackKilometerService.SetEnabled(t,true);Assert.True(TrackKilometerService.IsEnabled(System.Text.Json.JsonSerializer.Deserialize<Track>(System.Text.Json.JsonSerializer.Serialize(t))!));t.Segments[0].Points.Reverse();var m=new TrackKilometerService().Generate(t);Assert.True(m[0].Coordinate.Longitude<.03);Assert.Equal(0,m[^1].Coordinate.Longitude);}
    [Fact] public void CancelledGenerationStops()
    {var t=new Track{Segments=[Part(0,.03)]};Assert.Throws<OperationCanceledException>(()=>new TrackKilometerService().Generate(t,new CancellationToken(true)));}
}

