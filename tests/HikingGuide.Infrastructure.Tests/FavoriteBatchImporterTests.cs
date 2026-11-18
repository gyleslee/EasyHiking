using HikingGuide.Core.Favorites;
using HikingGuide.Infrastructure.Favorites;

namespace HikingGuide.Infrastructure.Tests;
public sealed class FavoriteBatchImporterTests:IDisposable
{
    private readonly string root=Path.Combine("F:\\Codex\\Temp","FavoriteBatch",Guid.NewGuid().ToString("N"));
    private async Task<FavoritesRepository> Repo(string name){var r=new FavoritesRepository(new FavoritesPathProvider(Path.Combine(root,name)));await r.InitializeAsync();return r;}
    private async Task<string> Gpx(string name,string xml){var file=Path.Combine(root,name+".gpx");await File.WriteAllTextAsync(file,xml);return file;}
    [Fact]public async Task MixedFormatsContinueAfterBadFileAndDoNotImportSamePathTwice()
    {
        var source=await Repo("source");await source.SaveAsync(new(){Name="共享身份点",Latitude=23,Longitude=113});
        var gpx=Path.Combine(root,"points.gpx");var kml=Path.Combine(root,"points.kml");var kmz=Path.Combine(root,"points.kmz");var hgfav=Path.Combine(root,"points.hgfav");
        await FavoriteMapFileTransfer.ExportAsync(source,gpx,FavoriteMapFileFormat.Gpx);await FavoriteMapFileTransfer.ExportAsync(source,kml,FavoriteMapFileFormat.Kml);await source.ExportKmzAsync(kmz);await source.ExportAsync(hgfav);
        var bad=await Gpx("错误","<gpx><wpt lat='23' lon='113'><name>不应半导入</name></wpt><wpt lat='999' lon='113'/></gpx>");
        var target=await Repo("target");var reports=new List<FavoriteBatchImportProgress>();
        var result=await FavoriteBatchImporter.ImportAsync(target,[gpx,bad,kml,kmz,hgfav,gpx],progress:new InlineProgress(reports.Add));
        Assert.Equal(5,result.FilesTotal);Assert.Equal(4,result.SucceededFiles);Assert.Equal(1,result.FailedFiles);Assert.Equal(1,result.Imported);Assert.Equal(3,result.Skipped);Assert.False(result.Cancelled);
        Assert.Contains(result.Files,f=>f.FilePath==bad&&!f.Succeeded&&!string.IsNullOrWhiteSpace(f.Error));
        Assert.Single(await target.SearchAsync());Assert.Equal(5,reports[^1].FilesProcessed);Assert.Equal("共享身份点",(await target.SearchAsync()).Single().Name);
    }
    [Theory][InlineData(FavoriteConflictPolicy.Skip,"第一份",1)][InlineData(FavoriteConflictPolicy.Overwrite,"第二份",1)][InlineData(FavoriteConflictPolicy.KeepBoth,"第二份",2)]
    public async Task OneConflictPolicyAppliesAcrossDifferentFiles(FavoriteConflictPolicy policy,string expectedDescription,int expectedCount)
    {
        var source=await Repo("source");var point=new FavoritePoint{Name="同身份",Description="第一份",Latitude=23,Longitude=113};await source.SaveAsync(point);
        var first=Path.Combine(root,"first.gpx");await FavoriteMapFileTransfer.ExportAsync(source,first,FavoriteMapFileFormat.Gpx);
        await source.SaveAsync(point with{Description="第二份"});var second=Path.Combine(root,"second.kml");await FavoriteMapFileTransfer.ExportAsync(source,second,FavoriteMapFileFormat.Kml);
        var target=await Repo("target");var result=await FavoriteBatchImporter.ImportAsync(target,[first,second],policy);
        var points=await target.SearchAsync();Assert.Equal(expectedCount,points.Count);Assert.Contains(points,p=>p.Description==expectedDescription);Assert.Equal(2,result.SucceededFiles);Assert.Equal(0,result.FailedFiles);
    }
    [Fact]public async Task CancellationKeepsCompletedFilesAndDoesNotStartRemainingFiles()
    {
        var target=await Repo("target");var files=new List<string>();
        for(var i=0;i<3;i++)files.Add(await Gpx("文件"+i,$"<gpx><wpt lat='23' lon='113'><name>点{i}</name></wpt></gpx>"));
        using var cancellation=new CancellationTokenSource();
        var result=await FavoriteBatchImporter.ImportAsync(target,files,progress:new InlineProgress(p=>{if(p.FilesProcessed==1)cancellation.Cancel();}),token:cancellation.Token);
        Assert.True(result.Cancelled);Assert.Single(result.Files);Assert.Equal(1,result.Imported);Assert.Equal("点0",(await target.SearchAsync()).Single().Name);
    }
    [Fact]public async Task MissingImageWarningIdentifiesItsFileWhileNextFileStillImports()
    {
        var target=await Repo("target");var file=Path.Combine(root,"missing.kml");await File.WriteAllTextAsync(file,"<kml><Placemark><name>缺图</name><description><![CDATA[<img src='missing.png'/>]]></description><Point><coordinates>113,23</coordinates></Point></Placemark></kml>");
        var next=await Gpx("next","<gpx><wpt lat='24' lon='114'><name>另一个点</name></wpt></gpx>");
        var result=await FavoriteBatchImporter.ImportAsync(target,[file,next]);Assert.Equal(2,result.Imported);Assert.Equal(file,result.Files[0].FilePath);Assert.NotEmpty(result.Files[0].Warnings);Assert.True(result.Files[1].Succeeded);
    }
    private sealed class InlineProgress(Action<FavoriteBatchImportProgress> report):IProgress<FavoriteBatchImportProgress>{public void Report(FavoriteBatchImportProgress value)=>report(value);}
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
