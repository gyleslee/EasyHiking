using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.Core.Favorites;
using HikingGuide.Infrastructure.Favorites;
using HikingGuide.Infrastructure.Media;
using HikingGuide.App.Services;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunFavoriteMapFilesSmokeAsync(MainWindow owner,string output)
    {
        var folder=Path.GetDirectoryName(output)!;var checks=new Dictionary<string,bool>();string? error=null;
        try
        {
            var now=DateTimeOffset.UtcNow;var a=await Favorites.SaveCollectionAsync(new(Guid.NewGuid(),"山峰收藏",1,now,now));var b=await Favorites.SaveCollectionAsync(new(Guid.NewGuid(),"营地收藏",2,now,now));
            byte[] png;using(var image=GetResourceStream(new Uri("pack://application:,,,/Assets/AppLogo.png"))!.Stream){using var stream=new MemoryStream();await image.CopyToAsync(stream);png=stream.ToArray();}
            var p=new FavoritePoint{Name="观景台",Description="中文收藏示例，含本地图片。",Latitude=23.1,Longitude=113.3,ElevationMeters=123.4,CollectionId=a.Id};
            var q=new FavoritePoint{Name="营地",Description="仅为操作演示",Latitude=23.2,Longitude=113.4,CollectionId=b.Id};
            await Favorites.SaveAsync(p,[new(png,"示例.png","image/png",".png",1280,1280,"图片说明")]);await Favorites.SaveAsync(q);
            var importDialog=FavoritesPanel.CreateImportFileDialog();
            checks["导入选择器允许多选并包含四种格式"]=importDialog.Multiselect&&new[]{"*.hgfav","*.kmz","*.gpx","*.kml"}.All(importDialog.Filter.Contains);
            foreach(var format in new[]{FavoriteMapFileFormat.Gpx,FavoriteMapFileFormat.Kml})
            {
                var file=Path.Combine(folder,"收藏点演示."+format.ToString().ToLowerInvariant());
                var result=await FavoriteMapFileTransfer.ExportAsync(Favorites,file,format,collectionIds:[a.Id,b.Id]);
                var target=new FavoritesRepository(new FavoritesPathProvider(Path.Combine(folder,format.ToString())));await target.InitializeAsync();
                var imported=await FavoriteMapFileTransfer.ImportAsync(target,file,imageValidator:new WpfImageDecodeValidator());
                checks[format+"真实解码图片导入成功"]=result.PointCount==2&&imported.Imported==2&&(await target.GetAsync(p.Id))!.Blobs.Count==1;
            }
            var kmz=Path.Combine(folder,"收藏点演示.kmz");var hgfav=Path.Combine(folder,"收藏点演示.hgfav");await Favorites.ExportKmzAsync(kmz);await Favorites.ExportAsync(hgfav);
            var bad=Path.Combine(folder,"错误.gpx");await File.WriteAllTextAsync(bad,"<gpx><wpt lat='999' lon='113'/></gpx>");
            var batchTarget=new FavoritesRepository(new FavoritesPathProvider(Path.Combine(folder,"BatchTarget")));await batchTarget.InitializeAsync();
            var batch=await FavoriteBatchImporter.ImportAsync(batchTarget,[Path.Combine(folder,"收藏点演示.gpx"),bad,Path.Combine(folder,"收藏点演示.kml"),kmz,hgfav],imageValidator:new WpfImageDecodeValidator());
            checks["四格式批量导入与文件间去重"]=batch.SucceededFiles==4&&batch.Imported==2&&batch.Skipped==6;
            checks["错误文件不阻止后续文件"]=batch.FailedFiles==1&&batch.Files[^1].Succeeded;
            var panel=new FavoritesPanel(true);var manager=new Window{Owner=owner,Title="收藏点管理器",Width=1100,Height=650,Content=panel,WindowStartupLocation=WindowStartupLocation.CenterOwner};manager.Show();await panel.RefreshAsync();await T10ManualLayoutAsync(manager);
            await panel.ImportFilesForAutomationAsync([Path.Combine(folder,"收藏点演示.gpx"),bad,Path.Combine(folder,"收藏点演示.kml"),kmz,hgfav],FavoriteConflictPolicy.Skip);
            checks["真实进度窗口完成批量流程并保留文件错误"]=panel.LastImportForAutomation is{SucceededFiles:4,FailedFiles:1,Skipped:8};
            var labels=FavoritesPanel.Descendants<Button>(manager).Select(x=>x.Content?.ToString()).ToArray();
            checks["管理器三个地图格式入口齐全"]=new[]{"导出为KMZ…","导出为GPX…","导出为KML…"}.All(labels.Contains);
            await CaptureT10ClientAsync(manager,Path.Combine(folder,"16-favorites-manager.png"));manager.Close();
            var menu=FavoritesPanel.Descendants<Menu>(owner).Single().Items.OfType<MenuItem>().Single(m=>m.Header?.ToString()=="收藏点");
            checks["主菜单GPX与KML入口齐全"]=new[]{"导出为GPX…","导出为KML…"}.All(label=>menu.Items.OfType<MenuItem>().Any(m=>m.Header?.ToString()==label));
            var export=new FavoriteExportWindow(await Favorites.CollectionsAsync(),await Favorites.SearchAsync(),[],null){Owner=owner};export.Show();export.SelectForAutomation(FavoriteExportScope.Collections,a.Id,b.Id);await T10ManualLayoutAsync(export);
            checks["多收藏夹导出范围保留"]=export.Selection!.CollectionIds.Count==2;await CaptureT10ClientAsync(export,Path.Combine(folder,"favorites-export-selection.png"));export.Close();
            var about=new AboutWindow{Owner=owner};about.Show();await T10ManualLayoutAsync(about);await CaptureT10ClientAsync(about,Path.Combine(folder,"29-about.png"));about.Close();
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(x=>x);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}
