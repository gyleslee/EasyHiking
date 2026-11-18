using System.Text.Json;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using Xunit;
namespace HikingGuide.Application.Tests;
public sealed class TrackDisplaySettingsTests
{
    [Fact]public void DefaultsAndCorruptValuesAreSafeAndOnlySelectionHasColor()
    {
        var d=TrackDisplaySettings.Normalize(null);Assert.Equal(8,d.Selected.Width);Assert.Equal("both",d.Selected.Endpoints);
        var result=TrackDisplaySettings.Normalize(new(){SelectedColor="bad",MainGuide=new(){Width=double.NaN,Opacity=2,EndpointSize=-1,LineStyle="bad",Endpoints="bad",EndpointMinZoom=30}});
        Assert.Equal("#FD9700",result.SelectedColor);Assert.Equal(7,result.MainGuide.Width);Assert.Equal(1,result.MainGuide.Opacity);Assert.Equal(4,result.MainGuide.EndpointSize);Assert.Equal(22,result.MainGuide.EndpointMinZoom);Assert.Equal("solid",result.MainGuide.LineStyle);
        Assert.DoesNotContain(typeof(TrackLineStyle).GetProperties(),p=>p.Name.Contains("Color"));
    }
    [Fact]public void EndpointBuilderUsesWholeTrackEndpointsAndCombinesClosedRoute()
    {
        var p=new HikingProject();var t=new Track{Segments=[new(){Points=[new(){Latitude=23,Longitude=113},new(){Latitude=23.01,Longitude=113}]},new(){Points=[new(){Latitude=23.02,Longitude=113},new(){Latitude=23.03,Longitude=113}]}]};p.AddTrack(t,false);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(TrackEndpointGeoJsonBuilder.Build(p,new HashSet<Guid>{t.Id})));var f=json.RootElement.GetProperty("features");Assert.Equal(2,f.GetArrayLength());Assert.Equal(23.03,f[1].GetProperty("geometry").GetProperty("coordinates")[1].GetDouble());
        t.Segments[^1].Points[^1].Latitude=23;
        using var closed=JsonDocument.Parse(JsonSerializer.Serialize(TrackEndpointGeoJsonBuilder.Build(p,new HashSet<Guid>{t.Id})));Assert.Equal("both",Assert.Single(closed.RootElement.GetProperty("features").EnumerateArray()).GetProperty("properties").GetProperty("endpoint").GetString());
        using var hidden=JsonDocument.Parse(JsonSerializer.Serialize(TrackEndpointGeoJsonBuilder.Build(p,new HashSet<Guid>())));Assert.Empty(hidden.RootElement.GetProperty("features").EnumerateArray());
    }
}
