using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace HikingGuide.Infrastructure.Maps;

public sealed record CachedMapTile(byte[] Bytes,string ContentType,DateTimeOffset ExpiresAt,bool CanCache=true);

/// <summary>按图源计费的持久FIFO缓存，单瓦片并发请求合并；不接触项目或DEM文件。</summary>
public sealed class MapTileDiskCache
{
    private const int Magic=0x48475431;
    private const int MaxTileBytes=8*1024*1024;
    private static readonly ConcurrentDictionary<string,MapTileDiskCache> SharedCaches=new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly TimeProvider _clock;
    private readonly bool _leastRecentlyUsed;
    private readonly ConcurrentDictionary<Guid,SourceState> _sources=new();
    private readonly ConcurrentDictionary<Guid,long> _limits=new();
    private readonly ConcurrentDictionary<string,Lazy<Task<CachedMapTile>>> _inflight=new();
    public MapTileDiskCache(string root,TimeProvider? clock=null,bool leastRecentlyUsed=false) {_root=Path.GetFullPath(root);_clock=clock??TimeProvider.System;_leastRecentlyUsed=leastRecentlyUsed;}
    public static MapTileDiskCache Shared(string root) => SharedCaches.GetOrAdd(Path.GetFullPath(root),p=>new(p));
    public static MapTileDiskCache SharedLru(string root) => SharedCaches.GetOrAdd(Path.GetFullPath(root),p=>new(p,leastRecentlyUsed:true));
    public void Configure(Guid source,long bytes) => _limits[source]=Math.Max(0,bytes);
    public async Task<CachedMapTile> GetAsync(Guid source,string key,long capacityBytes,Func<Task<CachedMapTile>> fetch,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(source==Guid.Empty) throw new ArgumentException("图源ID无效。");
        _limits.TryAdd(source,Math.Max(0,capacityBytes));
        var state=_sources.GetOrAdd(source,_=>new());var generation=Interlocked.Read(ref state.Generation);
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))); var flight=source.ToString("N")+hash+":"+generation;
        var lazy=_inflight.GetOrAdd(flight,_=>new(()=>GetCoreAsync(source,hash,fetch,generation),LazyThreadSafetyMode.ExecutionAndPublication));
        // 即使调用者先取消等待，也必须在后台任务结束时移除失败/取消的占位，避免下次请求复用旧异常。
        _=lazy.Value.ContinueWith(_=>_inflight.TryRemove(new KeyValuePair<string,Lazy<Task<CachedMapTile>>>(flight,lazy)),
            CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        try {return await lazy.Value.WaitAsync(token).ConfigureAwait(false);}
        finally {if(lazy.IsValueCreated && lazy.Value.IsCompleted)_inflight.TryRemove(new KeyValuePair<string,Lazy<Task<CachedMapTile>>>(flight,lazy));}
    }
    private async Task<CachedMapTile> GetCoreAsync(Guid source,string hash,Func<Task<CachedMapTile>> fetch,long generation)
    {
        var state=_sources.GetOrAdd(source,_=>new()); var path=Path.Combine(_root,source.ToString("N"),hash+".tile");
        await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(generation!=state.Generation)throw new OperationCanceledException("缓存已清除，请重新请求瓦片。");
            try
            {
                Load(state,source); Evict(state,_limits[source]);
                if(state.Files.ContainsKey(path))
                {
                    try
                    {
                        if(new FileInfo(path).Length>MaxTileBytes+4096)throw new InvalidDataException("瓦片缓存长度无效。");
                        var tile=Decode(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
                        if(tile.ExpiresAt>_clock.GetUtcNow())
                        {
                            if(_leastRecentlyUsed){var now=_clock.GetUtcNow().UtcDateTime;File.SetLastWriteTimeUtc(path,now);state.Files[path]=state.Files[path] with{Written=now};}
                            return tile;
                        }
                    }
                    catch(Exception e) when(e is IOException or InvalidDataException or ArgumentException or EndOfStreamException) { }
                    Remove(state,path);
                }
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
        }
        finally {state.Gate.Release();}
        var fetched=await fetch().ConfigureAwait(false);
        if(!fetched.CanCache || fetched.ExpiresAt<=_clock.GetUtcNow() || fetched.Bytes.Length==0 || fetched.Bytes.Length>MaxTileBytes)return fetched;
        await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(generation!=state.Generation)return fetched;
            try
            {
                Load(state,source); var bytes=Encode(fetched); if(bytes.LongLength>_limits[source])return fetched;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try {await File.WriteAllBytesAsync(temp,bytes).ConfigureAwait(false); File.Move(temp,path,true);}
                finally {if(File.Exists(temp))File.Delete(temp);}
                if(state.Files.Remove(path,out var old))state.Total-=old.Length;
                state.Files[path]=new(bytes.LongLength,DateTime.UtcNow);state.Total+=bytes.LongLength;Evict(state,_limits[source]);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
        }
        finally {state.Gate.Release();}
        return fetched;
    }
    public Task TrimAsync(Guid source,long capacityBytes) => Task.Run(async()=>
    {
        Configure(source,capacityBytes);var state=_sources.GetOrAdd(source,_=>new());await state.Gate.WaitAsync().ConfigureAwait(false);
        try {Load(state,source);Evict(state,_limits[source]);} finally {state.Gate.Release();}
    });
    /// <summary>只清所选类别并使清除前的任务失去回写资格，不改变其容量或其他类别。</summary>
    public Task ClearAsync(Guid source)=>Task.Run(async()=>
    {
        if(source==Guid.Empty)throw new ArgumentException("图源ID无效。");
        var state=_sources.GetOrAdd(source,_=>new());await state.Gate.WaitAsync().ConfigureAwait(false);
        try{Interlocked.Increment(ref state.Generation);state.Loaded=false;Load(state,source);Evict(state,0);}
        finally{state.Gate.Release();}
    });
    private void Load(SourceState state,Guid source)
    {
        if(state.Loaded)return; var dir=Path.Combine(_root,source.ToString("N"));
        var found=new Dictionary<string,FileEntry>(StringComparer.OrdinalIgnoreCase);long total=0;
        if(Directory.Exists(dir))foreach(var file in new DirectoryInfo(dir).EnumerateFiles("*.tile"))
        {
            if(file.Name.Length!=69 || !file.Name[..64].All(Uri.IsHexDigit))continue;
            found[file.FullName]=new(file.Length,file.LastWriteTimeUtc);total+=file.Length;
        }
        state.Files.Clear();foreach(var file in found)state.Files.Add(file.Key,file.Value);state.Total=total;
        state.Loaded=true;
    }
    private static void Remove(SourceState state,string path)
    {
        File.Delete(path);if(state.Files.Remove(path,out var file))state.Total-=file.Length;
    }
    private static void Evict(SourceState state,long capacity)
    {
        if(state.Total<=capacity)return;
        foreach(var file in state.Files.OrderBy(f=>f.Value.Written).ThenBy(f=>f.Key).ToArray())
        {Remove(state,file.Key);if(state.Total<=capacity)break;}
    }
    private static byte[] Encode(CachedMapTile tile)
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
        writer.Write(Magic);writer.Write(tile.ExpiresAt.UtcTicks);writer.Write(tile.ContentType);writer.Write(tile.Bytes.Length);writer.Write(tile.Bytes);return stream.ToArray();
    }
    private static CachedMapTile Decode(byte[] bytes)
    {
        if(bytes.Length>MaxTileBytes+4096)throw new InvalidDataException("瓦片缓存长度无效。");
        using var stream=new MemoryStream(bytes);using var reader=new BinaryReader(stream,Encoding.UTF8);
        if(reader.ReadInt32()!=Magic)throw new InvalidDataException("瓦片缓存格式无效。");
        var expires=new DateTimeOffset(reader.ReadInt64(),TimeSpan.Zero);var type=reader.ReadString();var count=reader.ReadInt32();
        if(type.Length>256 || type.Contains('\r') || type.Contains('\n') || count<=0 || count>MaxTileBytes || stream.Length-stream.Position!=count)throw new InvalidDataException("瓦片缓存内容无效。");
        return new(reader.ReadBytes(count),type,expires);
    }
    private sealed record FileEntry(long Length,DateTime Written);
    private sealed class SourceState
    {
        public SemaphoreSlim Gate {get;}=new(1,1);public Dictionary<string,FileEntry> Files {get;}=new(StringComparer.OrdinalIgnoreCase);
        public long Total; public bool Loaded; public long Generation;
    }
}
