using HikingGuide.Core.Models;
namespace HikingGuide.Application.Maps;

/// <summary>从原始整轨首尾取点，独立于地图分块和抽稀，避免每块出现假起终点。</summary>
public static class TrackEndpointGeoJsonBuilder
{
    public static object Build(HikingProject project,IReadOnlySet<Guid> visibleTrackIds)
    {
        var features=new List<object>();
        foreach(var track in project.Tracks.Where(t=>visibleTrackIds.Contains(t.Id)))
        {
            if(track.StartPoint is not{} start||track.EndPoint is not{} end)continue;
            var first=track.Segments.First(s=>!s.IsEmpty);var last=track.Segments.Last(s=>!s.IsEmpty);
            var combined=start.Coordinate==end.Coordinate;
            void Add(TrackPoint point,string endpoint,TrackSegment part)
            {
                if(!MapLayerVisibility.IsSplitSectionVisible(project.Settings.Map,part.SplitSectionId??part.Id))return;
                features.Add(new {type="Feature",geometry=new {type="Point",coordinates=new[]{point.Longitude,point.Latitude}},
                    properties=new {trackId=track.Id,isActive=track.Id==project.MainGuideTrackId,color=track.DisplayColor??"#2877C7",endpoint}});
            }
            Add(start,combined?"both":"start",first);
            if(!combined)Add(end,"end",last);
        }
        return new {type="FeatureCollection",features};
    }
}
