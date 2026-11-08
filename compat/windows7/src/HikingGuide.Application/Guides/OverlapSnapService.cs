using HikingGuide.Core.Models;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.Guides;

public sealed record OverlapSnapOptions(double ExtraDistanceMeters=10, double MileageClusterMeters=30);
public sealed class OverlapSnapService
{
    public IReadOnlyList<TrackPosition> Find(Track track,GeoCoordinate coordinate,OverlapSnapOptions? options=null,CancellationToken token=default)
    {
        options ??= new();
        if(!double.IsFinite(options.ExtraDistanceMeters) || options.ExtraDistanceMeters<0 || !double.IsFinite(options.MileageClusterMeters) || options.MileageClusterMeters<=0) throw new InvalidOperationException("吸附候选参数无效。");
        token.ThrowIfCancellationRequested(); var snap=new TrackSnapService(); var nearest=snap.Snap(track,coordinate).Position;
        if(nearest is null) throw new InvalidOperationException("轨迹没有可吸附线段。");
        var candidates=snap.SnapAllWithinTolerance(track,coordinate,nearest.DistanceToTrack+options.ExtraDistanceMeters,options.ExtraDistanceMeters);
        var result=new List<TrackPosition>(); TrackPosition? best=null; double previous=double.NegativeInfinity;
        foreach(var p in candidates)
        {
            token.ThrowIfCancellationRequested();
            if(best is not null && p.DistanceFromTrackStart-previous>options.MileageClusterMeters) {result.Add(best); best=null;}
            if(best is null || p.DistanceToTrack<best.DistanceToTrack)best=p;
            previous=p.DistanceFromTrackStart;
        }
        if(best is not null)result.Add(best); return result;
    }
}
