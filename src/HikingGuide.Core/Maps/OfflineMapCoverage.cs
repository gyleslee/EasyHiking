using NetTopologySuite.Geometries;
using NtsGeometry=NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Union;

namespace HikingGuide.Core.Maps;

public sealed record OfflineMapPoint(double Longitude,double Latitude);
public sealed record OfflineMapRegion(List<OfflineMapPoint> Points);
public readonly record struct OfflineTileAddress(int Zoom,int X,int Y)
{
    public long TileId=>OfflineMapCoverage.TileId(Zoom,X,Y);
}
public sealed record OfflineCoverageCount(Dictionary<int,long> Levels)
{
    public long Total=>Levels.Values.Sum();
    public bool Exact {get;init;}=true;
}
/// <summary>WGS84多边形与完整XYZ瓦片相交；地图显示几何和项目轨迹均不参与此计算。</summary>
public static class OfflineMapCoverage
{
    public const long MaximumDownloadTiles=2_000_000;
    private static readonly GeometryFactory Factory=new();
    public static NtsGeometry CreateGeometry(OfflineMapRegion region)
    {
        if(region?.Points is not {Count:>=3 and <=1000} points)throw new ArgumentException("下载范围至少需要3个点，最多1000个点。");
        var coordinates=new List<Coordinate>();double? last=null;
        foreach(var point in points)
        {
            if(!double.IsFinite(point.Longitude)||!double.IsFinite(point.Latitude)||Math.Abs(point.Longitude)>180||Math.Abs(point.Latitude)>85.05112878)throw new ArgumentException("选区经纬度超出地图支持范围。");
            var x=(point.Longitude+180)/360;if(last.HasValue)x+=Math.Round(last.Value-x);last=x;
            var lat=point.Latitude*Math.PI/180;var y=(1-Math.Asinh(Math.Tan(lat))/Math.PI)/2;
            if(coordinates.Count==0||!coordinates[^1].Equals2D(new(x,y)))coordinates.Add(new(x,y));
        }
        if(!coordinates[0].Equals2D(coordinates[^1]))coordinates.Add(coordinates[0].Copy());
        if(coordinates.Count<4)throw new ArgumentException("下载范围至少需要3个不同的点。");
        var polygon=Factory.CreatePolygon(coordinates.ToArray());
        if(!polygon.IsValid||polygon.Area<=0)throw new ArgumentException("下载范围不能交叉、重叠或退化成一条线，请重新选区。");
        if(polygon.EnvelopeInternal.Width>.5+1e-9)throw new ArgumentException("单次选区横跨经度不能超过180度，请分区下载。");
        var pieces=new List<NtsGeometry>();
        for(var world=(int)Math.Floor(polygon.EnvelopeInternal.MinX);world<=(int)Math.Floor(polygon.EnvelopeInternal.MaxX);world++)
        {var part=polygon.Intersection(Factory.ToGeometry(new Envelope(world,world+1,0,1)));if(part.Area>0)pieces.Add(AffineTransformation.TranslationInstance(-world,0).Transform(part));}
        return UnaryUnionOp.Union(pieces);
    }
    public static OfflineCoverageCount Count(OfflineMapRegion region,int minZoom,int maxZoom,CancellationToken token=default)
    {
        ValidateZoom(minZoom,maxZoom);var shape=CreateGeometry(region);var prepared=PreparedGeometryFactory.Prepare(shape);var levels=new Dictionary<int,long>();
        var exact=true;
        for(var z=minZoom;z<=maxZoom;z++)
        {token.ThrowIfCancellationRequested();var lower=shape.Area*Math.Pow(4,z);if(lower>MaximumDownloadTiles){levels[z]=(long)Math.Ceiling(lower);exact=false;}else levels[z]=CountNode(0,0,0,z);}
        return new(levels){Exact=exact};
        long CountNode(int depth,int x,int y,int target)
        {
            token.ThrowIfCancellationRequested();var box=Box(depth,x,y);
            if(!prepared.Intersects(box))return 0;
            if(prepared.Covers(box))return 1L<<(2*(target-depth));
            if(depth==target)return shape.Intersection(box).Area>0?1:0;
            long count=0;for(var dy=0;dy<2;dy++)for(var dx=0;dx<2;dx++){count+=CountNode(depth+1,x*2+dx,y*2+dy,target);if(count>MaximumDownloadTiles){exact=false;return count;}}return count;
        }
    }
    public static IEnumerable<OfflineTileAddress> Enumerate(OfflineMapRegion region,int minZoom,int maxZoom,CancellationToken token=default)
    {
        ValidateZoom(minZoom,maxZoom);var shape=CreateGeometry(region);var prepared=PreparedGeometryFactory.Prepare(shape);
        for(var z=minZoom;z<=maxZoom;z++)foreach(var address in Walk(0,0,0,z,false))yield return address;
        IEnumerable<OfflineTileAddress> Walk(int depth,int x,int y,int target,bool covered)
        {
            token.ThrowIfCancellationRequested();
            if(!covered){var box=Box(depth,x,y);if(!prepared.Intersects(box))yield break;covered=prepared.Covers(box);if(depth==target&&!covered&&shape.Intersection(box).Area<=0)yield break;}
            if(depth==target){yield return new(depth,x,y);yield break;}
            for(var dy=0;dy<2;dy++)for(var dx=0;dx<2;dx++)foreach(var address in Walk(depth+1,x*2+dx,y*2+dy,target,covered))yield return address;
        }
    }
    private static NtsGeometry Box(int z,int x,int y){var n=Math.Pow(2,z);return Factory.ToGeometry(new Envelope(x/n,(x+1)/n,y/n,(y+1)/n));}
    private static void ValidateZoom(int min,int max){if(min<0||max>24||min>max)throw new ArgumentException("下载层级应在0至24之间，最小层级不能大于最大层级。");}
    public static long TileId(int z,int x,int y)
    {
        ValidateZoom(z,z);var n=1<<z;if(x<0||y<0||x>=n||y>=n)throw new ArgumentException("瓦片坐标无效。");
        long d=0;var xx=x;var yy=y;
        for(var s=n/2;s>0;s/=2){var rx=(xx&s)>0?1:0;var ry=(yy&s)>0?1:0;d+=(long)s*s*((3*rx)^ry);if(ry==0){if(rx==1){xx=s-1-xx;yy=s-1-yy;}(xx,yy)=(yy,xx);}}
        return ((1L<<(2*z))-1)/3+d;
    }
}

