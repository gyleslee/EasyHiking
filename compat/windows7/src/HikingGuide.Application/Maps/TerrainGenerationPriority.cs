namespace HikingGuide.Application.Maps;

/// <summary>地图后台任务的让行信号。缓存命中不等待；未完成的地形与视野移动优先于派生计算。</summary>
public sealed class TerrainGenerationPriority
{
    public static TerrainGenerationPriority Shared { get; }=new();
    private readonly object _gate=new();
    private bool _ready=true;
    private int _terrain;
    private TaskCompletionSource<bool>? _resume;
    public void SetTerrainReady(bool ready){lock(_gate){_ready=ready;Refresh();}}
    public IDisposable BeginTerrain()
    {lock(_gate){_terrain++;Refresh();}return new Lease(this);}
    public Task WaitForDerivedAsync(CancellationToken token=default)
    {lock(_gate){token.ThrowIfCancellationRequested();return _resume?.Task.WaitAsync(token)??Task.CompletedTask;}}
    private void Refresh()
    {
        if(!_ready||_terrain>0)_resume??=new(TaskCreationOptions.RunContinuationsAsynchronously);
        else{var resume=_resume;_resume=null;resume?.TrySetResult(true);}
    }
    private sealed class Lease(TerrainGenerationPriority owner):IDisposable
    {
        private TerrainGenerationPriority? _owner=owner;
        public void Dispose(){var value=Interlocked.Exchange(ref _owner,null);if(value is not null)lock(value._gate){value._terrain--;value.Refresh();}}
    }
}
