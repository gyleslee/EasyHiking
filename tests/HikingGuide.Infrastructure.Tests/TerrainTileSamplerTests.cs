using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.Maps;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace HikingGuide.Infrastructure.Tests;
public sealed class TerrainTileSamplerTests:IDisposable
{
    private readonly string _root=Path.Combine(@"F:\Codex\Temp","HikingGuide-P102-"+Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ProviderAndRgbAgreeAcrossTifOrHgtOneDegreeBoundaries(bool hgt)
    {
        DemGdalRuntime.EnsureReady();var source=Path.Combine(_root,"dem");Directory.CreateDirectory(source);
        for(var lat=22;lat<=23;lat++)for(var lon=113;lon<=114;lon++)Create(source,lat,lon,hgt);
        var originals=Directory.GetFiles(source).ToDictionary(p=>p,p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
        var catalog=new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root,"data")));
        var mounted=await catalog.MountAsync("地形一致性",source,unitMetersConfirmed:true);
        using var provider=new DemElevationProvider(catalog);
        using var cachedProvider=new DemElevationProvider(catalog,rasterWindowCacheCapacity:2);
        var sampler=new TerrainTileSampler(catalog,cachedProvider);
        Assert.Contains(await sampler.AvailableAsync(),c=>c.Id==mounted.Database.Id);
        var first=sampler.Sample(mounted.Database.Id,9,417,222);var second=sampler.Sample(mounted.Database.Id,9,418,222);
        foreach(var row in new[]{0,128,256,400,511})foreach(var col in new[]{0,128,256,511})
        {
            var coordinate=TerrainRgb.Coordinate(9,417,222,col+.5,row+.5);var expected=provider.SampleDetailed(mounted.Database.Id,new GeoCoordinate(coordinate.Latitude,coordinate.Longitude)).ElevationMeters!.Value;
            var i=(row*512+col)*4;Assert.Equal(255,first[i+3]);Assert.InRange(Math.Abs(TerrainRgb.Decode(first[i],first[i+1],first[i+2])-expected),0,.10001);
            var a=(row*512+511)*4;var b=row*512*4;
            Assert.InRange(Math.Abs(TerrainRgb.Decode(first[a],first[a+1],first[a+2])-TerrainRgb.Decode(second[b],second[b+1],second[b+2])),0,1.1);
        }
        Assert.Throws<TerrainUnavailableException>(()=>sampler.Sample(mounted.Database.Id,9,0,0));
        using var stop=new CancellationTokenSource();stop.Cancel();Assert.Throws<OperationCanceledException>(()=>sampler.Sample(mounted.Database.Id,9,417,222,stop.Token));
        foreach(var (path,hash) in originals)Assert.Equal(hash,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
    }
    [Fact]
    public async Task DenseWindowCachePreservesBilinearAndNoDataAcrossWindowEdges()
    {
        DemGdalRuntime.EnsureReady();var source=Path.Combine(_root,"nodata");Directory.CreateDirectory(source);Create(source,22,113,false);
        using(var edit=Gdal.Open(Path.Combine(source,"N22E113.tif"),Access.GA_Update))
        {using var band=edit.GetRasterBand(1);band.SetNoDataValue(-32768);band.WriteRaster(129,129,1,1,new double[]{-32768},1,1,0,0);edit.FlushCache();}
        var catalog=new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root,"data")));
        var mounted=await catalog.MountAsync("窗口边界",source,unitMetersConfirmed:true);
        using var plain=new DemElevationProvider(catalog);using var dense=new DemElevationProvider(catalog,rasterWindowCacheCapacity:1);
        foreach(var row in new[]{0d,127d,127.7,128d,128.3,129d,130d,255d,256d,1199.9,1200d})
        foreach(var col in new[]{0d,127d,127.7,128d,128.3,129d,130d,255d,256d,1199.9,1200d})
        {
            var coordinate=new GeoCoordinate(23-row/1200,113+col/1200);
            var a=plain.SampleDetailed(mounted.Database.Id,coordinate);var b=dense.SampleDetailed(mounted.Database.Id,coordinate);
            Assert.Equal(a.Status,b.Status);Assert.Equal(a.ElevationMeters,b.ElevationMeters);
        }
        Assert.Equal(DemSampleStatus.NoData,dense.SampleDetailed(mounted.Database.Id,new(23-129/1200d,113+129/1200d)).Status);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task DenseRowsMatchPointSamplingIncludingNoDataSeamsAndOverlaps(bool hgt)
    {
        DemGdalRuntime.EnsureReady();var source=Path.Combine(_root,"dense");Directory.CreateDirectory(source);
        Create(source,22,113,hgt);Create(source,22,114,hgt);
        var path=Path.Combine(source,"N22E113."+(hgt?"hgt":"tif"));
        if(hgt)
        {using var stream=File.Open(path,FileMode.Open,FileAccess.Write);stream.Position=(129*1201L+129)*2;stream.Write(new byte[]{128,0});}
        else
        {
            var overlap=Path.Combine(source,"A-overlap.tif");File.Copy(path,overlap);
            using var edit=Gdal.Open(overlap,Access.GA_Update);using var band=edit.GetRasterBand(1);
            band.SetNoDataValue(-32768);band.WriteRaster(129,129,1,1,new double[]{-32768},1,1,0,0);edit.FlushCache();
        }
        var catalog=new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root,"data")));
        var mount=await catalog.MountAsync("密集一致性",source,unitMetersConfirmed:true);
        using var plain=new DemElevationProvider(catalog);using var dense=new DemElevationProvider(catalog,rasterWindowCacheCapacity:1);
        var coordinates=(from row in new[]{0d,127d,127.7,128d,128.3,129d,130d,255d,256d,1199.9,1200d}
                         from col in new[]{0d,127d,127.7,128d,128.3,129d,130d,255d,256d,1199.9,1200d,1201d,2399d}
                         select new GeoCoordinate(23-row/1200,113+col/1200)).Append(new(20,110)).ToArray();
        var expected=coordinates.Select(c=>plain.SampleDetailed(mount.Database.Id,c)).ToArray();
        Assert.Equal(expected,dense.SampleDenseRow(mount.Database.Id,coordinates));
        Array.Reverse(coordinates);Assert.Equal(coordinates.Select(c=>plain.SampleDetailed(mount.Database.Id,c)),dense.SampleDenseRow(mount.Database.Id,coordinates));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>dense.SampleDenseRow(mount.Database.Id,coordinates,cancel.Token));
    }
    [Fact]
    public async Task DenseRowsRecheckFilesInsteadOfReusingDisconnectedData()
    {
        DemGdalRuntime.EnsureReady();var source=Path.Combine(_root,"disconnected");Directory.CreateDirectory(source);Create(source,22,113,true);
        var catalog=new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root,"data")));var mount=await catalog.MountAsync("连接检查",source,unitMetersConfirmed:true);
        var coordinates=new[]{new GeoCoordinate(22.5,113.5)};
        using(var provider=new DemElevationProvider(catalog,rasterWindowCacheCapacity:2))Assert.Equal(DemSampleStatus.Valid,provider.SampleDenseRow(mount.Database.Id,coordinates)[0].Status);
        Directory.Move(source,source+"-removed");
        using var after=new DemElevationProvider(catalog,rasterWindowCacheCapacity:2);
        Assert.Equal(DemSampleStatus.DatabaseDisconnected,after.SampleDenseRow(mount.Database.Id,coordinates)[0].Status);
    }
    [Fact]
    public async Task ConcurrentSamplingDoesNotMixReusableBuffers()
    {
        DemGdalRuntime.EnsureReady();
        var source = Path.Combine(_root, "concurrent");
        Directory.CreateDirectory(source); Create(source, 22, 113, false);
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root, "data")));
        var mounted = await catalog.MountAsync("并发采样", source, unitMetersConfirmed: true);
        using var provider = new DemElevationProvider(catalog, rasterWindowCacheCapacity: 2);
        var points = Enumerable.Range(0, 1000)
            .Select(i => new GeoCoordinate(22.01 + (i % 97) / 100d, 113.01 + (i % 89) / 100d)).ToArray();
        var expected = points.Select(p => provider.SampleDetailed(mounted.Database.Id, p)).ToArray();
        Parallel.For(0, points.Length, i =>
            Assert.Equal(expected[i], provider.SampleDetailed(mounted.Database.Id, points[i])));
    }

    [Fact]
    public async Task LruReadKeepsRecentlyUsedTile_AndClearIsIsolated()
    {
        var cache=new MapTileDiskCache(Path.Combine(_root,"cache"),leastRecentlyUsed:true);var a=Guid.NewGuid();var b=Guid.NewGuid();int calls=0;
        Task<CachedMapTile> Fetch(){calls++;return Task.FromResult(new CachedMapTile(new byte[100],"image/png",DateTimeOffset.MaxValue));}
        await cache.GetAsync(a,"old",300,Fetch);await Task.Delay(25);await cache.GetAsync(a,"new",300,Fetch);await Task.Delay(25);await cache.GetAsync(a,"old",300,Fetch);
        await cache.GetAsync(b,"independent",300,Fetch);await cache.TrimAsync(a,150);
        await cache.GetAsync(a,"old",150,Fetch);Assert.Equal(3,calls);
        await cache.TrimAsync(a,0);await cache.GetAsync(b,"independent",300,Fetch);Assert.Equal(3,calls);
    }
    [Fact]
    public async Task GpuSurfaceKeepsValidPixelsAlongsideNoDataAndSharesExactNeighborHalo()
    {
        DemGdalRuntime.EnsureReady();var folder=Path.Combine(_root,"surface");Directory.CreateDirectory(folder);Create(folder,22,113,false);
        var catalog=new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root,"data")));var mount=await catalog.MountAsync("GPU有效性",folder,unitMetersConfirmed:true);
        using var provider=new DemElevationProvider(catalog,rasterWindowCacheCapacity:2);var sampler=new TerrainTileSampler(catalog,provider);
        var left=sampler.SampleSurface(mount.Database.Id,9,417,222);var right=sampler.SampleSurface(mount.Database.Id,9,418,222);
        const int size=TerrainRgb.SurfaceTileSize;
        Assert.Equal(size*size*4,left.Length);Assert.Contains((byte)0,Enumerable.Range(0,size*size).Select(i=>left[i*4+3]));Assert.Contains((byte)255,Enumerable.Range(0,size*size).Select(i=>left[i*4+3]));
        for(var row=0;row<size;row++)for(var channel=0;channel<4;channel++)for(var halo=0;halo<4;halo++)
            Assert.Equal(left[(row*size+512+halo)*4+channel],right[(row*size+halo)*4+channel]);
        Assert.Throws<TerrainUnavailableException>(()=>sampler.Sample(mount.Database.Id,9,417,222));
    }
    private static void Create(string folder,int lat,int lon,bool hgt)
    {
        const int n=1201;var values=new double[n*n];var raw=hgt?new byte[n*n*2]:null;
        for(var row=0;row<n;row++)for(var col=0;col<n;col++)
        {
            var h=(short)Math.Round(1000+(lat+1-row/1200d-22)*100+(lon+col/1200d-113)*50);values[row*n+col]=h;
            if(raw is not null){raw[(row*n+col)*2]=(byte)(h>>8);raw[(row*n+col)*2+1]=(byte)h;}
        }
        var path=Path.Combine(folder,$"N{lat}E{lon}."+(hgt?"hgt":"tif"));
        if(raw is not null){File.WriteAllBytes(path,raw);return;}
        using var dataset=Gdal.GetDriverByName("GTiff").Create(path,n,n,1,DataType.GDT_Int16,[]);
        dataset.SetGeoTransform([lon-1/2400d,1/1200d,0,lat+1+1/2400d,0,-1/1200d]);
        using var srs=new SpatialReference("");srs.ImportFromEPSG(4326);srs.ExportToWkt(out var wkt,[]);dataset.SetProjection(wkt);
        using var band=dataset.GetRasterBand(1);band.SetUnitType("m");band.WriteRaster(0,0,n,n,values,n,n,0,0);dataset.FlushCache();
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
