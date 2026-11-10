using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Application.Maps;
namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunOsmPolicySmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;
        try
        {
            GlobalSettingsService.Current.MapSources=[new(){Name="OpenStreetMap",UrlTemplate="https://tile.openstreetmap.org/{z}/{x}/{y}.png"},new(){Name="用户自建OSM服务器",UrlTemplate="https://example.invalid/{z}/{x}/{y}.png"}];
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();T10ManualField<TabControl>(manager,"LibraryTabs").SelectedIndex=3;
            var panel=T10ManualField<OfflineMapDownloadPanel>(manager,"OfflinePanel");panel.Initialize(GlobalSettingsService);await T10ManualLayoutAsync(manager);
            var sources=((IEnumerable<MapSourceDefinition>)T10ManualField<ComboBox>(panel,"SourceBox").ItemsSource).ToArray();
            checks["离线下载列表排除官方OSM"]=sources.All(s=>OfflineMapSourceIdentity.DownloadRestriction(s) is null)&&sources.Length==1;
            checks["其他图源仍可选择"]=sources.Single().Name=="用户自建OSM服务器";
            checks["官方OSM仍保留为在线图源"]=GlobalSettingsService.Current.MapSources.Any(s=>s.Name=="OpenStreetMap"&&s.Enabled);
            checks["版本保持V1.9.1"]=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion=="V1.9.1";
            GlobalSettingsService.Current.MapSources=[GlobalSettingsService.Current.MapSources.First()];panel.Initialize(GlobalSettingsService);await T10ManualLayoutAsync(manager);
            checks["仅官方OSM时不能估算或开始下载"]=T10ManualField<ComboBox>(panel,"SourceBox").Items.Count==0&&!T10ManualField<Button>(panel,"EstimateButton").IsEnabled&&!T10ManualField<Button>(panel,"StartButton").IsEnabled;
            await CaptureT10ClientAsync(manager,Path.Combine(Path.GetDirectoryName(output)!,"OSM离线下载限制.png"));manager.Close();
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}


