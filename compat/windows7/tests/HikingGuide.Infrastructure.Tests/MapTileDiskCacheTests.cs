using HikingGuide.Infrastructure.Maps;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using System.Text.Json;

namespace HikingGuide.Infrastructure.Tests;

public sealed class MapTileDiskCacheTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"HikingGuide-TileCache-"+Guid.NewGuid().ToString("N"));
    private static CachedMapTile Tile(byte value=1,bool allowed=true)=>new(Enumerable.Repeat(value,100).Select(v=>(byte)v).ToArray(),"image/png",DateTimeOffset.UtcNow.AddHours(1),allowed);
    [Fact]
    public async Task RepeatedConcurrentAndRestartRequestsReuseOneDownload()
    {
        var cache=new MapTileDiskCache(_root);var source=Guid.NewGuid();int calls=0;
        async Task<CachedMapTile> Fetch(){Interlocked.Increment(ref calls);await Task.Delay(40);return Tile();}
        await Task.WhenAll(Enumerable.Range(0,12).Select(_=>cache.GetAsync(source,"tile",5000,Fetch)));
        Assert.Equal(1,calls);await cache.GetAsync(source,"tile",5000,Fetch);Assert.Equal(1,calls);
        await new MapTileDiskCache(_root).GetAsync(source,"tile",5000,Fetch);Assert.Equal(1,calls);
    }
    [Fact]
    public async Task CapacityFifoEvictionAndImmediateReductionArePerSource()
    {
        var cache=new MapTileDiskCache(_root);var a=Guid.NewGuid();var b=Guid.NewGuid();int calls=0;
        Task<CachedMapTile> Fetch(){calls++;return Task.FromResult(Tile());}
        await cache.GetAsync(a,"old",300,Fetch);await Task.Delay(20);await cache.GetAsync(a,"new",300,Fetch);
        await cache.GetAsync(b,"old",300,Fetch);await cache.TrimAsync(a,150);
        Assert.Single(Directory.GetFiles(Path.Combine(_root,a.ToString("N")),"*.tile"));
        await cache.GetAsync(a,"new",150,Fetch);Assert.Equal(3,calls);
        await cache.GetAsync(b,"old",300,Fetch);Assert.Equal(3,calls);
        await cache.GetAsync(a,"old",150,Fetch);Assert.Equal(4,calls);
        await cache.TrimAsync(a,0);Assert.Empty(Directory.GetFiles(Path.Combine(_root,a.ToString("N")),"*.tile"));
    }
    [Fact]
    public async Task FailuresNoStoreOversizeAndDisabledCapacityAreNotStored()
    {
        var cache=new MapTileDiskCache(_root);var id=Guid.NewGuid();
        await Assert.ThrowsAsync<HttpRequestException>(()=>cache.GetAsync(id,"error",1000,()=>throw new HttpRequestException("失败")));
        await cache.GetAsync(id,"nostore",1000,()=>Task.FromResult(Tile(allowed:false)));
        await cache.TrimAsync(id,50);await cache.GetAsync(id,"large",50,()=>Task.FromResult(Tile()));
        Assert.False(Directory.Exists(Path.Combine(_root,id.ToString("N"))));
    }
    [Fact]
    public async Task CorruptionIsDiscardedAndRefetched()
    {
        var cache=new MapTileDiskCache(_root);var id=Guid.NewGuid();int calls=0;
        Task<CachedMapTile> Fetch(){calls++;return Task.FromResult(Tile());}
        await cache.GetAsync(id,"one",1000,Fetch);var file=Directory.GetFiles(Path.Combine(_root,id.ToString("N")),"*.tile")[0];
        await File.WriteAllBytesAsync(file,[0,1,2]);await cache.GetAsync(id,"one",1000,Fetch);Assert.Equal(2,calls);
    }
    [Fact]
    public void ExistingSettingsDefault500AndMirrorsShareCacheIdentityButCredentialsChangeIt()
    {
        Assert.Equal(500,JsonSerializer.Deserialize<MapSourceDefinition>("{}")!.CacheCapacityMb);
        var source=new MapSourceDefinition {UrlTemplate="https://{switch:a,b}.example.com/{z}/{x}/{y}.png",CacheCapacityMb=123};
        var slot=new RasterLayerSlot("base",source,["https://a.example.com/{z}/{x}/{y}.png","https://b.example.com/{z}/{x}/{y}.png"],true,1);
        var registry=new MapTileRequestRegistry();var registered=registry.Register([slot])[0];
        using var first=registry.Resolve(new Uri(registered.Templates[0].Replace("{z}","1").Replace("{x}","0").Replace("{y}","0")))!;
        using var second=registry.Resolve(new Uri(registered.Templates[1].Replace("{z}","1").Replace("{x}","0").Replace("{y}","0")))!;
        Assert.True(first.Options.TryGetValue(MapTileRequestRegistry.CacheRequestKey,out var a));Assert.True(second.Options.TryGetValue(MapTileRequestRegistry.CacheRequestKey,out var b));
        Assert.Equal(a!.Key,b!.Key);Assert.Equal(123L*1024*1024,a.CapacityBytes);Assert.Equal(123,source.DeepCopy().CacheCapacityMb);
        source.Headers["Authorization"]="secret";registered=registry.Register([slot])[0];
        using var changed=registry.Resolve(new Uri(registered.Templates[0].Replace("{z}","1").Replace("{x}","0").Replace("{y}","0")))!;
        changed.Options.TryGetValue(MapTileRequestRegistry.CacheRequestKey,out var c);Assert.NotEqual(a.Key,c!.Key);Assert.DoesNotContain("secret",c.Key);
    }
    [Fact]
    public async Task ExpiredTileIsFetchedAgainWithoutWaitingRealTime()
    {
        var clock=new TestClock();var cache=new MapTileDiskCache(_root,clock);var id=Guid.NewGuid();int calls=0;
        Task<CachedMapTile> Fetch(){calls++;return Task.FromResult(new CachedMapTile([1,2,3],"image/png",clock.Now.AddMinutes(1)));}
        await cache.GetAsync(id,"ttl",1000,Fetch);clock.Now=clock.Now.AddMinutes(2);
        await cache.GetAsync(id,"ttl",1000,Fetch);Assert.Equal(2,calls);
    }
    private sealed class TestClock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
