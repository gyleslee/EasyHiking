using System.IO.Compression;
using System.Text;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
namespace HikingGuide.Infrastructure.Tests;
public sealed class OfflineMapsTests:IDisposable
{
    [Fact] public async Task OfficialOsmCannotEstimateStartOrResumeAndMakesNoTileRequests()
    {
        var library=new OfflineMapLibrary(_root);var fetcher=new Fetcher();var downloads=new OfflineMapDownloads(library,fetcher);
        var source=new MapSourceDefinition{Name="改名不绕过限制",UrlTemplate="https://tile.openstreetmap.org/{z}/{x}/{y}.png"};
        await Assert.ThrowsAsync<ArgumentException>(()=>downloads.EstimateAsync(source,Region,13,13));
        await Assert.ThrowsAsync<ArgumentException>(()=>downloads.StartAsync("禁止任务",source,Region,13,13,1,100));
        Assert.Empty(downloads.Tasks);
        var task=new OfflineDownloadTask{Source=source,Region=Region,Name="已有未完成任务",MinZoom=13,MaxZoom=13,State=OfflineDownloadState.Paused};
        var dir=Path.Combine(downloads.TasksDirectory,task.Id.ToString("N"));Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir,"task.json"),System.Text.Json.JsonSerializer.Serialize(task));
        var restarted=new OfflineMapDownloads(library,fetcher);await restarted.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>restarted.ResumeAsync(task.Id));Assert.Equal(0,fetcher.Calls);Assert.False(restarted.IsBusy);
        Assert.True(File.Exists(Path.Combine(dir,"task.json")));
    }
    [Theory][InlineData(8435,8874,"95.1%")][InlineData(0,0,"0.0%")][InlineData(10,10,"100.0%")][InlineData(11,10,"100.0%")]
    public void DownloadProgressIncludesBoundedPercentage(long completed,long planned,string expected)
    {var task=new OfflineDownloadTask{CompletedTiles=completed,PlannedTiles=planned};Assert.Contains(expected,task.ProgressText);Assert.Contains("失败",task.ProgressText);}
    private readonly string _root=Path.Combine(Path.GetTempPath(),"HikingGuide-OfflineTests-"+Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6QAAAABJRU5ErkJggg==");
    private static OfflineMapRegion Region=>new([new(113.30,24.40),new(113.34,24.40),new(113.34,24.44),new(113.30,24.44)]);
    private static MapSourceDefinition Source=>new(){Name="测试图源",UrlTemplate="https://example.invalid/{z}/{x}/{y}.png",Headers=new(){["Authorization"]="private-test-secret"}};
    private sealed class Fetcher:IOfflineMapTileFetcher
    {
        public int Delay;public bool Fail;public int Calls;public Dictionary<long,int> PerTile=[];
        public async Task<OfflineTileData> FetchAsync(MapSourceDefinition source,OfflineTileAddress address,CancellationToken token,Func<CancellationToken,Task>? beforeNetwork=null)
        {Interlocked.Increment(ref Calls);lock(PerTile){PerTile.TryGetValue(address.TileId,out var n);PerTile[address.TileId]=n+1;}if(Delay>0)await Task.Delay(Delay,token);if(Fail&&address.TileId%3==0)throw new HttpRequestException("失败",null,System.Net.HttpStatusCode.NotFound);return new(Png,2,"image/png");}
        public byte[] NormalizePng(byte[] bytes)=>bytes;
    }
    private static async Task Wait(OfflineMapDownloads downloads){for(var i=0;i<3000&&downloads.IsBusy;i++)await Task.Delay(10);var timeout=downloads.IsBusy;if(timeout)await downloads.StopAsync();Assert.False(timeout,"后台任务未在测试时限内完成："+string.Join("；",downloads.Tasks.Select(t=>t.StateText+" "+t.ProgressText+" "+t.Message)));}
    [Fact] public async Task PmTilesSupportsLeafDirectoriesAndExactTileReads()
    {
        Directory.CreateDirectory(_root);var path=Path.Combine(_root,"many.pmtiles");var addresses=Enumerable.Range(0,8).SelectMany(z=>Enumerable.Range(0,1<<z).SelectMany(x=>Enumerable.Range(0,1<<z).Select(y=>new OfflineTileAddress(z,x,y)))).OrderBy(t=>t.TileId).Take(8192).ToArray();
        async IAsyncEnumerable<(long,byte[])> Tiles(){foreach(var address in addresses){yield return(address.TileId,Png);await Task.Yield();}}
        var pack=new OfflineMapPack{Name="多叶目录",SourceName="图源",SourceFingerprint=new string('A',64),Region=Region,MinZoom=0,MaxZoom=7,TileCount=addresses.Length};await PmTilesArchive.WriteAsync(path,pack,2,Tiles());
        var reader=new PmTilesArchive(path);Assert.Equal(Png,await reader.ReadTileAsync(addresses[0]));Assert.Equal(Png,await reader.ReadTileAsync(addresses[4096]));Assert.Equal(Png,await reader.ReadTileAsync(addresses[^1]));Assert.Null(await reader.ReadTileAsync(new(8,0,0)));
        var bytes=await File.ReadAllBytesAsync(path);Assert.Equal("PMTiles",Encoding.ASCII.GetString(bytes,0,7));Assert.Equal(3,bytes[7]);Assert.True(BitConverter.ToUInt64(bytes,8)+BitConverter.ToUInt64(bytes,16)<=16384);Assert.True(BitConverter.ToUInt64(bytes,48)>0);
        using var metadata=new GZipStream(new MemoryStream(bytes,(int)BitConverter.ToUInt64(bytes,24),(int)BitConverter.ToUInt64(bytes,32)),CompressionMode.Decompress);using var text=new StreamReader(metadata);Assert.Contains("easyhiking",await text.ReadToEndAsync());
    }
    [Fact] public async Task PauseRestartAndResumeKeepCompletedTilesAndPublishValidArchive()
    {
        var library=new OfflineMapLibrary(_root);var fetcher=new Fetcher{Delay=30};var downloads=new OfflineMapDownloads(library,fetcher);var source=Source;
        var task=await downloads.StartAsync("我的离线地图",source,Region,13,15,1,100);for(var i=0;i<200&&task.CompletedTiles<3;i++)await Task.Delay(10);await downloads.PauseAsync();var count=task.CompletedTiles;Assert.True(count>0);Assert.True(count<task.PlannedTiles);
        var prior=new Dictionary<long,int>(fetcher.PerTile);var restarted=new OfflineMapDownloads(library,fetcher);await restarted.InitializeAsync();Assert.Equal(count,Assert.Single(restarted.Tasks).CompletedTiles);fetcher.Delay=0;await restarted.ResumeAsync(task.Id);await Wait(restarted);
        var pack=Assert.Single(library.Packs);Assert.False(pack.IsPartial);Assert.Equal(task.PlannedTiles,pack.TileCount);Assert.Contains("我的离线地图-测试图源-",Path.GetFileName(pack.FilePath));Assert.Equal(OfflineMapSourceIdentity.Fingerprint(source),pack.SourceFingerprint);
        Assert.Equal("测试图源",Path.GetFileName(Path.GetDirectoryName(pack.FilePath)));Assert.DoesNotContain(Directory.EnumerateFiles(_root),p=>Path.GetExtension(p)==".sqlite");
        foreach(var pair in prior.Where(p=>p.Value==1).Take((int)count))Assert.True(fetcher.PerTile[pair.Key]<=2); // an in-flight canceled request may retry; completed ones are retained.
        var address=OfflineMapCoverage.Enumerate(Region,13,15).First();Assert.NotNull(await library.ReadTileAsync(pack.SourceFingerprint,address));
        Assert.Null(await library.ReadTileAsync(pack.SourceFingerprint,new(16,address.X*8,address.Y*8)));
        await library.SetEnabledAsync(pack.Id,false);Assert.Null(await library.ReadTileAsync(pack.SourceFingerprint,address));var reload=new OfflineMapLibrary(_root);await reload.InitializeAsync();Assert.False(Assert.Single(reload.Packs).Enabled);
        await library.SetEnabledAsync(pack.Id,true);Assert.NotNull(await library.ReadTileAsync(pack.SourceFingerprint,address));source.CoordinateSystem=MapSourceCoordinateSystem.Gcj02;Assert.Null(await library.ReadTileAsync(OfflineMapSourceIdentity.Fingerprint(source),address));
        var file=await File.ReadAllBytesAsync(pack.FilePath);using var metadata=new GZipStream(new MemoryStream(file,(int)BitConverter.ToUInt64(file,24),(int)BitConverter.ToUInt64(file,32)),CompressionMode.Decompress);using var text=new StreamReader(metadata);Assert.DoesNotContain("private-test-secret",await text.ReadToEndAsync());
    }
    [Fact] public async Task FailuresRemainExplicitPartialCanBeReadAndRetriedToCompletion()
    {
        var library=new OfflineMapLibrary(_root);var fetcher=new Fetcher{Fail=true};var downloads=new OfflineMapDownloads(library,fetcher);var task=await downloads.StartAsync("部分下载",Source,Region,13,14,2,100);await Wait(downloads);Assert.Equal(OfflineDownloadState.Failed,task.State);Assert.Empty(library.Packs);Assert.True(task.FailedTiles>0);
        await downloads.SavePartialAsync(task.Id);await Wait(downloads);var partial=Assert.Single(library.Packs);Assert.True(partial.IsPartial);Assert.True(partial.TileCount<partial.PlannedTiles);
        fetcher.Fail=false;await downloads.ResumeAsync(task.Id);await Wait(downloads);Assert.False(Assert.Single(library.Packs).IsPartial);Assert.Equal(OfflineDownloadState.Completed,task.State);Assert.False(File.Exists(partial.FilePath));
    }
    [Fact] public async Task CancelledPackagingNeverPublishesAndDeletionLeavesOtherFilesAlone()
    {
        Directory.CreateDirectory(_root);var marker=Path.Combine(_root,"keep.txt");await File.WriteAllTextAsync(marker,"保留");var path=Path.Combine(_root,"cancel.partial");using var cancel=new CancellationTokenSource();
        async IAsyncEnumerable<(long,byte[])> Tiles(){yield return(1,Png);cancel.Cancel();await Task.Yield();yield return(2,Png);}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>PmTilesArchive.WriteAsync(path,new(){SourceFingerprint=new string('B',64),Region=Region},2,Tiles(),cancel.Token));Assert.False(File.Exists(path));Assert.Equal("保留",await File.ReadAllTextAsync(marker));
    }
    [Fact] public void IdentityIgnoresDisplaySettingsButKeepsCoordinateAndAccessConfiguration()
    {
        var source=Source;var original=OfflineMapSourceIdentity.Fingerprint(source);source.Name="改名";source.Opacity=.2;source.CacheCapacityMb=12;Assert.Equal(original,OfflineMapSourceIdentity.Fingerprint(source));source.Headers["Authorization"]="changed";Assert.NotEqual(original,OfflineMapSourceIdentity.Fingerprint(source));
        Assert.NotNull(OfflineMapSourceIdentity.DownloadRestriction(new(){UrlTemplate="https://tile.openstreetmap.org/{z}/{x}/{y}.png"}));Assert.DoesNotContain(':',OfflineMapDownloads.SafeName("测试:地图"));Assert.StartsWith("地图_",OfflineMapDownloads.SafeName("CON"));
    }
    [Fact] public async Task CorruptPackageIsSkippedAndDoesNotPreventOtherFilesOrOnlineFallback()
    {Directory.CreateDirectory(_root);await File.WriteAllBytesAsync(Path.Combine(_root,"bad.pmtiles"),[1,2,3]);var library=new OfflineMapLibrary(_root);await library.InitializeAsync();Assert.Empty(library.Packs);Assert.NotNull(library.Warning);Assert.Null(await library.ReadTileAsync(new string('F',64),new(10,1,1)));Assert.True(File.Exists(Path.Combine(_root,"bad.pmtiles")));}
    [Fact] public void PortableAndConfiguredInstalledRootsKeepOfflineFilesOutsideDataSubfolder()
    {var function=Path.Combine(_root,"用户资料");Assert.Equal(Path.Combine(function,"离线地图"),HikingGuide.Infrastructure.Projects.ApplicationDataPaths.OfflineMapsDirectory(Path.Combine(_root,"Program Files","EasyHiking"),function));Assert.Equal(Path.Combine(_root,"Portable","离线地图"),HikingGuide.Infrastructure.Projects.ApplicationDataPaths.OfflineMapsDirectory(Path.Combine(_root,"Portable")));}
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}

