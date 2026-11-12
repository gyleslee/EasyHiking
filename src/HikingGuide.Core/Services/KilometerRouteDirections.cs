using System.Text.Json;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
namespace HikingGuide.Core.Services;

/// <summary>公里标识专用的虚拟行进顺序；不反转、合并或改写原始轨迹。</summary>
public static class KilometerRouteDirections
{
    public const string Key="hg:kilometerDirections";
    public sealed record Part(Guid SegmentId,bool Reversed);
    private sealed record Configuration(long Revision,Part[] Parts);
    public static IReadOnlyList<Part>? Read(Track track)
    {
        try
        {
            if(!track.Metadata.TryGetValue(Key,out var value)||value is null)return null;
            var config=JsonSerializer.Deserialize<Configuration>(value);if(config is null||config.Revision!=track.GeometryRevision)return null;var parts=config.Parts;var ids=track.Segments.Where(s=>s.Points.Count>=2).Select(s=>s.Id).ToHashSet();
            return parts is not null&&parts.All(p=>p is not null)&&parts.Length==ids.Count&&parts.Select(p=>p.SegmentId).Distinct().Count()==ids.Count&&parts.All(p=>ids.Contains(p.SegmentId))?parts:null;
        }
        catch(JsonException){return null;}
    }
    public static string Serialize(Track track,IReadOnlyList<Part> parts)=>JsonSerializer.Serialize(new Configuration(track.GeometryRevision,parts.ToArray()));
    public static void Set(Track track,IReadOnlyList<Part> parts)=>track.Metadata[Key]=Serialize(track,parts);
    public static IReadOnlyList<Part> Natural(Track track)=>track.Segments.Where(s=>s.Points.Count>=2).Select(s=>new Part(s.Id,false)).ToArray();
    public static IReadOnlyList<Part> Reverse(IReadOnlyList<Part> parts)=>parts.Reverse().Select(p=>p with{Reversed=!p.Reversed}).ToArray();
    public static IReadOnlyList<Part> Suggest(Track track)
    {
        var segments=track.Segments.Where(s=>s.Points.Count>=2).ToArray();var n=segments.Length;if(n==0)return [];
        var costs=new double[n,2];var previous=new int[n,2];
        for(var i=1;i<n;i++)for(var b=0;b<2;b++)
        {
            costs[i,b]=double.PositiveInfinity;
            for(var a=0;a<2;a++)
            {
                var end=a==0?segments[i-1].EndPoint!:segments[i-1].StartPoint!;var start=b==0?segments[i].StartPoint!:segments[i].EndPoint!;
                var cost=costs[i-1,a]+GeoDistanceCalculator.Instance.CalculateDistance(end.Coordinate,start.Coordinate);
                if(cost<costs[i,b]){costs[i,b]=cost;previous[i,b]=a;}
            }
        }
        var choice=costs[n-1,0]<=costs[n-1,1]?0:1;var result=new Part[n];for(var i=n-1;i>=0;i--){result[i]=new(segments[i].Id,choice==1);choice=previous[i,choice];}return result;
    }
    public static bool NeedsConfirmation(Track track)
    {
        if(Read(track) is not null)return false;var natural=Natural(track);if(natural.Count<2)return false;
        return GapDistance(track,natural)>GapDistance(track,Suggest(track))+1;
    }
    public static double GapDistance(Track track,IReadOnlyList<Part> parts)
    {
        var byId=track.Segments.ToDictionary(s=>s.Id);double total=0;
        for(var i=1;i<parts.Count;i++){var a=byId[parts[i-1].SegmentId];var b=byId[parts[i].SegmentId];total+=GeoDistanceCalculator.Instance.CalculateDistance((parts[i-1].Reversed?a.StartPoint!:a.EndPoint!).Coordinate,(parts[i].Reversed?b.EndPoint!:b.StartPoint!).Coordinate);}return total;
    }
    public static IEnumerable<GeoCoordinate> Coordinates(Track track)
    {
        var byId=track.Segments.Where(s=>s.Points.Count>=2).ToDictionary(s=>s.Id);
        foreach(var part in Read(track)??Natural(track))foreach(var point in part.Reversed?byId[part.SegmentId].Points.AsEnumerable().Reverse():byId[part.SegmentId].Points)yield return point.Coordinate;
    }
}
