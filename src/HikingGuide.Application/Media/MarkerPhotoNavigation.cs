using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;
namespace HikingGuide.Application.Media;

/// <summary>只计算同轨迹有图片标注的浏览顺序，不修改吸附、归属或原始点。</summary>
public sealed class MarkerPhotoNavigation
{
    public static Guid? TrackId(MapMarkerBase marker)=>marker switch{Waypoint w=>w.AssociatedTrackId,GuideNode n=>n.TrackId,_=>null};
    public IReadOnlyList<MapMarkerBase> Build(HikingProject project,MapMarkerBase selected)
    {
        var owners=project.MediaAttachments.Select(a=>a.OwnerId).Concat(project.ExternalMediaReferences.Select(a=>a.OwnerId)).ToHashSet();
        var id=TrackId(selected);var track=project.Tracks.FirstOrDefault(t=>t.Id==id);
        if(track is null||!track.Segments.Any(s=>s.Points.Count>=2))return owners.Contains(selected.Id)?[selected]:[];
        var service=new GuideStructureService();
        double Position(MapMarkerBase marker)=>marker switch{
            Waypoint w=>service.SnapNodeToPosition(track,w.Coordinate,w.PreferredTrackPosition).DistanceFromTrackStart,
            GuideNode n=>service.SnapNodeToPosition(track,n.SnappedCoordinate??n.OriginalCoordinate,n.PreferredTrackPosition??n.ExactPosition).DistanceFromTrackStart,
            _=>double.PositiveInfinity};
        return project.Waypoints.Cast<MapMarkerBase>().Concat(project.GuideNodes).Where(m=>owners.Contains(m.Id)&&TrackId(m)==id).OrderBy(Position).ToArray();
    }
}
