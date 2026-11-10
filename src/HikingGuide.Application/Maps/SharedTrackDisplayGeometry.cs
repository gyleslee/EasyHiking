using System.Runtime.CompilerServices;
using System.Text.Json;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

/// <summary>攻略切点、物理断点先固定，再生成一份公共显示骨架；不修改计算轨迹。</summary>
public sealed class SharedTrackDisplayGeometry
{
    private readonly ConditionalWeakTable<Track, Dictionary<string, MapTrackFeature>> _cache = new();
    public void Invalidate(Guid id){foreach(var pair in _cache)if(pair.Key.Id==id)_cache.Remove(pair.Key);}
    public MapTrackFeature Build(HikingProject project, Track track, double zoom, CancellationToken token)
    {
        var ranges=project.GuideSegments.Where(g=>g.TrackId==track.Id).Select(g=>(g.Id,
            Start:project.GuideNodes.FirstOrDefault(n=>n.Id==g.StartNodeId),
            End:project.GuideNodes.FirstOrDefault(n=>n.Id==g.EndNodeId)))
            .Where(g=>g.Start is {IsOrphaned:false,ExactPosition:not null}&&g.End is {IsOrphaned:false,ExactPosition:not null})
            .Select(g=>(g.Id,Start:g.Start!.ExactPosition!,End:g.End!.ExactPosition!)).ToArray();
        var key=track.GeometryRevision+"/"+MapDisplayProfile.Default.GetCacheZoomBucket(zoom)+"/"+JsonSerializer.Serialize(ranges.Select(r=>new{r.Id,r.Start,r.End}));
        var cache=_cache.GetOrCreateValue(track);
        lock(cache)if(cache.TryGetValue(key,out var saved))return saved with{Name=track.Name??"未命名轨迹",DisplayColor=track.DisplayColor,IsActive=track.Id==project.MainGuideTrackId};
        var parts=new List<IReadOnlyList<MapCoordinate>>();var pieces=new List<MapRenderPiece>();
        static double At(TrackPosition p)=>p.StartPointIndex+(p.EndPointIndex-p.StartPointIndex)*p.Fraction;
        for(var si=0;si<track.Segments.Count;si++)
        {
            token.ThrowIfCancellationRequested();var part=track.Segments[si];var source=part.Points;
            if(source.Count<2){parts.Add(source.Select(p=>new MapCoordinate(p.Longitude,p.Latitude)).ToArray());continue;}
            var cuts=new SortedDictionary<double,MapCoordinate>{{0,new(source[0].Longitude,source[0].Latitude)},{source.Count-1,new(source[^1].Longitude,source[^1].Latitude)}};
            foreach(var p in ranges.SelectMany(r=>new[]{r.Start,r.End}).Where(p=>p.TrackId==track.Id&&p.SegmentIndex==si))
                if(At(p)>=0&&At(p)<=source.Count-1)cuts[At(p)]=new(p.SnappedCoordinate.Longitude,p.SnappedCoordinate.Latitude);
            var ordered=cuts.ToArray();var merged=new List<MapCoordinate>();
            for(var ci=1;ci<ordered.Length;ci++)
            {
                var a=ordered[ci-1];var b=ordered[ci];var raw=new List<MapCoordinate>{a.Value};
                for(var pi=(int)Math.Floor(a.Key)+1;pi<b.Key;pi++){token.ThrowIfCancellationRequested();raw.Add(new(source[pi].Longitude,source[pi].Latitude));}
                raw.Add(b.Value);
                var line=DouglasPeuckerTrackDisplaySimplifier.SimplifySegment(raw,MapDisplayProfile.Default.GetToleranceMeters(zoom),token);
                if(merged.Count==0)merged.AddRange(line);else merged.AddRange(line.Skip(1));
                var mid=(a.Key+b.Key)/2;
                var guide=ranges.FirstOrDefault(r=>(si>r.Start.SegmentIndex||si==r.Start.SegmentIndex&&mid>=At(r.Start))&&(si<r.End.SegmentIndex||si==r.End.SegmentIndex&&mid<=At(r.End)));
                for(var start=0;start<line.Count-1;start+=511)
                    pieces.Add(new(part.SplitSectionId??part.Id,guide.Id==Guid.Empty?null:guide.Id,line.Skip(start).Take(512).ToArray()));
            }
            parts.Add(merged);
        }
        var result=new MapTrackFeature(track.Id,track.Name??"未命名轨迹",track.Id==project.MainGuideTrackId,parts,track.GetPointCount(),parts.Sum(p=>p.Count),track.DisplayColor){GeometryRevision=track.GeometryRevision,RenderPieces=pieces};
        lock(cache){if(cache.Count>=10)cache.Clear();cache[key]=result;}return result;
    }
}
