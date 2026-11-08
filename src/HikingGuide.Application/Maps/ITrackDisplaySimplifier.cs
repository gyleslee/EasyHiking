using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public interface ITrackDisplaySimplifier
{
    MapTrackFeature Simplify(Track track, bool isActive, double zoom, CancellationToken cancellationToken = default);
    void Invalidate(Guid trackId);
}
