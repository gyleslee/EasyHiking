using HikingGuide.Core.Models;

namespace HikingGuide.Core.Services;

public static class PrimaryTrackSelection
{
    public static Guid? Resolve(HikingProject project, object? model)
    {
        var id = model switch
        {
            Track track => track.Id, SplitSection section => section.TrackId,
            GuideNode node => node.TrackId, Waypoint point => point.AssociatedTrackId,
            GuideSegment segment => segment.TrackId,
            TrackSegment part => project.Tracks.FirstOrDefault(t => t.Segments.Contains(part))?.Id,
            _ => null
        };
        return id is Guid value && project.Tracks.Any(t => t.Id == value) ? value : null;
    }
}
