using System.Runtime.CompilerServices;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
namespace HikingGuide.Core.Services;

public sealed record KilometerMarkProfile(double MaximumUnconfirmedGapMeters=1000,bool IncludeFinalEndpoint=true);
public sealed record KilometerTrackEligibility(bool Eligible,string Reason,double MaximumGapMeters=0);
public sealed record TrackKilometerMark(double DistanceMeters,GeoCoordinate Coordinate,bool IsEndpoint=false);
/// <summary>仅派生地图公里点，不修改轨迹、补连接线或更改统计定义。</summary>
public sealed class TrackKilometerService(KilometerMarkProfile? profile=null)
{
    public const string VisibilityKey="hg:kilometerMarks";
    public const string RouteKindKey="hg:kilometerRouteKind";
    private readonly KilometerMarkProfile _profile=profile??new();
    public static bool IsEnabled(Track track)=>track.Metadata.GetValueOrDefault(VisibilityKey)=="1";
    public static void SetEnabled(Track track,bool enabled){if(enabled)track.Metadata[VisibilityKey]="1";else track.Metadata.Remove(VisibilityKey);}
    private sealed record AssessmentKey(long Revision,int Shape,double Limit,string? Kind,bool IgnoreKind);
    private sealed class AssessmentCache { public AssessmentKey? Key;public KilometerTrackEligibility? Result; }
    private static readonly ConditionalWeakTable<Track,AssessmentCache> Assessments=new();
    public KilometerTrackEligibility Assess(Track track,bool ignoreKind=false)
    {
        var hash=new HashCode();foreach(var part in track.Segments){hash.Add(part.Id);hash.Add(part.Points.Count);hash.Add(part.StartPoint?.Coordinate);hash.Add(part.EndPoint?.Coordinate);}hash.Add(track.Metadata.GetValueOrDefault(KilometerRouteDirections.Key));
        var key=new AssessmentKey(track.GeometryRevision,hash.ToHashCode(),_profile.MaximumUnconfirmedGapMeters,track.Metadata.GetValueOrDefault(RouteKindKey),ignoreKind);var cache=Assessments.GetValue(track,_=>new());
        lock(cache){if(cache.Key==key&&cache.Result is {} result)return result;cache.Result=AssessGeometry(track,ignoreKind);cache.Key=key;return cache.Result;}
    }
    private KilometerTrackEligibility AssessGeometry(Track track,bool ignoreKind)
    {
        if(!ignoreKind&&track.Metadata.GetValueOrDefault(RouteKindKey)=="collection")return new(false,"独立路线集合不显示公里标识");
        var parts=track.Segments.Where(s=>s.Points.Count>=2).ToArray();
        if(parts.Length==0)return new(false,"没有可计程的轨迹线段");
        bool distinct=false;foreach(var s in parts)for(var i=0;i<s.Points.Count;i++){var p=s.Points[i];if(!double.IsFinite(p.Latitude)||!double.IsFinite(p.Longitude)||Math.Abs(p.Latitude)>90||Math.Abs(p.Longitude)>180)return new(false,"轨迹坐标无效");if(i>0&&p.Coordinate!=s.Points[i-1].Coordinate)distinct=true;}
        if(!distinct)return new(false,"轨迹长度为零");
        var byId=parts.ToDictionary(s=>s.Id);var route=KilometerRouteDirections.Read(track)??KilometerRouteDirections.Suggest(track);
        var max=0d;for(var i=1;i<route.Count;i++){var a=byId[route[i-1].SegmentId];var b=byId[route[i].SegmentId];var gap=GeoDistanceCalculator.Instance.CalculateDistance((route[i-1].Reversed?a.StartPoint!:a.EndPoint!).Coordinate,(route[i].Reversed?b.EndPoint!:b.StartPoint!).Coordinate);max=Math.Max(max,gap);if(track.Metadata.GetValueOrDefault(RouteKindKey)!="route"&&gap>_profile.MaximumUnconfirmedGapMeters)return new(false,"包含大断口；确认是一条行进路线后可设置",max);}
        return new(true,"按原始顺序累计轨迹，小间隙按直线计入",max);
    }
    public static bool RequiresKindConfirmation(Track track)=>track.Metadata.GetValueOrDefault(RouteKindKey) is not ("route" or "collection")&&track.Segments.Where(s=>s.Points.Count>=2).Zip(track.Segments.Where(s=>s.Points.Count>=2).Skip(1),(a,b)=>GeoDistanceCalculator.Instance.CalculateDistance(a.EndPoint!.Coordinate,b.StartPoint!.Coordinate)).Any(g=>g>.001);
    public IReadOnlyList<TrackKilometerMark> Generate(Track track,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(!Assess(track).Eligible)return [];
        if(KilometerRouteDirections.NeedsConfirmation(track))return [];
        var points=KilometerRouteDirections.Coordinates(track).ToArray();var marks=new List<TrackKilometerMark>();
        double cumulative=0,next=1000;
        for(var i=1;i<points.Length;i++)
        {
            token.ThrowIfCancellationRequested();var a=points[i-1];var b=points[i];var length=GeoDistanceCalculator.Instance.CalculateDistance(a,b);if(length<=0)continue;
            while(next<=cumulative+length+1e-7){var t=Math.Clamp((next-cumulative)/length,0,1);var delta=((b.Longitude-a.Longitude+540)%360)-180;var lon=((a.Longitude+delta*t+540)%360)-180;marks.Add(new(next,new(a.Latitude+(b.Latitude-a.Latitude)*t,lon)));next+=1000;}
            cumulative+=length;
        }
        if(_profile.IncludeFinalEndpoint&&cumulative>0){if(marks.Count>0&&Math.Abs(marks[^1].DistanceMeters-cumulative)<.001)marks[^1]=new(cumulative,points[^1],true);else marks.Add(new(cumulative,points[^1],true));}
        return marks;
    }
}
