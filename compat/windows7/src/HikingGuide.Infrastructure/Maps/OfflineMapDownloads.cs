using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Maps;
using HikingGuide.Core.Models;
using Microsoft.Data.Sqlite;
namespace HikingGuide.Infrastructure.Maps;

public enum OfflineDownloadState {Preparing,Downloading,Paused,Failed,Packaging,Completed}
public sealed class OfflineDownloadTask
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Name {get;set;}="";
    public MapSourceDefinition Source {get;set;}=new();
    public OfflineMapRegion Region {get;set;}=new([]);
    public int MinZoom {get;set;}
    public int MaxZoom {get;set;}
    public int Concurrency {get;set;}=2;
    public int RequestIntervalMs {get;set;}=200;
    public long PlannedTiles {get;set;}
    public long CompletedTiles {get;set;}
    public long FailedTiles {get;set;}
    public long DownloadedBytes {get;set;}
    public OfflineDownloadState State {get;set;}
    public string Message {get;set;}="";
    public DateTimeOffset Created {get;set;}=DateTimeOffset.Now;
    public string? PublishedPath {get;set;}
    public string SourceName=>Source.Name;
    public string StateText=>State switch{OfflineDownloadState.Preparing=>"准备中",OfflineDownloadState.Downloading=>"下载中",OfflineDownloadState.Paused=>"已暂停",OfflineDownloadState.Failed=>"待重试",OfflineDownloadState.Packaging=>"生成PMTiles",_=>"已完成"};
    public string ProgressText=>$"{CompletedTiles:N0}/{PlannedTiles:N0}（失败 {FailedTiles:N0}）（{(PlannedTiles>0?Math.Clamp(CompletedTiles*100d/PlannedTiles,0,100):0):F1}%）";
    public string SizeText=>OfflineMapFormatting.Bytes(DownloadedBytes);
    public string ZoomText=>$"Z{MinZoom}—Z{MaxZoom}";
}
public sealed record OfflineDownloadEstimate(OfflineCoverageCount Coverage,long EstimatedBytes,int Samples,string Message)
{
    public string SizeText=>OfflineMapFormatting.Bytes(EstimatedBytes);
}
/// <summary>单个后台下载任务、可恢复SQLite阶段库与原子发布；永不覆盖既有离线包。</summary>
public sealed class OfflineMapDownloads(OfflineMapLibrary library,IOfflineMapTileFetcher fetcher)
{
    private readonly SemaphoreSlim _commands=new(1,1),_database=new(1,1),_rate=new(1,1);
    private readonly ConcurrentDictionary<Guid,OfflineDownloadTask> _tasks=new();
    private CancellationTokenSource? _cancel;
    private Task? _running;
    private Guid? _active;
    private Task? _initialization;
    private readonly object _sync=new();
    private DateTimeOffset _nextRequest;
    private long _lastNotification;
    public string TasksDirectory=>Path.Combine(library.StateDirectory,"Tasks");
    public event EventHandler? Changed;
    public IReadOnlyList<OfflineDownloadTask> Tasks=>_tasks.Values.OrderByDescending(t=>t.Created).ToArray();
    public bool IsBusy=>_running is {IsCompleted:false};
    public Guid? ActiveId=>_active;
    public Task InitializeAsync(){lock(_sync)return _initialization??=Task.Run(async()=>{
        Directory.CreateDirectory(TasksDirectory);await library.InitializeAsync().ConfigureAwait(false);var anotherActive=false;try{using var check=AcquireDownloadLease();}catch(IOException){anotherActive=true;}
        foreach(var directory in Directory.EnumerateDirectories(TasksDirectory))
        {
            if(!Guid.TryParseExact(Path.GetFileName(directory),"N",out var id)||(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)continue;
            try{var path=Path.Combine(directory,"task.json");if(!File.Exists(path))continue;var task=JsonSerializer.Deserialize<OfflineDownloadTask>(await File.ReadAllTextAsync(path).ConfigureAwait(false),AtomicJson.Options);if(task is null||task.Id!=id)continue;
                if(File.Exists(Path.Combine(directory,"tiles.sqlite")))
                {
                    try{await using var db=OpenDatabase(task);await db.OpenAsync().ConfigureAwait(false);var query=db.CreateCommand();query.CommandText="SELECT COUNT(*),COALESCE(SUM(length(bytes)),0) FROM tiles WHERE state=1";await using var reader=await query.ExecuteReaderAsync().ConfigureAwait(false);await reader.ReadAsync().ConfigureAwait(false);task.CompletedTiles=reader.GetInt64(0);task.DownloadedBytes=reader.GetInt64(1);}
                    catch(SqliteException){task.State=OfflineDownloadState.Failed;task.Message="任务阶段库暂不可读取，原文件保留，请检查后重试。";}
                }
                if(task.State is OfflineDownloadState.Downloading or OfflineDownloadState.Preparing or OfflineDownloadState.Packaging){task.State=OfflineDownloadState.Paused;task.Message=anotherActive?"另一窗口正在运行下载任务，请在原窗口操作。":"上次任务已中断，点击续传即可继续。";if(!anotherActive)await SaveTask(task).ConfigureAwait(false);}
                if(task.State!=OfflineDownloadState.Completed)_tasks[id]=task;
            }catch(Exception e)when(e is IOException or JsonException or ArgumentException){/* 保留无法识别的任务目录，不自动删除。 */}
        }
        Notify(true);
    });}
    public async Task<OfflineDownloadEstimate> EstimateAsync(MapSourceDefinition source,OfflineMapRegion region,int min,int max,CancellationToken token=default)
    {
        var restriction=OfflineMapSourceIdentity.DownloadRestriction(source);if(restriction is not null)throw new ArgumentException(restriction);
        Validate(source,region,min,max);var coverage=await Task.Run(()=>OfflineMapCoverage.Count(region,min,max,token),token).ConfigureAwait(false);
        if(coverage.Total>OfflineMapCoverage.MaximumDownloadTiles)return new(coverage,checked(coverage.Total*65536),0,"范围过大，单任务最多200万张瓦片，请缩小选区或降低最大层级。");
        var bytes=new List<int>();
        foreach(var z in new[]{max,(min+max)/2,min}.Distinct())
        foreach(var address in OfflineMapCoverage.Enumerate(region,z,z,token).Take(2))
        {
            try{var tile=await fetcher.FetchAsync(source,address,token,ct=>RateAsync(250,ct)).ConfigureAwait(false);bytes.Add(tile.Bytes.Length);}catch(HttpRequestException){/* 用可取得的样本预估；全部失败则用保守默认值并明确提示。 */}
        }
        var average=bytes.Count>0?bytes.Average():65536;var estimated=(long)Math.Ceiling(coverage.Total*average*1.02)+16384;
        return new(coverage,estimated,bytes.Count,bytes.Count>0?"按少量真实瓦片抽样估算；图片内容差异会影响实际大小，另需约一倍临时空间。":"未取得有效样本，暂按每张64KB粗估；请检查图源后再下载。");
    }
    public async Task<OfflineDownloadTask> StartAsync(string name,MapSourceDefinition source,OfflineMapRegion region,int min,int max,int concurrency,int interval,CancellationToken token=default)
    {
        await InitializeAsync().ConfigureAwait(false);Validate(source,region,min,max);if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>120)throw new ArgumentException("离线地图名称不能为空，且不超过120个字符。");
        if(concurrency is <1 or >4||interval is <100 or >10000)throw new ArgumentException("并发数为1至4，请求间隔为100至10000毫秒。");
        var restriction=OfflineMapSourceIdentity.DownloadRestriction(source);if(restriction is not null)throw new ArgumentException(restriction);
        var coverage=await Task.Run(()=>OfflineMapCoverage.Count(region,min,max,token),token).ConfigureAwait(false);if(coverage.Total==0||coverage.Total>OfflineMapCoverage.MaximumDownloadTiles)throw new ArgumentException("下载范围为空或超过200万张瓦片，请缩小范围或降低层级。");
        var task=new OfflineDownloadTask{Name=name.Trim(),Source=source.DeepCopy(),Region=region,MinZoom=min,MaxZoom=max,Concurrency=concurrency,RequestIntervalMs=interval,PlannedTiles=coverage.Total,State=OfflineDownloadState.Paused};
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try{if(IsBusy)throw new InvalidOperationException("已有任务正在运行，请先暂停当前任务。");await SaveTask(task).ConfigureAwait(false);_tasks[task.Id]=task;Launch(task);}
        finally{_commands.Release();}return task;
    }
    public async Task ResumeAsync(Guid id)
    {
        await InitializeAsync().ConfigureAwait(false);await _commands.WaitAsync().ConfigureAwait(false);
        try{if(IsBusy)throw new InvalidOperationException("请先暂停正在运行的任务。");if(!_tasks.TryGetValue(id,out var task))throw new InvalidOperationException("下载任务不存在。");Validate(task.Source,task.Region,task.MinZoom,task.MaxZoom);var restriction=OfflineMapSourceIdentity.DownloadRestriction(task.Source);if(restriction is not null)throw new ArgumentException(restriction);Launch(task);}
        finally{_commands.Release();}
    }
    private void Launch(OfflineDownloadTask task){var lease=AcquireDownloadLease();_cancel?.Dispose();_cancel=new();_active=task.Id;var token=_cancel.Token;_running=Task.Run(()=>RunAsync(task,token,lease));}
    public async Task PauseAsync(){var task=_running;_cancel?.Cancel();if(task is not null)await task.ConfigureAwait(false);}
    public Task StopAsync()=>PauseAsync();
    private async Task RunAsync(OfflineDownloadTask task,CancellationToken token,FileStream lease)
    {
        using var ownership=lease;
        try
        {
            task.State=OfflineDownloadState.Preparing;task.Message="正在准备可恢复任务…";await SaveTask(task).ConfigureAwait(false);Notify(true);
            await PrepareDatabase(task,token).ConfigureAwait(false);var pending=await Pending(task,token).ConfigureAwait(false);task.State=OfflineDownloadState.Downloading;task.FailedTiles=0;await SaveTask(task).ConfigureAwait(false);Notify(true);
            var index=-1;
            async Task Worker()
            {
                while(true)
                {
                    var position=Interlocked.Increment(ref index);if(position>=pending.Count)return;token.ThrowIfCancellationRequested();var address=pending[position];OfflineTileData? tile=null;string? error=null;
                    for(var attempt=0;attempt<3;attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        try{tile=await fetcher.FetchAsync(task.Source,address,token,ct=>RateAsync(task.RequestIntervalMs,ct)).ConfigureAwait(false);break;}
                        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
                        catch(Exception e)when(e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
                        {error=e is HttpRequestException h&&h.StatusCode.HasValue?$"图源返回HTTP {(int)h.StatusCode.Value}":"网络或瓦片内容不可用";if(e is HttpRequestException status&&(int?)status.StatusCode is 401 or 403 or 404)break;if(e is HttpRequestException limited&&(int?)limited.StatusCode==429){throw new IOException("图源正在限流，任务已暂停；请稍后续传或增加请求间隔。");}await Task.Delay(500*(attempt+1),token).ConfigureAwait(false);}
                    }
                    await _database.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await using var db=OpenDatabase(task);await db.OpenAsync(token).ConfigureAwait(false);var command=db.CreateCommand();command.CommandText="UPDATE tiles SET state=$state, bytes=$bytes, type=$type WHERE id=$id";command.Parameters.AddWithValue("$state",tile is null?2:1);command.Parameters.AddWithValue("$bytes",(object?)tile?.Bytes??DBNull.Value);command.Parameters.AddWithValue("$type",tile?.TileType??0);command.Parameters.AddWithValue("$id",address.TileId);await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        if(tile is not null){task.CompletedTiles++;task.DownloadedBytes+=tile.Bytes.Length;}else{task.FailedTiles++;task.Message=error??"部分瓦片下载失败";}
                    }
                    finally{_database.Release();}Notify();
                }
            }
            async Task SupervisedWorker(){try{await Worker().ConfigureAwait(false);}catch{_cancel?.Cancel();throw;}}
            await Task.WhenAll(Enumerable.Range(0,task.Concurrency).Select(_=>SupervisedWorker())).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if(task.FailedTiles>0){task.State=OfflineDownloadState.Failed;task.Message=$"{task.FailedTiles:N0}张瓦片失败，可续传重试或保存已下载部分。";}
            else await Publish(task,false,token).ConfigureAwait(false);
        }
        catch(OperationCanceledException){task.State=OfflineDownloadState.Paused;task.Message="任务已暂停，已下载数据保留，可继续下载。";}
        catch(Exception error){task.State=OfflineDownloadState.Failed;task.Message=error is IOException?error.Message:"任务未完成，请检查磁盘空间、网络或图源后续传；已下载内容保留。";}
        finally{try{await SaveTask(task).ConfigureAwait(false);}catch(IOException){task.Message="任务已停止，但保存任务状态失败，请检查磁盘空间。";}_active=null;Notify(true);}
    }
    private async Task PrepareDatabase(OfflineDownloadTask task,CancellationToken token)
    {
        await using var db=OpenDatabase(task);await db.OpenAsync(token).ConfigureAwait(false);var create=db.CreateCommand();create.CommandText="PRAGMA journal_mode=WAL;CREATE TABLE IF NOT EXISTS tiles(id INTEGER PRIMARY KEY,z INTEGER,x INTEGER,y INTEGER,state INTEGER NOT NULL DEFAULT 0,type INTEGER NOT NULL DEFAULT 0,bytes BLOB);";await create.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var count=db.CreateCommand();count.CommandText="SELECT COUNT(*) FROM tiles";var existing=(long)(await count.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        if(existing!=task.PlannedTiles)
        {
            using var transaction=db.BeginTransaction();var insert=db.CreateCommand();insert.Transaction=transaction;insert.CommandText="INSERT OR IGNORE INTO tiles(id,z,x,y) VALUES($id,$z,$x,$y)";
            foreach(var key in new[]{"$id","$z","$x","$y"})insert.Parameters.Add(key,SqliteType.Integer);
            foreach(var tile in OfflineMapCoverage.Enumerate(task.Region,task.MinZoom,task.MaxZoom,token)){insert.Parameters[0].Value=tile.TileId;insert.Parameters[1].Value=tile.Zoom;insert.Parameters[2].Value=tile.X;insert.Parameters[3].Value=tile.Y;await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);}transaction.Commit();
        }
        var totals=db.CreateCommand();totals.CommandText="SELECT COUNT(*),COALESCE(SUM(length(bytes)),0) FROM tiles WHERE state=1";await using var reader=await totals.ExecuteReaderAsync(token).ConfigureAwait(false);await reader.ReadAsync(token).ConfigureAwait(false);task.CompletedTiles=reader.GetInt64(0);task.DownloadedBytes=reader.GetInt64(1);
    }
    private async Task<List<OfflineTileAddress>> Pending(OfflineDownloadTask task,CancellationToken token)
    {await using var db=OpenDatabase(task);await db.OpenAsync(token).ConfigureAwait(false);var query=db.CreateCommand();query.CommandText="SELECT z,x,y FROM tiles WHERE state<>1 ORDER BY id";await using var reader=await query.ExecuteReaderAsync(token).ConfigureAwait(false);var result=new List<OfflineTileAddress>();while(await reader.ReadAsync(token).ConfigureAwait(false))result.Add(new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2)));return result;}
    private async Task RateAsync(int interval,CancellationToken token)
    {
        await _rate.WaitAsync(token).ConfigureAwait(false);try{var wait=_nextRequest-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait,token).ConfigureAwait(false);_nextRequest=DateTimeOffset.UtcNow.AddMilliseconds(interval);}finally{_rate.Release();}
    }
    private SqliteConnection OpenDatabase(OfflineDownloadTask task)=>new(new SqliteConnectionStringBuilder{DataSource=Path.Combine(TaskDirectory(task.Id),"tiles.sqlite"),Pooling=false}.ToString());
    private string TaskDirectory(Guid id)=>Path.Combine(TasksDirectory,id.ToString("N"));
    private Task SaveTask(OfflineDownloadTask task)=>AtomicJson.WriteAsync(Path.Combine(TaskDirectory(task.Id),"task.json"),task);
    private void Notify(bool force=false){var now=Environment.TickCount64;if(force||now-Interlocked.Read(ref _lastNotification)>250){Interlocked.Exchange(ref _lastNotification,now);Changed?.Invoke(this,EventArgs.Empty);}}
    private static void Validate(MapSourceDefinition source,OfflineMapRegion region,int min,int max)
    {if(!new MapSourceTemplateService().Validate(source).IsValid)throw new ArgumentException("图源设置无效，请先在在线图源库修正。");if(min<source.MinZoom||max>source.MaxZoom||min>max)throw new ArgumentException("下载层级超出所选图源范围。");OfflineMapCoverage.CreateGeometry(region);}
    public async Task SavePartialAsync(Guid id)
    {
        await InitializeAsync().ConfigureAwait(false);await _commands.WaitAsync().ConfigureAwait(false);
        try{if(IsBusy)throw new InvalidOperationException("请先暂停当前下载任务。");if(!_tasks.TryGetValue(id,out var task)||task.CompletedTiles==0)throw new InvalidOperationException("没有可保存的瓦片。");var lease=AcquireDownloadLease();_active=id;_cancel=new();_running=Task.Run(async()=>{using var ownership=lease;try{await Publish(task,true,_cancel.Token).ConfigureAwait(false);}catch(OperationCanceledException){task.State=OfflineDownloadState.Paused;task.Message="生成已暂停，下载数据保留。";}catch(Exception){task.State=OfflineDownloadState.Failed;task.Message="生成失败，下载数据保留，请检查磁盘空间。";}finally{await SaveTask(task).ConfigureAwait(false);_active=null;Notify(true);}});}
        finally{_commands.Release();}
    }
    private async Task Publish(OfflineDownloadTask task,bool partial,CancellationToken token)
    {
        task.State=OfflineDownloadState.Packaging;task.Message="正在生成PMTiles文件…";await SaveTask(task).ConfigureAwait(false);Notify(true);
        var pack=new OfflineMapPack{Id=task.Id,Name=task.Name,SourceName=task.Source.Name,SourceId=task.Source.Id,SourceFingerprint=OfflineMapSourceIdentity.Fingerprint(task.Source),Attribution=task.Source.Attribution??"",Region=task.Region,MinZoom=task.MinZoom,MaxZoom=task.MaxZoom,TileCount=task.CompletedTiles,PlannedTiles=task.PlannedTiles,IsPartial=partial&&task.CompletedTiles<task.PlannedTiles,Created=DateTimeOffset.Now};
        byte type;await using(var db=OpenDatabase(task)){await db.OpenAsync(token).ConfigureAwait(false);var query=db.CreateCommand();query.CommandText="SELECT DISTINCT type FROM tiles WHERE state=1";var types=new List<byte>();await using var reader=await query.ExecuteReaderAsync(token).ConfigureAwait(false);while(await reader.ReadAsync(token).ConfigureAwait(false))types.Add((byte)reader.GetInt32(0));type=types.Count==1?types[0]:(byte)2;}
        var normalize=false;await using(var db=OpenDatabase(task)){await db.OpenAsync(token).ConfigureAwait(false);var query=db.CreateCommand();query.CommandText="SELECT COUNT(DISTINCT type) FROM tiles WHERE state=1";normalize=(long)(await query.ExecuteScalarAsync(token).ConfigureAwait(false))!>1;}
        var sourceDirectory=Path.Combine(library.PacksDirectory,SafeName(task.Source.Name));Directory.CreateDirectory(sourceDirectory);var filename=SafeName(task.Name)+"-"+SafeName(task.Source.Name)+"-"+pack.Created.ToLocalTime().ToString("yyyyMMdd-HHmmssfff")+".pmtiles";var final=Path.Combine(sourceDirectory,filename);library.EnsureManagedFile(final);var temp=Path.Combine(TaskDirectory(task.Id),"building.pmtiles.partial");
        if(File.Exists(temp))File.Delete(temp);
        if(new DriveInfo(Path.GetPathRoot(final)!).AvailableFreeSpace<task.DownloadedBytes+32*1024*1024)throw new IOException("磁盘剩余空间不足，已保留下载数据；释放空间后可续传生成。");
        async IAsyncEnumerable<(long TileId,byte[] Bytes)> Tiles([EnumeratorCancellation] CancellationToken cancellation=default)
        {await using var db=OpenDatabase(task);await db.OpenAsync(cancellation).ConfigureAwait(false);var query=db.CreateCommand();query.CommandText="SELECT id,bytes FROM tiles WHERE state=1 ORDER BY id";await using var reader=await query.ExecuteReaderAsync(cancellation).ConfigureAwait(false);while(await reader.ReadAsync(cancellation).ConfigureAwait(false)){var bytes=(byte[])reader[1];yield return(reader.GetInt64(0),normalize?fetcher.NormalizePng(bytes):bytes);}}
        try{await PmTilesArchive.WriteAsync(temp,pack,type,Tiles(token),token).ConfigureAwait(false);token.ThrowIfCancellationRequested();_ = new PmTilesArchive(temp);File.Move(temp,final,false);}
        finally{if(File.Exists(temp))File.Delete(temp);}
        task.PublishedPath=final;
        foreach(var previous in library.Packs.Where(p=>p.Id==task.Id&&p.IsPartial&&p.FilePath!=final).ToArray())
        {if(File.Exists(previous.FilePath)){library.EnsureManagedFile(previous.FilePath);File.Delete(previous.FilePath);}}
        task.State=partial?OfflineDownloadState.Paused:OfflineDownloadState.Completed;task.Message=partial?"已保存当前部分，可继续补齐；缺失瓦片按原图源加载。":"离线地图已完成，可在地图浏览时优先使用。";await SaveTask(task).ConfigureAwait(false);await library.RefreshAsync().ConfigureAwait(false);
        if(!partial){var dbPath=Path.Combine(TaskDirectory(task.Id),"tiles.sqlite");foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(dbPath+suffix))File.Delete(dbPath+suffix);}
    }
    public async Task DeleteTaskAsync(Guid id)
    {
        await InitializeAsync().ConfigureAwait(false);await _commands.WaitAsync().ConfigureAwait(false);
        try{if(_active==id)throw new InvalidOperationException("请先暂停任务。");using var lease=AcquireDownloadLease();var directory=Path.GetFullPath(TaskDirectory(id));if(!directory.StartsWith(Path.GetFullPath(TasksDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("任务目录无效。");if(Directory.Exists(directory)){if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("任务目录是重解析链接，已停止删除。");foreach(var file in Directory.EnumerateFiles(directory)){if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("任务文件是重解析链接，已停止删除。");File.Delete(file);}Directory.Delete(directory,false);}_tasks.TryRemove(id,out _);}
        finally{_commands.Release();}Notify(true);
    }
    public static string SafeName(string text)
    {var cleaned=new string(text.Trim().Select(c=>Path.GetInvalidFileNameChars().Contains(c)||char.IsControl(c)?'_':c).ToArray()).TrimEnd('.',' ');if(cleaned.Length==0)cleaned="未命名地图";var info=new StringInfo(cleaned);if(info.LengthInTextElements>35)cleaned=info.SubstringByTextElements(0,35);if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(cleaned.Split('.')[0],StringComparer.OrdinalIgnoreCase))cleaned="地图_"+cleaned;return cleaned;}
    private FileStream AcquireDownloadLease()
    {Directory.CreateDirectory(library.StateDirectory);try{return new FileStream(Path.Combine(library.StateDirectory,"download.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){throw new IOException("另一软件窗口正在下载或生成离线地图，请先暂停另一个窗口的任务。");}}
}


