using HikingGuide.Infrastructure.Maps;
namespace HikingGuide.Infrastructure.Tests;
public sealed class MapTileCacheClearTests:IDisposable
{
    private readonly string _root=Path.Combine(@"F:\Codex\Temp","HikingGuide-clear-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task ClearIsCategorySpecificAndKeepsCapacityAndFreshRequestsIndependent()
    {
        var cache=new MapTileDiskCache(_root);var selected=Guid.NewGuid();var other=Guid.NewGuid();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CachedMapTile> Tile(byte value)=>Task.FromResult(new CachedMapTile([value],"image/png",DateTimeOffset.MaxValue));
        await cache.GetAsync(other,"other",4096,()=>Tile(8));await cache.GetAsync(selected,"existing",4096,()=>Tile(2));
        var pending=cache.GetAsync(selected,"pending",4096,async()=>{started.SetResult();await release.Task;return await Tile(1);});
        await started.Task;await cache.ClearAsync(selected);
        var fresh=await cache.GetAsync(selected,"pending",4096,()=>Tile(9));Assert.Equal(9,fresh.Bytes[0]);
        release.SetResult();await pending;
        var cached=await cache.GetAsync(selected,"pending",4096,()=>throw new Exception("旧任务覆盖了新缓存或容量被清零。"));
        Assert.Equal(9,cached.Bytes[0]);
        Assert.Single(Directory.GetFiles(Path.Combine(_root,selected.ToString("N")),"*.tile"));
        var untouched=await cache.GetAsync(other,"other",4096,()=>throw new Exception("其他类别被误清。"));Assert.Equal(8,untouched.Bytes[0]);
        await cache.ClearAsync(selected);Assert.Empty(Directory.GetFiles(Path.Combine(_root,selected.ToString("N")),"*.tile"));
        await cache.GetAsync(selected,"after",4096,()=>Tile(3));Assert.Single(Directory.GetFiles(Path.Combine(_root,selected.ToString("N")),"*.tile"));
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
