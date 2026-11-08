using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunRoadDefaultsSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        await RunMapControlsSmokeAsync(window,main,output);
        using var prior=JsonDocument.Parse(await File.ReadAllTextAsync(output));
        var checks=prior.RootElement.GetProperty("Checks").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetBoolean());
        string? error=prior.RootElement.GetProperty("Error").GetString();
        try
        {
            async Task Wait(Func<bool> ready){for(var i=0;i<300;i++){if(ready())return;await Task.Delay(50);}throw new InvalidOperationException("道路默认层级界面操作未完成");}
            MapSourceManagerWindow Open(){var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();T10ManualField<TabControl>(manager,"LibraryTabs").SelectedIndex=2;return manager;}
            var manager=Open();await T10ManualLayoutAsync(manager);var panel=T10ManualField<OsmRoadSettingsPanel>(manager,"RoadPanel");
            var grid=T10ManualField<DataGrid>(panel,"StylesGrid");var styles=(IEnumerable<OsmRoadStyle>)grid.ItemsSource;var style=styles.First(s=>s.Id=="bdca864257e1845f8563");
            checks["小径原最小层级13及机耕道细分原层级12已恢复"]=style.MinZoom==13&&styles.Any(s=>s.Id=="8fddf56db86c30856520"&&s.MinZoom==12);
            T10ManualField<TextBox>(panel,"SearchBox").Text="徒步小径";await T10ManualLayoutAsync(manager);await CaptureT10ClientAsync(manager,Path.Combine(Path.GetDirectoryName(output)!,"T187-道路原层级.png"));
            style.MinZoom=12;style.MaxZoom=21;style.Color="#123456";grid.Items.Refresh();await panel.ApplyAsync();
            checks["面板保存立即建立新的默认层级"]=GlobalSettingsService.Current.OsmRoads.DefaultZoomLevels[style.Id]==new OsmRoadZoomRange(12,21);
            T10ManualField<Button>(panel,"RestoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>GlobalSettingsService.Current.OsmRoads.Styles.First(s=>s.Id==style.Id).Color!="#123456");
            var restored=GlobalSettingsService.Current.OsmRoads.Styles.First(s=>s.Id==style.Id);checks["同一面板恢复默认保留刚保存的层级并恢复默认颜色"]=restored.MinZoom==12&&restored.MaxZoom==21;
            await Wait(()=>!T10ManualField<bool>(panel,"_busy"));manager.Close();await Wait(()=>!manager.IsVisible);manager=Open();panel=T10ManualField<OsmRoadSettingsPanel>(manager,"RoadPanel");await T10ManualLayoutAsync(manager);
            T10ManualField<Button>(panel,"RestoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>!T10ManualField<bool>(panel,"_busy"));
            restored=GlobalSettingsService.Current.OsmRoads.Styles.First(s=>s.Id==style.Id);checks["重新打开面板恢复默认仍保留新层级"]=restored.MinZoom==12&&restored.MaxZoom==21;
            await CaptureT10ClientAsync(manager,Path.Combine(Path.GetDirectoryName(output)!,"T187-保存层级作为默认.png"));manager.Close();await Wait(()=>!manager.IsVisible);
            var reload=new HikingGuide.Infrastructure.Maps.GlobalSettingsService(new HikingGuide.Infrastructure.Projects.ApplicationDataPaths(),LoggingService,MapSourceTemplateService);
            await reload.LoadAsync();restored=reload.Current.OsmRoads.Styles.First(s=>s.Id==style.Id);checks["重读磁盘配置后默认层级仍为12至21"]=restored.MinZoom==12&&restored.MaxZoom==21&&reload.Current.OsmRoads.DefaultZoomLevels[style.Id]==new OsmRoadZoomRange(12,21);
        }
        catch(Exception e){error=(error is null?"":error+"\n")+e;foreach(var manager in Windows.OfType<MapSourceManagerWindow>().ToArray())manager.Close();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}
