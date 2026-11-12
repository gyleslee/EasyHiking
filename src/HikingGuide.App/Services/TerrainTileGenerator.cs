using System.IO;
using System.Runtime.InteropServices;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;
using SkiaSharp;

namespace HikingGuide.App.Services;

public sealed partial class TerrainTileGenerator(DemDatabaseCatalog catalog) : IDisposable
{
    // Terrain密集读取使用独立只读句柄/原像元窗口，避免压缩TIF每像元解压及阻塞攻略分析句柄。
    private readonly DemElevationProvider _terrainProvider=new(catalog,maximumOpenHandles:4,rasterWindowCacheCapacity:32);
    public static readonly Guid CacheId=Guid.Parse("e8752365-3470-4993-89a8-08964cfae503");
    public static string CacheRoot=>Path.Combine(new ApplicationDataPaths().RootDirectory,"TerrainCache");
    private readonly MapTileDiskCache _cache=MapTileDiskCache.SharedLru(CacheRoot);
    private readonly SemaphoreSlim _gate=new(2,2);
    private readonly object _sync=new();
    private CancellationTokenSource _generation=new();
    private Terrain3DSettings _settings=new();
    private string? _pathPrefix;
    private int _maxNativeZoom;
    private TerrainTileSampler? _sampler;
    public TerrainTileSampler Sampler =>_sampler??=new(catalog,_terrainProvider);
    public int GeneratedTiles {get;private set;}
    public long Revision {get;private set;}
    public Task<bool> CheckHealthAsync(double latitude,double longitude)=>Task.Run(()=>
    {
        try
        {
            Terrain3DSettings s;string? prefix;lock(_sync){s=_settings;prefix=_pathPrefix;}
            if(s.Enabled&&s.TerrainDemDatabaseId is {} selected&&catalog.Databases.FirstOrDefault(d=>d.Id==selected) is {} current&&prefix?.Contains('/'+current.Fingerprint+'/',StringComparison.Ordinal)!=true)return false;
            return !s.Enabled||(s.TerrainDemDatabaseId is {} id&&Sampler.Describe(id) is not null&&
                _terrainProvider.SampleDetailed(id,new HikingGuide.Core.Geometry.GeoCoordinate(latitude,longitude)).Status==DemSampleStatus.Valid);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or KeyNotFoundException){return false;}
    });
    public TerrainMapConfiguration Configure(Terrain3DSettings settings)
    {
        lock(_sync)
        {
            var choice=settings.TerrainDemDatabaseId is {} id?Sampler.Describe(id):null;
            var db=catalog.Databases.FirstOrDefault(d=>d.Id==settings.TerrainDemDatabaseId);
            var prefix=choice is null||db is null?null:$"/terrain/{choice.Id:N}/{db.Fingerprint}/{TerrainRgb.EncodingVersion}/";
            if(prefix!=_pathPrefix||settings.Enabled!=_settings.Enabled)
            {var old=_generation;_generation=new();old.Cancel();TerrainGenerationPriority.Shared.SetTerrainReady(!settings.Enabled||choice is null);}
            _settings=settings;_pathPrefix=prefix;_maxNativeZoom=choice?.MaxNativeZoom??0;Revision++;
            _cache.Configure(CacheId,(long)settings.CacheLimitMb*1024*1024);
            _=TrimSafelyAsync((long)settings.CacheLimitMb*1024*1024);
            return new(settings.Enabled&&choice is not null,choice?.Id,prefix is null?null:$"https://hikingguide-terrain.local{prefix}{{z}}/{{x}}/{{y}}.png",
                choice?.MaxNativeZoom??0,settings.Exaggeration,settings.DefaultPitch,Revision,
                settings.Enabled&&choice is null?"当前3D地形DEM未连接，地图已切回2D。":null);
        }
    }
    public void SetWorkReady(long revision,bool ready)
    {lock(_sync){if(revision==Revision)TerrainGenerationPriority.Shared.SetTerrainReady(!_settings.Enabled||ready);}}
    // 已收到的DEM请求必须完成；可视覆盖包括倾斜地形和接缝邻域，不等于2D地面范围。
    public async Task<byte[]> GetAsync(Uri uri)
    {
        Terrain3DSettings settings;CancellationToken token;string? prefix;int maxNative;
        lock(_sync){settings=_settings;token=_generation.Token;prefix=_pathPrefix;maxNative=_maxNativeZoom;}
        if(!settings.Enabled||prefix is null)throw new OperationCanceledException();
        var currentDatabase=catalog.Databases.FirstOrDefault(d=>d.Id==settings.TerrainDemDatabaseId);
        if(currentDatabase is null||!Directory.Exists(currentDatabase.RootPath)||!prefix.Contains('/'+currentDatabase.Fingerprint+'/',StringComparison.Ordinal))
            throw new TerrainUnavailableException("当前3D地形DEM未连接或数据已更新，地图已切回2D，请检查DEM后重新启用。");
        if(!uri.AbsolutePath.StartsWith(prefix,StringComparison.Ordinal))throw new FileNotFoundException("地形瓦片身份已失效。");
        var p=uri.AbsolutePath[prefix.Length..].Split('/');
        if(p.Length!=3||!int.TryParse(p[0],out var z)||!int.TryParse(p[1],out var x)||!p[2].EndsWith(".png",StringComparison.Ordinal)||!int.TryParse(p[2][..^4],out var y)||z is <0 or >18||x<0||y<0||x>=(1<<z)||y>=(1<<z))throw new ArgumentException("地形瓦片参数无效。");
        if(z>maxNative)throw new ArgumentException("请求层级超过当前DEM原生最高级别。");
        var result=await _cache.GetAsync(CacheId,uri.AbsolutePath,(long)settings.CacheLimitMb*1024*1024,async()=>
        {
            using var priority=TerrainGenerationPriority.Shared.BeginTerrain();
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                var pixels=await Task.Run(()=>Sampler.Sample(settings.TerrainDemDatabaseId!.Value,z,x,y,token,()=>token.ThrowIfCancellationRequested()),token).ConfigureAwait(false);
                using var bitmap=new SKBitmap(512,512,SKColorType.Rgba8888,SKAlphaType.Opaque);
                Marshal.Copy(pixels,0,bitmap.GetPixels(),pixels.Length);
                using var image=SKImage.FromBitmap(bitmap);using var png=image.Encode(SKEncodedImageFormat.Png,100);
                token.ThrowIfCancellationRequested();GeneratedTiles++;
                return new CachedMapTile(png.ToArray(),"image/png",DateTimeOffset.MaxValue);
            }
            finally{_gate.Release();}
        },token).ConfigureAwait(false);
        return result.Bytes;
    }
    public Task<long> UsageAsync()=>Task.Run(()=>Directory.Exists(CacheRoot)?Directory.EnumerateFiles(CacheRoot,"*.tile",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length):0L);
    public string? CacheWarning {get;private set;}
    private async Task TrimSafelyAsync(long capacity)
    {try{await _cache.TrimAsync(CacheId,capacity);CacheWarning=null;}catch(Exception e) when(e is IOException or UnauthorizedAccessException){CacheWarning="3D缓存暂时无法清理，请检查缓存目录权限及磁盘空间。";}}
    public async Task ClearAsync()
    {
        lock(_sync){var old=_generation;_generation=new();old.Cancel();}
        await _gate.WaitAsync();await _gate.WaitAsync();
        try{await _cache.TrimAsync(CacheId,0);}
        finally{_cache.Configure(CacheId,(long)_settings.CacheLimitMb*1024*1024);_gate.Release(2);}
    }
    public void Dispose(){lock(_sync){_generation.Cancel();_surfaceGeneration.Cancel();}TerrainGenerationPriority.Shared.SetTerrainReady(true);_terrainProvider.Dispose();}
}
