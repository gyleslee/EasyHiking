using System.IO;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Models;
using HikingGuide.Application.Maps;
using HikingGuide.Infrastructure.Maps;

namespace HikingGuide.App.Services;

public sealed partial class TerrainTileGenerator
{
    private readonly MapTileDiskCache _surfaceCache=MapTileDiskCache.Shared(DemTerrainTiles.CacheRoot);
    private readonly SemaphoreSlim _surfaceGate=new(1,1);
    private CancellationTokenSource _surfaceGeneration=new();
    private string? _surfacePrefix;
    private Guid? _surfaceDem;
    private long _surfaceCapacity;
    private bool _surfaceClearing;
    public async Task ClearGpuSurfaceAsync()
    {
        lock(_sync){_surfaceClearing=true;_surfaceGeneration.Cancel();}
        await _surfaceGate.WaitAsync().ConfigureAwait(false);
        try{await _surfaceCache.ClearAsync(DemTerrainTiles.ContourCacheId).ConfigureAwait(false);}
        finally{lock(_sync){_surfaceGeneration=new();_surfaceClearing=false;}_surfaceGate.Release();}
    }
    public int SurfaceGeneratedTiles {get;private set;}
    public object ConfigureGpuSurface(OfflineTerrainSettings settings)
    {
        lock(_sync)
        {
            var choice=settings.DemDatabaseId is {} id?Sampler.Describe(id):null;
            var db=catalog.Databases.FirstOrDefault(d=>d.Id==settings.DemDatabaseId);
            var prefix=choice is null||db is null?null:$"/contour-dem/{choice.Id:N}/{db.Fingerprint}/rgb-516-mask-v2/";
            if(prefix!=_surfacePrefix||!settings.ContoursVisible)
            {var old=_surfaceGeneration;_surfaceGeneration=new();old.Cancel();}
            _surfacePrefix=prefix;_surfaceDem=choice?.Id;_surfaceCapacity=(long)settings.ContourCacheMb*1024*1024;
            _surfaceCache.Configure(DemTerrainTiles.ContourCacheId,_surfaceCapacity);
            return new {surfaceUrlTemplate=prefix is null?null:$"https://hikingguide-terrain.local{prefix}{{z}}/{{x}}/{{y}}.rgba",
                maxNativeZoom=choice?.MaxNativeZoom??0,demId=choice?.Id,ready=choice is not null,size=TerrainRgb.SurfaceTileSize,border=TerrainRgb.SurfaceBorder};
        }
    }
    public async Task<byte[]> GetGpuSurfaceAsync(Uri uri)
    {
        string? prefix;Guid? dem;long capacity;CancellationToken token;
        lock(_sync){if(_surfaceClearing)throw new OperationCanceledException();prefix=_surfacePrefix;dem=_surfaceDem;capacity=_surfaceCapacity;token=_surfaceGeneration.Token;}
        if(prefix is null||dem is null||!uri.AbsolutePath.StartsWith(prefix,StringComparison.Ordinal))throw new OperationCanceledException();
        var current=catalog.Databases.FirstOrDefault(d=>d.Id==dem);
        if(current is null||!prefix.Contains('/'+current.Fingerprint+'/',StringComparison.Ordinal))throw new OperationCanceledException();
        var parts=uri.AbsolutePath[prefix.Length..].Split('/');
        if(parts.Length!=3||!int.TryParse(parts[0],out var z)||!int.TryParse(parts[1],out var x)||!parts[2].EndsWith(".rgba",StringComparison.Ordinal)||!int.TryParse(parts[2][..^5],out var y))throw new ArgumentException("GPU高程瓦片参数无效。");
        var tile=await _surfaceCache.GetAsync(DemTerrainTiles.ContourCacheId,uri.AbsolutePath,capacity,async()=>
        {
            await _surfaceGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await TerrainGenerationPriority.Shared.WaitForDerivedAsync(token).ConfigureAwait(false);
                var pixels=await Task.Run(()=>Sampler.SampleSurface(dem.Value,z,x,y,token),token).ConfigureAwait(false);
                SurfaceGeneratedTiles++;
                return new CachedMapTile(pixels,"application/octet-stream",DateTimeOffset.MaxValue);
            }
            finally{_surfaceGate.Release();}
        },token).ConfigureAwait(false);
        return tile.Bytes;
    }
}
