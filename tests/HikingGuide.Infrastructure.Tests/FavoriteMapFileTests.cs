using System.Xml.Linq;
using HikingGuide.Core.Favorites;
using HikingGuide.Infrastructure.Favorites;

namespace HikingGuide.Infrastructure.Tests;
public sealed class FavoriteMapFileTests:IDisposable
{
    private readonly string root=Path.Combine("F:\\Codex\\Temp","FavoriteMapFiles",Guid.NewGuid().ToString("N"));
    private async Task<FavoritesRepository> Repo(string name){var r=new FavoritesRepository(new FavoritesPathProvider(Path.Combine(root,name)));await r.InitializeAsync();return r;}
    private static FavoriteMediaInput Photo=>new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII="),"中文照片.png","image/png",".png",1,1,"图片说明 <&>","https://example.com/original.png");
    [Theory][InlineData(FavoriteMapFileFormat.Gpx)][InlineData(FavoriteMapFileFormat.Kml)]
    public async Task Roundtrip_MultipleCollectionsLocalAndOnlinePhotosMissingAndZeroElevation(FavoriteMapFileFormat format)
    {
        var source=await Repo("source");var now=DateTimeOffset.UtcNow;var folder=await source.SaveCollectionAsync(new(Guid.NewGuid(),"山峰收藏夹",1,now,now));
        var point=new FavoritePoint{Name="中文地点 <&>",Description="多行\n说明 <div>原文</div> & 符号",Latitude=23.1,Longitude=113.2,CollectionId=folder.Id,ElevationMeters=0,SourceReference="https://example.com/source"};
        await source.SaveAsync(point,[Photo]);var detail=(await source.GetAsync(point.Id))!;
        var online=new FavoriteMediaAttachment(Guid.NewGuid(),point.Id,null,"在线图片","网络说明",1,now,"https://example.com/photo?id=1&v=2",true);
        await source.SaveAsync(point,retained:detail.Attachments.Concat([online]).ToArray());
        var other=new FavoritePoint{Name="无海拔",Latitude=-12,Longitude=-33};await source.SaveAsync(other);
        var file=Path.Combine(root,"中文#导出%."+format.ToString().ToLowerInvariant());var result=await FavoriteMapFileTransfer.ExportAsync(source,file,format);
        Assert.Equal(2,result.PointCount);Assert.Equal(1,result.ImageCount);Assert.Single(Directory.GetDirectories(root,"中文#导出%_图片_*"));
        var target=await Repo("target");var imported=await FavoriteMapFileTransfer.ImportAsync(target,file);Assert.Equal(2,imported.Imported);
        var restored=(await target.GetAsync(point.Id))!;Assert.Equal(point.Name,restored.Point.Name);Assert.Equal(point.Description,restored.Point.Description);Assert.Equal(0,restored.Point.ElevationMeters);
        Assert.Null((await target.GetAsync(other.Id))!.Point.ElevationMeters);
        Assert.Equal("山峰收藏夹",(await target.CollectionsAsync()).Single(c=>c.Id==restored.Point.CollectionId).Name);
        Assert.Single(restored.Blobs);Assert.Equal(Photo.Bytes,await target.ReadOriginalAsync(restored.Blobs[0]));
        Assert.Equal(Photo.Caption,restored.Attachments.Single(a=>a.MediaBlobId.HasValue).Caption);
        Assert.Equal(online.ExternalUrl,restored.Attachments.Single(a=>a.OnlineOnly).ExternalUrl);Assert.Equal(2,restored.Attachments.Count);
        Assert.Equal(2,(await FavoriteMapFileTransfer.ImportAsync(target,file)).Skipped);
        Assert.Equal(2,(await FavoriteMapFileTransfer.ImportAsync(target,file,FavoriteConflictPolicy.KeepBoth)).Imported);Assert.Equal(4,(await target.SearchAsync()).Count);
    }
    [Theory][InlineData(FavoriteMapFileFormat.Gpx)][InlineData(FavoriteMapFileFormat.Kml)]
    public async Task SelectionAndCancellationLeaveExistingOutputAndSourceIntact(FavoriteMapFileFormat format)
    {
        var repo=await Repo("source");var p=new FavoritePoint{Name="选中",Latitude=23,Longitude=113};var p2=p with{Id=Guid.NewGuid(),Name="不选"};await repo.SaveAsync(p,[Photo]);await repo.SaveAsync(p2);
        var file=Path.Combine(root,"selected."+format.ToString().ToLowerInvariant());await FavoriteMapFileTransfer.ExportAsync(repo,file,format,[p.Id]);
        var original=await File.ReadAllBytesAsync(file);var dirs=Directory.GetDirectories(root).Order().ToArray();
        using var cts=new CancellationTokenSource();cts.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>FavoriteMapFileTransfer.ExportAsync(repo,file,format,overwrite:true,token:cts.Token));
        Assert.Equal(original,await File.ReadAllBytesAsync(file));Assert.Equal(dirs,Directory.GetDirectories(root).Order().ToArray());
        var target=await Repo("target");Assert.Equal(1,(await FavoriteMapFileTransfer.ImportAsync(target,file)).Imported);Assert.Single(await target.SearchAsync());Assert.Equal(2,(await repo.SearchAsync()).Count);
    }
    [Fact]public async Task StandardGpxReadsOnlyWaypointsAndPreservesTextAndSignedElevation()
    {
        var repo=await Repo("target");var file=Path.Combine(root,"外来航点.gpx");
        await File.WriteAllTextAsync(file,"<gpx version='1.0' xmlns='http://www.topografix.com/GPX/1/0'><wpt lat='-12.3' lon='-45.6'><ele>-10</ele><name>中文点</name><desc>描述 &amp; 正文</desc></wpt><trk><trkseg><trkpt lat='23' lon='113'/></trkseg></trk></gpx>");
        Assert.Equal(1,(await FavoriteMapFileTransfer.ImportAsync(repo,file)).Imported);var p=Assert.Single(await repo.SearchAsync());Assert.Equal(-10,p.ElevationMeters);Assert.Equal("描述 & 正文",p.Description);
    }
    [Fact]public async Task StandardKmlReadsPointFoldersAndIgnoresLines()
    {
        var repo=await Repo("target");var file=Path.Combine(root,"地图.kml");await File.WriteAllTextAsync(file,"<kml xmlns='http://www.opengis.net/kml/2.2'><Document><Folder><name>露营</name><Placemark><name>营地</name><Point><coordinates>113,23</coordinates></Point></Placemark><Placemark><LineString><coordinates>113,23 114,24</coordinates></LineString></Placemark></Folder></Document></kml>");
        Assert.Equal(1,(await FavoriteMapFileTransfer.ImportAsync(repo,file)).Imported);var p=Assert.Single(await repo.SearchAsync());Assert.Null(p.ElevationMeters);Assert.Equal("露营",(await repo.CollectionsAsync()).Single(c=>c.Id==p.CollectionId).Name);
    }
    [Fact]public async Task MalformedXmlAndInvalidCoordinatesDoNotPartiallyImport()
    {
        var repo=await Repo("target");var file=Path.Combine(root,"invalid.gpx");await File.WriteAllTextAsync(file,"<gpx><wpt lat='23' lon='113'><name>正常</name></wpt><wpt lat='999' lon='113'/></gpx>");
        await Assert.ThrowsAnyAsync<Exception>(()=>FavoriteMapFileTransfer.ImportAsync(repo,file));Assert.Empty(await repo.SearchAsync());
        await File.WriteAllTextAsync(file,"<!DOCTYPE gpx [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><gpx><wpt lat='23' lon='113'><name>&x;</name></wpt></gpx>");
        await Assert.ThrowsAnyAsync<Exception>(()=>FavoriteMapFileTransfer.ImportAsync(repo,file));Assert.Empty(await repo.SearchAsync());
    }
    [Theory][InlineData(FavoriteMapFileFormat.Gpx)][InlineData(FavoriteMapFileFormat.Kml)]
    public async Task MultipleSelectedCollectionsAndOverwriteKeepPhotoReferencesValid(FavoriteMapFileFormat format)
    {
        var repo=await Repo("source");var now=DateTimeOffset.UtcNow;var folders=new List<FavoriteCollection>();
        for(var i=0;i<3;i++){var f=await repo.SaveCollectionAsync(new(Guid.NewGuid(),"收藏夹"+i,i,now,now));folders.Add(f);await repo.SaveAsync(new(){Name="点"+i,CollectionId=f.Id,Latitude=23+i,Longitude=113},[Photo]);}
        var file=Path.Combine(root,"multiple."+format.ToString().ToLowerInvariant());
        await FavoriteMapFileTransfer.ExportAsync(repo,file,format,collectionIds:[folders[0].Id,folders[1].Id]);
        await FavoriteMapFileTransfer.ExportAsync(repo,file,format,collectionIds:[folders[0].Id,folders[1].Id],overwrite:true);
        var target=await Repo("target");Assert.Equal(2,(await FavoriteMapFileTransfer.ImportAsync(target,file)).Imported);
        foreach(var p in await target.SearchAsync())Assert.Single((await target.GetAsync(p.Id))!.Blobs);
        Assert.DoesNotContain(await target.SearchAsync(),p=>p.Name=="点2");
    }
    [Fact]public async Task MissingAndOutsidePhotosAreReportedWithoutReadingOutsideFolder()
    {
        var repo=await Repo("target");var folder=Path.Combine(root,"input");Directory.CreateDirectory(folder);await File.WriteAllBytesAsync(Path.Combine(root,"outside.png"),Photo.Bytes);
        var file=Path.Combine(folder,"points.kml");await File.WriteAllTextAsync(file,"<kml xmlns='http://www.opengis.net/kml/2.2'><Document><Placemark><name>点</name><description><![CDATA[<img src='../outside.png'/><img src='missing.png'/>]]></description><Point><coordinates>113,23</coordinates></Point></Placemark></Document></kml>");
        var result=await FavoriteMapFileTransfer.ImportAsync(repo,file);Assert.Equal(1,result.Imported);Assert.NotEmpty(result.Warnings);Assert.Empty((await repo.GetAsync((await repo.SearchAsync()).Single().Id))!.Attachments);
        await File.WriteAllBytesAsync(Path.Combine(folder,"photo.png"),Photo.Bytes);
        await File.WriteAllTextAsync(file,"<kml xmlns='http://www.opengis.net/kml/2.2'><Placemark><name>相对图片</name><description><![CDATA[<img src='./photo.png'/>]]></description><Point><coordinates>113,23</coordinates></Point></Placemark></kml>");
        await FavoriteMapFileTransfer.ImportAsync(repo,file);var imported=(await repo.SearchAsync()).Single(p=>p.Name=="相对图片");Assert.Single((await repo.GetAsync(imported.Id))!.Blobs);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
