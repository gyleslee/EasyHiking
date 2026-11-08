using System.Text.Json;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Maps;
namespace HikingGuide.Infrastructure.Maps;

/// <summary>永久离线包独立于可淘汰瓦片缓存。只有同图源指纹与实际存在的瓦片可以命中。</summary>
public sealed class OfflineMapLibrary(string root,string? stateDirectory=null)
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private PmTilesArchive[] _archives=[];
    private Dictionary<Guid,bool> _preferences=[];
    private Task? _initialization;
    private long _hits;
    public long ReadHits=>Interlocked.Read(ref _hits);
    private readonly object _sync=new();
    public string RootDirectory=>Path.GetFullPath(root);
    public string PacksDirectory=>RootDirectory;
    public string StateDirectory=>Path.GetFullPath(stateDirectory??Path.Combine(RootDirectory,".管理数据"));
    public string? Warning {get;private set;}
    public event EventHandler? Changed;
    public IReadOnlyList<OfflineMapPack> Packs=>Volatile.Read(ref _archives).Select(a=>a.Pack).ToArray();
    public Task InitializeAsync(){lock(_sync)return _initialization??=RefreshAsync();}
    public async Task RefreshAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(()=>{
                Directory.CreateDirectory(PacksDirectory);Warning=null;
                var prefs=Path.Combine(StateDirectory,"preferences.json");
                if(File.Exists(prefs))try{_preferences=JsonSerializer.Deserialize<Dictionary<Guid,bool>>(File.ReadAllText(prefs))??[];}catch(Exception e)when(e is IOException or JsonException){Warning="离线地图显示设置不可读，已暂用默认显示；原文件保留。";}
                var archives=new List<PmTilesArchive>();var invalid=0;
                var directories=new[]{PacksDirectory}.Concat(Directory.EnumerateDirectories(PacksDirectory).Where(d=>Path.GetFullPath(d)!=StateDirectory&&(File.GetAttributes(d)&FileAttributes.ReparsePoint)==0));
                foreach(var path in directories.SelectMany(d=>Directory.EnumerateFiles(d,"*.pmtiles")))
                {try{var archive=new PmTilesArchive(path);archive.Pack.Enabled=!_preferences.TryGetValue(archive.Pack.Id,out var enabled)||enabled;archives.Add(archive);}catch(Exception e)when(e is IOException or InvalidDataException or JsonException or ArgumentException or KeyNotFoundException or OverflowException){invalid++;}}
                _archives=archives.OrderByDescending(a=>a.Pack.Created).ToArray();if(invalid>0)Warning=$"有 {invalid} 个离线包损坏或不是本软件生成的栅格包，未启用；原文件保留。";
            }).ConfigureAwait(false);
        }
        finally{_gate.Release();}
        lock(_sync)_initialization=Task.CompletedTask;
        Changed?.Invoke(this,EventArgs.Empty);
    }
    public async Task<OfflineTileData?> ReadTileAsync(string fingerprint,OfflineTileAddress address,CancellationToken token=default)
    {
        try{await InitializeAsync().ConfigureAwait(false);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){Warning="离线地图库暂不可读取，当前使用原图源；请检查离线目录权限。";return null;}
        foreach(var archive in Volatile.Read(ref _archives).Where(a=>a.Pack.Enabled&&a.Pack.SourceFingerprint==fingerprint&&address.Zoom>=a.Pack.MinZoom&&address.Zoom<=a.Pack.MaxZoom))
        {
            try{var bytes=await archive.ReadTileAsync(address,token).ConfigureAwait(false);if(bytes is not null){Interlocked.Increment(ref _hits);return new(bytes,archive.ContentType=="image/png"?(byte)2:archive.ContentType=="image/jpeg"?(byte)3:(byte)4,archive.ContentType);}}
            catch(Exception e)when(e is IOException or InvalidDataException or OverflowException or ArgumentException or UnauthorizedAccessException){Warning="部分离线地图不可读，已尝试其他离线包或原图源，请检查离线包文件。";}
        }
        return null;
    }
    public async Task SetEnabledAsync(Guid id,bool enabled)
    {
        await InitializeAsync().ConfigureAwait(false);await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var next=new Dictionary<Guid,bool>(_preferences){[id]=enabled};Directory.CreateDirectory(StateDirectory);
            await AtomicJson.WriteAsync(Path.Combine(StateDirectory,"preferences.json"),next).ConfigureAwait(false);_preferences=next;
            foreach(var archive in _archives.Where(a=>a.Pack.Id==id))archive.Pack.Enabled=enabled;
        }
        finally{_gate.Release();}Changed?.Invoke(this,EventArgs.Empty);
    }
    public async Task DeleteAsync(Guid id)
    {
        await InitializeAsync().ConfigureAwait(false);await _gate.WaitAsync().ConfigureAwait(false);
        try{foreach(var archive in _archives.Where(a=>a.Pack.Id==id)){EnsureManagedFile(archive.Pack.FilePath);File.Delete(archive.Pack.FilePath);}_archives=_archives.Where(a=>a.Pack.Id!=id).ToArray();}
        finally{_gate.Release();}Changed?.Invoke(this,EventArgs.Empty);
    }
    public void EnsureManagedFile(string path)
    {
        var full=Path.GetFullPath(path);var parent=Path.GetDirectoryName(full)!;
        if(!full.StartsWith(PacksDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||(parent!=PacksDirectory&&Path.GetDirectoryName(parent)!=PacksDirectory)||Path.GetExtension(full)!=".pmtiles")throw new InvalidOperationException("拒绝操作离线地图库以外的文件。");
        if((File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)||(File.GetAttributes(PacksDirectory)&FileAttributes.ReparsePoint)!=0||(Directory.Exists(parent)&&(File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0))throw new IOException("离线目录或文件为重解析链接，已停止文件操作。");
    }
}
internal static class AtomicJson
{
    internal static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web){WriteIndented=true};
    internal static async Task WriteAsync<T>(string path,T value)
    {
        var temp=path+".tmp";Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,16384,true)){await JsonSerializer.SerializeAsync(stream,value,Options).ConfigureAwait(false);await stream.FlushAsync().ConfigureAwait(false);stream.Flush(true);}
        File.Move(temp,path,true);
    }
}
