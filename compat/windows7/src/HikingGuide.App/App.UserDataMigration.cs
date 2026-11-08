using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.App;

public partial class App
{
    private Window? _dataMigrationWindow;
    private async Task<bool> PrepareInstalledUserDataAsync()
    {
        if(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIKINGGUIDE_APPDATA_DIR"))||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIKINGGUIDE_FUNCTION_DATA_ROOT"))||
            !ApplicationDataPaths.UsesInstallationDrive(AppContext.BaseDirectory))return true;
        var root=ApplicationDataPaths.DefaultFunctionRootDirectory(AppContext.BaseDirectory);
        var old=ApplicationDataPaths.PreviousInstalledUserRoot(AppContext.BaseDirectory);
        if(!Directory.Exists(old))old=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EasyHiking");
        using var stop=new CancellationTokenSource();var finished=false;
        try
        {
            if(!root.Equals(old,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(old)&&
                !File.Exists(Path.Combine(root,".easyhiking-migration-complete.json")))
            {
                var text=new TextBlock{Text="正在准备用户配置…",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)};
                var cancel=new Button{Content="取消启动",HorizontalAlignment=HorizontalAlignment.Right,Padding=new Thickness(14,5,14,5)};
                cancel.Click+=(_,_)=>{stop.Cancel();cancel.IsEnabled=false;};
                var panel=new StackPanel{Margin=new Thickness(22)};
                panel.Children.Add(new TextBlock{Text="首次切换为安装盘配置目录",FontSize=17,FontWeight=FontWeights.SemiBold});
                panel.Children.Add(new TextBlock{Text="目标目录："+root,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
                panel.Children.Add(text);panel.Children.Add(new ProgressBar{IsIndeterminate=true,Height=8,Margin=new Thickness(0,0,0,18)});panel.Children.Add(cancel);
                _dataMigrationWindow=new Window{Title="迁移用户配置｜易行徒步规划制作器",Content=panel,Width=640,Height=290,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterScreen};
                _dataMigrationWindow.Closing+=(_,e)=>{if(!finished){e.Cancel=true;stop.Cancel();cancel.IsEnabled=false;}};
                _dataMigrationWindow.Show();
                var progress=new Progress<string>(message=>text.Text=message);
                await Task.Run(()=>InstalledUserDataMigration.MigrateAsync(old,root,progress,stop.Token));
                finished=true;text.Text="配置准备完成，正在启动主界面…";cancel.IsEnabled=false;
            }
            InstalledUserDataMigration.WriteProfileMarker(root,WindowsIdentity.GetCurrent().User?.Value??"");
            return true;
        }
        catch(OperationCanceledException)
        {
            finished=true;_dataMigrationWindow?.Close();Shutdown(0);return false;
        }
        catch(Exception error)
        {
            finished=true;
            MessageBox.Show(_dataMigrationWindow,"用户配置目录尚未准备完成，旧配置已保留。\n\n目标："+root+"\n\n"+error.Message+"\n\n请关闭其它软件实例并检查目录权限后重试。","用户配置初始化失败",MessageBoxButton.OK,MessageBoxImage.Warning);
            _dataMigrationWindow?.Close();Shutdown(1);return false;
        }
    }
}
