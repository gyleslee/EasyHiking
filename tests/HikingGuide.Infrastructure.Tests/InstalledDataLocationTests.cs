using System.Text.Json;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Data.Sqlite;

namespace HikingGuide.Infrastructure.Tests;

public sealed class InstalledDataLocationTests
{
    [Theory]
    [InlineData(@"D:\Program Files\EasyHiking",@"C:\Users\Gyles\AppData\Local",@"D:\Users\Gyles\AppData\Local\EasyHiking")]
    [InlineData(@"E:\软件\易行",@"D:\Users\张三\AppData\Local",@"E:\Users\张三\AppData\Local\EasyHiking")]
    public void OnlyDriveChangesAndUserProfileStructureIsPreserved(string app,string local,string expected)
        =>Assert.Equal(expected,ApplicationDataPaths.InstallationDriveUserRoot(app,local));

    [Fact]
    public async Task MigrationCopiesHumanContentAndSqliteAndOnlyRelocatesInternalPaths()
    {
        using var temp=new Temp();var source=Path.Combine(temp.Root,"old");var target=Path.Combine(temp.Root,"new");
        Directory.CreateDirectory(Path.Combine(source,"Data"));Directory.CreateDirectory(Path.Combine(source,"Favorites"));
        var text="人工填写的行程备注，不能丢失";await File.WriteAllTextAsync(Path.Combine(source,"Favorites","图片说明.txt"),text);
        var originalSettings=JsonSerializer.Serialize(new{cache=Path.Combine(source,"Data","MapTileCache"),dem=@"F:\原始DEM\N23E113.hgt"});
        await File.WriteAllTextAsync(Path.Combine(source,"Data","settings.json"),originalSettings);
        using(var db=new SqliteConnection("Data Source="+Path.Combine(source,"Favorites","Favorites.sqlite")+";Pooling=False")){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="CREATE TABLE Notes(Text TEXT);INSERT INTO Notes VALUES ('人工收藏说明');";cmd.ExecuteNonQuery();}
        Assert.True(await InstalledUserDataMigration.MigrateAsync(source,target));
        Assert.Equal(text,await File.ReadAllTextAsync(Path.Combine(target,"Favorites","图片说明.txt")));
        Assert.Equal(originalSettings,await File.ReadAllTextAsync(Path.Combine(source,"Data","settings.json")));
        var settings=JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(target,"Data","settings.json")));
        Assert.Equal(Path.Combine(target,"Data","MapTileCache"),settings.GetProperty("cache").GetString());
        Assert.Equal(@"F:\原始DEM\N23E113.hgt",settings.GetProperty("dem").GetString());
        using(var db=new SqliteConnection("Data Source="+Path.Combine(target,"Favorites","Favorites.sqlite"))){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT Text FROM Notes";Assert.Equal("人工收藏说明",cmd.ExecuteScalar());}
        Assert.False(await InstalledUserDataMigration.MigrateAsync(source,target));
    }
    [Fact]
    public async Task ExistingDestinationWinsWithoutOverwritingUserChanges()
    {
        using var temp=new Temp();var source=Path.Combine(temp.Root,"old");var target=Path.Combine(temp.Root,"new");
        Directory.CreateDirectory(source);Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(source,"settings.json"),"旧数据");await File.WriteAllTextAsync(Path.Combine(target,"settings.json"),"新目录的人工数据");
        Assert.False(await InstalledUserDataMigration.MigrateAsync(source,target));
        Assert.Equal("新目录的人工数据",await File.ReadAllTextAsync(Path.Combine(target,"settings.json")));
    }
    [Fact]
    public async Task CancelledMigrationCanResumeWithoutDeletingSource()
    {
        using var temp=new Temp();var source=Path.Combine(temp.Root,"old");var target=Path.Combine(temp.Root,"new");
        Directory.CreateDirectory(source);await File.WriteAllTextAsync(Path.Combine(source,"notes.txt"),"原始内容");
        using var stop=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>InstalledUserDataMigration.MigrateAsync(source,target,new CancelProgress(stop),stop.Token));
        Assert.True(File.Exists(Path.Combine(source,"notes.txt")));
        Assert.True(await InstalledUserDataMigration.MigrateAsync(source,target));
        Assert.Equal("原始内容",await File.ReadAllTextAsync(Path.Combine(target,"notes.txt")));
    }
    private sealed class CancelProgress(CancellationTokenSource stop):IProgress<string>{public void Report(string value)=>stop.Cancel();}
    private sealed class Temp:IDisposable
    {
        public string Root{get;}=Path.Combine(@"F:\Codex\Temp","HikingGuide-install-data-"+Guid.NewGuid().ToString("N"));
        public Temp()=>Directory.CreateDirectory(Root);
        public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(Root,true);}
    }
}
