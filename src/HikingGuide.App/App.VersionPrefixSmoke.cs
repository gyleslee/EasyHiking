using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using HikingGuide.Core.Software;
using HikingGuide.Application.Maps;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunVersionPrefixSmokeAsync(MainWindow window,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;
        try
        {
            var number=typeof(SoftwareIdentity).Assembly.GetName().Version!.ToString(3);
            var display="V"+number;
            checks["软件版本前缀统一为V"]=SoftwareIdentity.DisplayVersion==display;
            checks["主窗口标题显示V版本"]=window.Title.StartsWith(SoftwareIdentity.DisplayName+" "+display,StringComparison.Ordinal);
            foreach(var assembly in new[]{typeof(App).Assembly,typeof(SoftwareIdentity).Assembly,typeof(MapProtocol).Assembly,typeof(ProjectFileService).Assembly})
            {
                checks[assembly.GetName().Name+"显示元数据为V版本"]=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]==display;
                checks[assembly.GetName().Name+"数字版本未变"]=assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version==number+".0";
            }
            var about=new AboutWindow{Owner=window};about.Show();await T10ManualLayoutAsync(about);
            checks["关于窗口标题显示V版本"]=about.Title=="关于｜"+SoftwareIdentity.DisplayTitle;
            checks["关于窗口版本文字显示V版本"]=about.VersionLabel.Text=="软件版本："+display;
            await CaptureT10ClientAsync(about,Path.Combine(Path.GetDirectoryName(output)!,"29-about.png"));about.Close();
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=SoftwareIdentity.DisplayVersion,Title=window.Title,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true}));
        return passed;
    }
}
