using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;

namespace HikingGuide.Infrastructure.Elevation;

public sealed class TerrainUnavailableException(string message) : Exception(message);
public sealed record TerrainDemChoice(Guid Id,string Name,string Summary,int MaxNativeZoom);

/// <summary>复用P8索引、确定性优先级和只读句柄缓存；不重新扫描目录，不更改任何业务高程。</summary>
public sealed class TerrainTileSampler(DemDatabaseCatalog catalog,DemElevationProvider provider)
{
    public TerrainDemChoice? Describe(Guid id)
    {
        var db=catalog.Databases.FirstOrDefault(d=>d.Id==id);
        var entries=catalog.GetEntries(id);
        if(db is not {Status:DemDatabaseStatus.Ready}||!Directory.Exists(db.RootPath)||entries.Count==0)return null;
        var resolutions=entries.Select(e=>e.ResolutionMeters).Where(r=>double.IsFinite(r)&&r>0).ToArray();
        if(resolutions.Length==0)return null;
        var latitude=entries.Max(e=>Math.Max(Math.Abs(e.Wgs84Coverage.MinLatitude),Math.Abs(e.Wgs84Coverage.MaxLatitude)));
        var native=TerrainRgb.MaxNativeZoom(resolutions.Max(),latitude);
        var formats=string.Join("/",entries.Select(e=>e.Format==DemRasterFormat.Hgt?"HGT":"TIF").Distinct());
        return new(id,db.Name,$"{db.Name}｜{formats}｜{resolutions.Min():0.#}—{resolutions.Max():0.#}米｜原生最高Z{native}",native);
    }
    public async Task<IReadOnlyList<TerrainDemChoice>> AvailableAsync(CancellationToken token=default)=>await Task.Run(()=>
    {
        var result=new List<TerrainDemChoice>();
        foreach(var db in catalog.Databases)
        {
            token.ThrowIfCancellationRequested();var choice=Describe(db.Id);if(choice is null)continue;
            // 至少一个实际像元可采样；名称/有效索引不等于可读高程。
            var valid=catalog.GetEntries(db.Id).Take(16).Any(entry=>
            {
                var b=entry.Wgs84Coverage;
                return provider.SampleDetailed(db.Id,new GeoCoordinate((b.MinLatitude+b.MaxLatitude)/2,(b.MinLongitude+b.MaxLongitude)/2),token).Status==DemSampleStatus.Valid;
            });
            if(valid)result.Add(choice);
        }
        return (IReadOnlyList<TerrainDemChoice>)result;
    },token);
    public byte[] Sample(Guid id,int z,int x,int y,CancellationToken token=default,Action? checkViewport=null)
    {
        var choice=Describe(id)??throw new TerrainUnavailableException("当前3D地形DEM未连接，地图已切回2D。");
        if(z<0||z>choice.MaxNativeZoom||x<0||y<0||x>=(1<<z)||y>=(1<<z))throw new ArgumentOutOfRangeException(nameof(z),"地形瓦片参数无效。");
        var result=new byte[TerrainRgb.TileSize*TerrainRgb.TileSize*4];
        var longitudes=Enumerable.Range(0,TerrainRgb.TileSize).Select(col=>TerrainRgb.Coordinate(z,x,y,col+.5,.5).Longitude).ToArray();
        var coordinates=new GeoCoordinate[TerrainRgb.TileSize];
        for(var row=0;row<TerrainRgb.TileSize;row++)
        {
            token.ThrowIfCancellationRequested();checkViewport?.Invoke();
            var latitude=TerrainRgb.Coordinate(z,x,y,.5,row+.5).Latitude;
            for(var col=0;col<coordinates.Length;col++)coordinates[col]=new(latitude,longitudes[col]);
            var samples=provider.SampleDenseRow(id,coordinates,token);
            for(var col=0;col<TerrainRgb.TileSize;col++)
            {
                var p=coordinates[col];var sample=samples[col];
                if(sample.Status!=DemSampleStatus.Valid||sample.ElevationMeters is not {} h)
                    throw new TerrainUnavailableException($"当前视野的3D地形DEM存在缺测或未覆盖区域，地图已切回2D；未补0或改用其他DEM。瓦片Z{z}/{x}/{y}，缺测位置{p.Latitude:0.####},{p.Longitude:0.####}。");
                var (r,g,b)=TerrainRgb.Encode(h);var offset=(row*TerrainRgb.TileSize+col)*4;
                result[offset]=r;result[offset+1]=g;result[offset+2]=b;result[offset+3]=255;
            }
        }
        return result;
    }
}
