using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Maps;

/// <summary>rd5只读显示派生缓存。分块解码时短暂共享数据切换锁；不修改路由配置、原文件或项目。</summary>
public sealed class OsmRoadTileSource(string dataDirectory,string runtimeDirectory,string helperJar,string cacheRoot,SemaphoreSlim? dataGate=null):IDisposable
{
    public static readonly Guid CacheId=Guid.Parse("e8752365-3470-4993-89a8-08964cfae504");
    private readonly MapTileDiskCache _cache=MapTileDiskCache.Shared(cacheRoot);
    private readonly SemaphoreSlim _tiles=new(2,2);
    private readonly SemaphoreSlim _decodeGate=new(1,1);
    private readonly object _sync=new();
    private OsmRoadReaderProcess? _reader;
    private CancellationTokenSource _generation=new();
    private OsmRoadSettings _settings=new();
    private string _identity="",_signature="";
    private int _epoch;
    private bool _active;
    private long _chunksDecoded,_tilesEncoded;
    private sealed record View(double West,double South,double East,double North);
    private View? _view;
    private bool _terrainEnabled;
    public string Status {get;private set;}="OSM路网尚未开启。";
    public int AvailableFiles {get;private set;}
    public long ChunksDecoded=>Interlocked.Read(ref _chunksDecoded);
    public long TilesEncoded=>Interlocked.Read(ref _tilesEncoded);
    public string DataDirectory=>dataDirectory;
    public int ConfigurationRevision=>Volatile.Read(ref _epoch);
    public void InvalidateData()
    {lock(_sync){var old=_generation;_generation=new();old.Cancel();_reader?.Dispose();_reader=null;_epoch++;_identity="";_signature="";_active=false;}}
    public async Task<object> ConfigureAsync(OsmRoadSettings settings,CancellationToken token=default,bool? terrainEnabled=null)
    {
        if(!settings.IsValid)throw new ArgumentException("OSM路网设置无效。");
        var metadata=await Task.Run(()=>{
            var files=Directory.Exists(dataDirectory)?Directory.GetFiles(dataDirectory,"*.rd5").Select(p=>new FileInfo(p)).OrderBy(f=>f.Name,StringComparer.OrdinalIgnoreCase).ToArray():[];
            var lookup=Path.Combine(runtimeDirectory,"profiles2","lookups.dat");
            var index=string.Join("\n",files.Select(f=>$"{f.Name}/{f.Length}/{f.LastWriteTimeUtc.Ticks}"));
            var lookupHash=File.Exists(lookup)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(lookup))):"missing";
            return (Count:files.Length,Hash:Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("rd5-road-mvt-1/"+dataDirectory+"/"+lookupHash+"/"+index))));
        },token).ConfigureAwait(false);
        lock(_sync)
        {
            var modeChanged=terrainEnabled.HasValue&&terrainEnabled.Value!=_terrainEnabled;
            if(terrainEnabled.HasValue)_terrainEnabled=terrainEnabled.Value;
            var active=settings.Enabled&&settings.Opacity>0&&settings.Styles.Any(s=>s.Enabled)&&metadata.Count>0;
            if(metadata.Hash!=_identity||(!active&&_active)||modeChanged)
            {var old=_generation;_generation=new();old.Cancel();_reader?.Dispose();_reader=null;}
            _settings=settings.DeepCopy();_identity=metadata.Hash;_signature=_identity+"-"+_epoch+(_terrainEnabled?"-3d":"-2d");_active=active;AvailableFiles=metadata.Count;
            _cache.Configure(CacheId,(long)settings.CacheCapacityMb*1024*1024);
            Status=metadata.Count==0?"未找到BRouter路由数据，请先导入或下载rd5。":$"已加载 {metadata.Count} 个rd5；路网按当前视野后台提取。";
            return new{enabled=active,signature=_signature,opacity=settings.Opacity,minZoom=settings.MinimumZoom,maxZoom=15,styles=OsmRoadDefaults.BuildRenderStyles(settings),status=Status};
        }
    }
    public void SetViewport(double west,double south,double east,double north)
    {if(new[]{west,south,east,north}.All(double.IsFinite)&&south<north)_view=new(west,south,east,north);}
    private void CheckView(int z,int x,int y,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(_terrainEnabled)return; // 3D瓦片覆盖由引擎决定。
        var view=_view;if(view is null||view.West>view.East)return;
        var n=Math.Pow(2,z);var west=x/n*360-180;var east=(x+1)/n*360-180;
        var north=Math.Atan(Math.Sinh(Math.PI*(1-2*y/n)))*180/Math.PI;var south=Math.Atan(Math.Sinh(Math.PI*(1-2*(y+1)/n)))*180/Math.PI;
        var shift=360*Math.Round(((view.West+view.East)-(west+east))/720);var pad=east-west;
        if(east+shift<view.West-pad||west+shift>view.East+pad||north<view.South-pad||south>view.North+pad)throw new OperationCanceledException("地图已移动，跳过离屏路网。");
    }

    public async Task<byte[]> GetAsync(Uri uri)
    {
        string signature,identity;CancellationToken token;OsmRoadSettings settings;
        lock(_sync){if(!_active)throw new OperationCanceledException();signature=_signature;identity=_identity;token=_generation.Token;settings=_settings;}
        var p=uri.AbsolutePath.Trim('/').Split('/');
        if(p.Length!=4||p[0]!=signature)throw new OperationCanceledException();
        if(!int.TryParse(p[1],out var z)||!int.TryParse(p[2],out var x)||!p[3].EndsWith(".pbf",StringComparison.Ordinal)||!int.TryParse(p[3][..^4],out var y)||z<settings.MinimumZoom||z>15||x<0||y<0||x>=(1<<z)||y>=(1<<z))throw new ArgumentException("OSM路网瓦片参数无效。");
        var capacity=(long)settings.CacheCapacityMb*1024*1024;
        var result=await _cache.GetAsync(CacheId,$"{identity}/mvt/{z}/{x}/{y}",capacity,async()=>
        {
            await TerrainGenerationPriority.Shared.WaitForDerivedAsync(token).ConfigureAwait(false);
            await _tiles.WaitAsync(token).ConfigureAwait(false);
            var documents=new List<JsonDocument>();
            try
            {
                CheckView(z,x,y,token);var n=Math.Pow(2,z);var pad=360/n/64;
                var west=x/n*360-180-pad;var east=(x+1)/n*360-180+pad;
                var north=Math.Atan(Math.Sinh(Math.PI*(1-2*y/n)))*180/Math.PI+pad;var south=Math.Atan(Math.Sinh(Math.PI*(1-2*(y+1)/n)))*180/Math.PI-pad;
                for(var cy=Math.Max(0,(int)Math.Floor((south+90)*16));cy<=Math.Min(2879,(int)Math.Floor((north+90)*16));cy++)
                for(var cx=(int)Math.Floor((west+180)*16);cx<=(int)Math.Floor((east+180)*16);cx++)
                {
                    CheckView(z,x,y,token);await TerrainGenerationPriority.Shared.WaitForDerivedAsync(token).ConfigureAwait(false);
                    var xx=((cx%5760)+5760)%5760;
                    var chunk=await _cache.GetAsync(CacheId,$"{identity}/chunk/{xx}/{cy}",capacity,async()=>
                    {
                        await TerrainGenerationPriority.Shared.WaitForDerivedAsync(token).ConfigureAwait(false);
                        await _decodeGate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            if(dataGate is not null)await dataGate.WaitAsync(token).ConfigureAwait(false);
                            try
                            {
                            token.ThrowIfCancellationRequested();OsmRoadReaderProcess reader;
                            lock(_sync){token.ThrowIfCancellationRequested();reader=_reader??=new(Path.Combine(runtimeDirectory,"java","bin","java.exe"),helperJar,Path.Combine(runtimeDirectory,"brouter-1.7.10-all.jar"),Path.Combine(runtimeDirectory,"profiles2","lookups.dat"),dataDirectory);}
                            var raw=await reader.ReadChunkAsync(xx,cy,token).ConfigureAwait(false);Interlocked.Increment(ref _chunksDecoded);
                            using var buffer=new MemoryStream();using(var gzip=new GZipStream(buffer,CompressionLevel.Fastest,true))await gzip.WriteAsync(raw,token).ConfigureAwait(false);
                            return new CachedMapTile(buffer.ToArray(),"application/gzip",DateTimeOffset.MaxValue);
                            }
                            finally{dataGate?.Release();}
                        }
                        finally{_decodeGate.Release();}
                    },token).ConfigureAwait(false);
                    using var input=new MemoryStream(chunk.Bytes);using var unzip=new GZipStream(input,CompressionMode.Decompress);
                    documents.Add(await JsonDocument.ParseAsync(unzip,cancellationToken:token).ConfigureAwait(false));
                }
                CheckView(z,x,y,token);var bytes=await Task.Run(()=>OsmRoadVectorTile.Encode(documents.Select(d=>d.RootElement),z,x,y),token).ConfigureAwait(false);
                Interlocked.Increment(ref _tilesEncoded);Status=$"OSM路网已生成；复用缓存后无需再次解码。";
                return new CachedMapTile(bytes,"application/vnd.mapbox-vector-tile",DateTimeOffset.MaxValue);
            }
            finally{foreach(var doc in documents)doc.Dispose();_tiles.Release();}
        },token).ConfigureAwait(false);
        return result.Bytes;
    }
    public async Task ClearAsync()
    {
        lock(_sync){var old=_generation;_generation=new();old.Cancel();_reader?.Dispose();_reader=null;_epoch++;_signature=_identity+"-"+_epoch+(_terrainEnabled?"-3d":"-2d");}
        await _cache.ClearAsync(CacheId).ConfigureAwait(false);Status="OSM路网缓存已清空，原始rd5及其他缓存未改动。";
    }
    public void Dispose(){lock(_sync){_generation.Cancel();_reader?.Dispose();_reader=null;}}
    public async Task StopAsync(){Dispose();await _decodeGate.WaitAsync().ConfigureAwait(false);_decodeGate.Release();}
}
