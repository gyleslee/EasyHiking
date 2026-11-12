using System.Runtime.CompilerServices;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
namespace HikingGuide.Application.Maps;
public sealed class KilometerGeoJsonBuilder
{
    private sealed record Entry(long Revision,int Count,string? Directions,IReadOnlyList<TrackKilometerMark> Marks);
    private sealed class Holder { public Entry? Value; }
    private readonly ConditionalWeakTable<Track,Holder> _cache=new();
    public object Build(HikingProject project,CancellationToken token=default)
    {
        var features=new List<object>();
        foreach(var track in project.Tracks.Where(t=>TrackKilometerService.IsEnabled(t)&&MapLayerVisibility.IsTrackVisible(project.Settings.Map,t.Id)))
        {
            token.ThrowIfCancellationRequested();var count=track.GetPointCount();
            var holder=_cache.GetValue(track,_=>new Holder());Entry entry;lock(holder){entry=holder.Value!;if(entry is null||entry.Revision!=track.GeometryRevision||entry.Count!=count||entry.Directions!=(track.Metadata.GetValueOrDefault(TrackKilometerService.RouteKindKey)+"|"+track.Metadata.GetValueOrDefault(KilometerRouteDirections.Key)))holder.Value=entry=new(track.GeometryRevision,count,(track.Metadata.GetValueOrDefault(TrackKilometerService.RouteKindKey)+"|"+track.Metadata.GetValueOrDefault(KilometerRouteDirections.Key)),new TrackKilometerService().Generate(track,token));}
            foreach(var mark in entry.Marks)features.Add(new{type="Feature",geometry=new{type="Point",coordinates=new[]{mark.Coordinate.Longitude,mark.Coordinate.Latitude}},properties=new{trackId=track.Id,label=(mark.DistanceMeters/1000).ToString(mark.IsEndpoint?"0.0":"0",System.Globalization.CultureInfo.InvariantCulture),distanceMeters=mark.DistanceMeters,endpoint=mark.IsEndpoint}});
        }
        return new{type="FeatureCollection",features};
    }
}
