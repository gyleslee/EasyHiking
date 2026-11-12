using System.Text.Json;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
namespace HikingGuide.Application.Tests;
public sealed class KilometerGeoJsonTests
{
    [Fact] public void DefaultHiddenThenVisibleAndFinalHasOneDecimal()
    {
        var t=new Track{Segments=[new(){Points=[new(){Latitude=23,Longitude=113},new(){Latitude=23,Longitude=113.025}]}]};var p=new HikingProject();p.AddTrack(t,false);var b=new KilometerGeoJsonBuilder();
        JsonElement Features()=>JsonSerializer.SerializeToElement(b.Build(p)).GetProperty("features");
        Assert.Empty(Features().EnumerateArray());TrackKilometerService.SetEnabled(t,true);var f=Features();Assert.Equal(3,f.GetArrayLength());Assert.Equal("1",f[0].GetProperty("properties").GetProperty("label").GetString());Assert.Matches("^[0-9]+\\.[0-9]$",f[2].GetProperty("properties").GetProperty("label").GetString()!);
        TrackKilometerService.SetEnabled(t,false);Assert.Empty(Features().EnumerateArray());
    }
    [Fact] public async Task ConcurrentMapBuildsDoNotRaceCacheEntries()
    {
        var t=new Track{Segments=[new(){Points=[new(){Latitude=23,Longitude=113},new(){Latitude=23,Longitude=113.025}]}]};TrackKilometerService.SetEnabled(t,true);var p=new HikingProject();p.AddTrack(t,false);var b=new KilometerGeoJsonBuilder();
        var results=await Task.WhenAll(Enumerable.Range(0,20).Select(_=>Task.Run(()=>JsonSerializer.Serialize(b.Build(p)))));Assert.Single(results.Distinct());
    }
}
