using HikingGuide.Application.Maps;

namespace HikingGuide.Application.Tests;
public sealed class TerrainGenerationPriorityTests
{
    [Fact]
    public async Task DerivedWaitsForBothLoadedTerrainAndAllTerrainRequests()
    {
        var scheduler=new TerrainGenerationPriority();scheduler.SetTerrainReady(false);
        var first=scheduler.BeginTerrain();var second=scheduler.BeginTerrain();
        var work=scheduler.WaitForDerivedAsync();Assert.False(work.IsCompleted);
        scheduler.SetTerrainReady(true);Assert.False(work.IsCompleted);
        first.Dispose();Assert.False(work.IsCompleted);second.Dispose();
        await work.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task CancelledDerivedWaiterDoesNotCancelOtherRequests()
    {
        var scheduler=new TerrainGenerationPriority();scheduler.SetTerrainReady(false);
        using var cancel=new CancellationTokenSource();var a=scheduler.WaitForDerivedAsync(cancel.Token);var b=scheduler.WaitForDerivedAsync();
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>a);Assert.False(b.IsCompleted);
        scheduler.SetTerrainReady(true);await b.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task MovingAgainPausesNewWorkAndReturningTo2DResumes()
    {
        var scheduler=new TerrainGenerationPriority();await scheduler.WaitForDerivedAsync();
        scheduler.SetTerrainReady(false);var work=scheduler.WaitForDerivedAsync();Assert.False(work.IsCompleted);
        scheduler.SetTerrainReady(true);await work.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.SetTerrainReady(false);Assert.False(scheduler.WaitForDerivedAsync().IsCompleted);
        scheduler.SetTerrainReady(true);await scheduler.WaitForDerivedAsync();
    }
    [Fact]
    public async Task DisposingLeaseTwiceDoesNotBypassAnotherTerrainRequest()
    {
        var scheduler=new TerrainGenerationPriority();var a=scheduler.BeginTerrain();var b=scheduler.BeginTerrain();
        a.Dispose();a.Dispose();var work=scheduler.WaitForDerivedAsync();Assert.False(work.IsCompleted);
        b.Dispose();await work.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
