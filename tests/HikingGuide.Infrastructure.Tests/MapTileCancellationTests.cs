using HikingGuide.Infrastructure.Maps;

namespace HikingGuide.Infrastructure.Tests;

public sealed class MapTileCancellationTests
{
    [Fact]
    public async Task CancelledWaiterDoesNotLeaveFaultedGenerationForNextRequest()
    {
        var root=Path.Combine(@"F:\Codex\Temp","HikingGuide-tile-cancel-"+Guid.NewGuid().ToString("N"));
        var cache=new MapTileDiskCache(root);var source=Guid.NewGuid();
        var fetch=new TaskCompletionSource<CachedMapTile>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop=new CancellationTokenSource();
        var first=cache.GetAsync(source,"same-key",10000,()=>fetch.Task,stop.Token);
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
        fetch.SetException(new OperationCanceledException());
        await Task.Delay(100);
        var expected=new CachedMapTile([1,2,3],"image/png",DateTimeOffset.MaxValue);
        var second=await cache.GetAsync(source,"same-key",10000,()=>Task.FromResult(expected));
        Assert.Equal(expected.Bytes,second.Bytes);
        Directory.Delete(root,true);
    }
}
