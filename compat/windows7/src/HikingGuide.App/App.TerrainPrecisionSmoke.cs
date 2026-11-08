using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Input;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunTerrainPrecisionSmokeAsync(MainWindow owner,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;Terrain3DWindow? settings=null;
        try
        {
            for(var i=0;i<300&&!main.Map.IsReady;i++)await Task.Delay(100);
            settings=new(main.Map){Owner=owner};settings.Show();await Task.Delay(150);
            for(var i=0;i<200&&settings.StatusText.Text.StartsWith("正在检查",StringComparison.Ordinal);i++)await Task.Delay(50);
            await T10ManualLayoutAsync(settings);
            async Task Submit(string text)
            {
                settings.ExaggerationInput.Text=text;
                settings.ExaggerationInput.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,Environment.TickCount,settings.ExaggerationInput,settings.Pitch){RoutedEvent=Keyboard.LostKeyboardFocusEvent});
                await Task.Delay(350);
            }
            foreach(var (input,expected) in new[]{("1.37",1.4),("1.25",1.3),("0.75",0.8),("3",3.0),("0.5",0.5)})
            {
                await Submit(input);
                checks[$"输入{input}四舍五入显示并保存{expected:F1}"]=settings.ExaggerationInput.Text==expected.ToString("F1",System.Globalization.CultureInfo.CurrentCulture)&&
                    Math.Abs(settings.Exaggeration.Value-expected)<1e-10&&GlobalSettingsService.Current.Terrain3D?.Exaggeration==expected;
            }
            await Submit("1.37");await Submit("3.1");checks["越界值不覆盖1点4"]=GlobalSettingsService.Current.Terrain3D?.Exaggeration==1.4;
            await Submit("非数字");checks["非法输入不覆盖1点4"]=GlobalSettingsService.Current.Terrain3D?.Exaggeration==1.4;
            await Submit("1.37");
            var reopened=new GlobalSettingsService(new ApplicationDataPaths(),LoggingService,MapSourceTemplateService);await reopened.LoadAsync();
            checks["重新读取设置仍为1点4"]=reopened.Current.Terrain3D?.Exaggeration==1.4;
            checks["滑块步长0点1并吸附刻度"]=settings.Exaggeration.SmallChange==.1&&settings.Exaggeration.LargeChange==.1&&settings.Exaggeration.IsSnapToTickEnabled;
            await CaptureT10ClientAsync(settings,Path.Combine(Path.GetDirectoryName(output)!,"地形夸张一位小数.png"));
            settings.Close();settings=new(main.Map){Owner=owner};settings.Show();await Task.Delay(150);
            for(var i=0;i<200&&settings.StatusText.Text.StartsWith("正在检查",StringComparison.Ordinal);i++)await Task.Delay(50);
            checks["重新打开窗口显示1点4"]=settings.ExaggerationInput.Text=="1.4";
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=owner};manager.Show();manager.LibraryTabs.SelectedIndex=1;
            if(manager.TerrainDem.Items.Count>0)manager.TerrainDem.SelectedIndex=0;
            await T10ManualLayoutAsync(manager);
            checks["保存按钮显示等高线生成设置新名称"]=FavoritesPanel.Descendants<System.Windows.Controls.Button>(manager).Any(b=>b.Content?.ToString()=="应用并保存等高线生成设置"&&b.IsEnabled);
            await CaptureT10ClientAsync(manager,Path.Combine(Path.GetDirectoryName(output)!,"等高线生成器按钮更名.png"));manager.Close();
        }
        catch(Exception e){error=e.ToString();}
        finally{settings?.Close();}
        var passed=error is null&&checks.Count>0&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        return passed;
    }
}
