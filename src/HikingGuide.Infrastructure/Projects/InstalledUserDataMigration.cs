using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace HikingGuide.Infrastructure.Projects;

/// <summary>首次采用同盘配置时复制旧用户数据；迁移完成前不启动正常业务，不删除源目录。</summary>
public static class InstalledUserDataMigration
{
    public const string ApplicationId="EasyHiking-Installed-0EE343F3-212F-4B44-ABBF-7FF0B80AB4E7";
    public const string ProfileMarker="EasyHiking.Data.ini";
    private const string Pending=".easyhiking-migration-pending.json";
    private const string Complete=".easyhiking-migration-complete.json";
    public static async Task<bool> MigrateAsync(string sourceRoot,string destinationRoot,IProgress<string>? progress=null,CancellationToken token=default)
    {
        var source=Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var target=Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if(source.Equals(target,StringComparison.OrdinalIgnoreCase))return false;
        if(source.StartsWith(target+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            target.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new IOException("旧配置与新配置目录不能互相包含。");
        if(!Directory.Exists(source))return false;
        RejectLinks(source);RejectLinks(target);
        Directory.CreateDirectory(target);
        await using var gate=new FileStream(Path.Combine(target,".easyhiking-migration.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var pending=Path.Combine(target,Pending);var complete=Path.Combine(target,Complete);
        if(File.Exists(complete))return false;
        if(File.Exists(pending))
        {
            var state=JsonSerializer.Deserialize<MigrationState>(await File.ReadAllTextAsync(pending,token));
            if(state is null||!state.Source.Equals(source,StringComparison.OrdinalIgnoreCase)||!state.Destination.Equals(target,StringComparison.OrdinalIgnoreCase))throw new IOException("未完成的配置迁移来源不一致，请先核对配置目录。");
        }
        else
        {
            var hasExisting=Directory.EnumerateFileSystemEntries(target).Any(p=>Path.GetFileName(p) is not (ProfileMarker or ".easyhiking-migration.lock"));
            if(hasExisting)return false;
            await File.WriteAllTextAsync(pending,JsonSerializer.Serialize(new MigrationState(source,target)),token);
        }
        // 整次复制期间禁止旧收藏数据库被写入，避免数据库与图片来自不同的时间点。
        var oldFavorites=Path.Combine(source,"Favorites","Favorites.sqlite");
        await using var sourceGate=File.Exists(oldFavorites)?new FileStream(oldFavorites,FileMode.Open,FileAccess.Read,FileShare.Read):null;
        async Task CopyDirectory(string from,string to)
        {
            RejectLinks(from);RejectLinks(to);Directory.CreateDirectory(to);
            foreach(var entry in Directory.EnumerateFileSystemEntries(from))
            {
                token.ThrowIfCancellationRequested();RejectLinks(entry);
                var name=Path.GetFileName(entry);
                if(name is Pending or Complete or ProfileMarker or ".easyhiking-migration.lock")continue;
                var dest=Path.Combine(to,name);RejectLinks(dest);
                progress?.Report("正在复制用户配置："+Path.GetRelativePath(source,entry));
                if(Directory.Exists(entry)){await CopyDirectory(entry,dest);continue;}
                if(name.Equals("Favorites.sqlite",StringComparison.OrdinalIgnoreCase))
                {
                    using var input=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=entry,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
                    using var output=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=dest,Pooling=false}.ToString());
                    input.Open();output.Open();input.BackupDatabase(output);
                    continue;
                }
                if(name.Equals("Favorites.sqlite-wal",StringComparison.OrdinalIgnoreCase)||name.Equals("Favorites.sqlite-shm",StringComparison.OrdinalIgnoreCase))continue;
                var lastWrite=File.GetLastWriteTimeUtc(entry);
                await using(var read=new FileStream(entry,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true))
                await using(var write=new FileStream(dest,FileMode.Create,FileAccess.Write,FileShare.None,65536,true))
                {
                    await read.CopyToAsync(write,token);await write.FlushAsync(token);
                    if(write.Length!=read.Length)throw new IOException("配置文件复制长度校验失败。");
                }
                File.SetLastWriteTimeUtc(dest,lastWrite);
            }
        }
        await CopyDirectory(source,target);
        // 只重写已知配置里的内部路径；外部项目和挂载DEM不在此范围内。
        foreach(var relative in new[]{"Data/settings.json","Data/recent-projects.json","Data/active-project.json","Data/dem-databases.json"})
        {
            var file=Path.Combine(target,relative);
            if(!File.Exists(file))continue;
            JsonNode? json;
            try{json=JsonNode.Parse(await File.ReadAllTextAsync(file,token));}catch(JsonException){continue;}
            JsonNode? Rewrite(JsonNode? node)
            {
                if(node is JsonObject obj){foreach(var item in obj.ToArray()){var updated=Rewrite(item.Value);if(!ReferenceEquals(updated,item.Value))obj[item.Key]=updated;}}
                else if(node is JsonArray array){for(var i=0;i<array.Count;i++){var updated=Rewrite(array[i]);if(!ReferenceEquals(updated,array[i]))array[i]=updated;}}
                else if(node is JsonValue val&&val.TryGetValue<string>(out var value)&&value.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    return JsonValue.Create(target+value[source.Length..]);
                return node;
            }
            Rewrite(json);await File.WriteAllTextAsync(file,json?.ToJsonString(new(){WriteIndented=true})??"null",token);
        }
        await File.WriteAllTextAsync(complete,JsonSerializer.Serialize(new MigrationState(source,target)),token);
        File.Delete(pending);
        return true;
    }
    public static void WriteProfileMarker(string root,string userSid)
    {
        Directory.CreateDirectory(root);
        File.WriteAllLines(Path.Combine(root,ProfileMarker),["[Data]","AppId="+ApplicationId,"OwnerSid="+userSid]);
    }
    private static void RejectLinks(string path)
    {
        for(string? item=Path.GetFullPath(path);!string.IsNullOrEmpty(item);item=Path.GetDirectoryName(item))
            if((File.Exists(item)||Directory.Exists(item))&&(File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("配置目录包含链接，已停止自动迁移以保护原文件："+item);
    }
    private sealed record MigrationState(string Source,string Destination);
}
