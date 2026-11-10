using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunT192SmokeAsync(MainWindow window,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            Track Make(string name,double x)=>new(){Name=name,Segments=Enumerable.Range(0,2).Select(i=>new TrackSegment{Name="分段"+(i+1),SplitSectionId=Guid.NewGuid(),Points=[new(){Latitude=23,Longitude=x+i*.01},new(){Latitude=23,Longitude=x+i*.01+.001}]}).ToList()};
            var project=new HikingProject();var a=Make("山脊东线",113);var b=Make("山脊西线",114);var c=Make("下撤支线",115);var main=Make("主攻略保护示例",116);
            foreach(var t in new[]{a,b,c,main}){t.RecalculateDistances();project.AddTrack(t,false);}project.MainGuideTrackId=main.Id;
            object[] Rows(TrackMergeWindow d)=>d.TrackList.Items.Cast<object>().ToArray();
            object Source(object row)=>row.GetType().GetProperty("Source")!.GetValue(row)!;
            string Order(object row)=>(string)row.GetType().GetProperty("OrderText")!.GetValue(row)!;
            foreach(var connect in new[]{false,true})
            {
                TrackMergeWindow Dialog(IReadOnlyList<TrackMergeSource>? selected)=>new(project,connect,geometrySelection:selected){Owner=window};
                var empty=Dialog(null);empty.Show();await T10ManualLayoutAsync(empty);var allCount=Rows(empty).Length;
                var allName=FavoritesPanel.Descendants<TextBlock>(empty.TrackList).First(t=>t.Text=="山脊东线");
                var allReverse=FavoritesPanel.Descendants<CheckBox>(empty.TrackList).First(t=>Equals(t.Content,"反向"));
                var allGap=allReverse.TranslatePoint(new Point(),empty.TrackList).X-allName.TranslatePoint(new Point(allName.ActualWidth,0),empty.TrackList).X;
                checks[(connect?"连接":"合并")+"完整列表长短名称均紧接反向"]=allGap is >=0 and <=15;
                checks[(connect?"连接":"合并")+"未多选时列出全部普通轨迹与分段"]=allCount==9;empty.Close();
                var single=Dialog([new(a.Id,false)]);checks[(connect?"连接":"合并")+"单选仍列出全部候选"]=Rows(single).Length==allCount;single.Close();
                var normal=new TrackMergeWindow(project,connect,geometrySelection:[new(a.Id,false),new(b.Id,false)],restrictToSelection:false);
                checks[(connect?"连接":"合并")+"非编辑模式保留完整列表"]=Rows(normal).Length==allCount;normal.Close();
                var two=Dialog([new(a.Id,false),new(b.Id,false)]);two.Show();await T10ManualLayoutAsync(two);
                checks[(connect?"连接":"合并")+"多选整轨只列所选且不附带分段"]=Rows(two).Length==2&&Rows(two).All(r=>Source(r) is Track);
                checks[(connect?"连接":"合并")+"顺序框显隐正确"]=FavoritesPanel.Descendants<TextBox>(two.TrackList).Count(t=>t.IsVisible)==(connect?2:0);
                checks[(connect?"连接":"合并")+"标题完整且顺序标题显隐正确"]=two.IncludeHeader.Text==(connect?"是否连接":"是否合并")&&two.OrderHeader.Visibility==(connect?Visibility.Visible:Visibility.Collapsed)&&FavoritesPanel.Descendants<TextBlock>(two).Any(t=>t.Text=="轨迹名称")&&FavoritesPanel.Descendants<TextBlock>(two).Any(t=>t.Text=="是否反向");
                var nameText=FavoritesPanel.Descendants<TextBlock>(two.TrackList).First(t=>t.Text=="山脊东线");
                var reverse=FavoritesPanel.Descendants<CheckBox>(two.TrackList).First(t=>Equals(t.Content,"反向"));
                var gap=reverse.TranslatePoint(new Point(),two.TrackList).X-nameText.TranslatePoint(new Point(nameText.ActualWidth,0),two.TrackList).X;
                checks[(connect?"连接":"合并")+"反向紧接轨迹名称"]=gap is >=0 and <=45;
                if(!connect)await CaptureT10ClientAsync(two,Path.Combine(folder,"合并轨迹.png"));two.Close();
                var section=a.GetSplitSections()[1];var mixed=Dialog([new(a.Id,false,section.Id),new(b.Id,false)]);
                checks[(connect?"连接":"合并")+"整轨分段混选仅列所选两项"]=Rows(mixed).Length==2&&Rows(mixed).Any(r=>Source(r) is SplitSection)&&Rows(mixed).Any(r=>Source(r) is Track);mixed.Close();
                var split=Dialog(a.GetSplitSections().Select(s=>new TrackMergeSource(a.Id,false,s.Id)).ToArray());
                checks[(connect?"连接":"合并")+"仅选分段不附带父轨迹"]=Rows(split).Length==2&&Rows(split).All(r=>Source(r) is SplitSection);split.Close();
            }
            var dialog=new TrackMergeWindow(project,true,geometrySelection:[new(a.Id,false),new(b.Id,false),new(c.Id,false)]){Owner=window};dialog.Show();await T10ManualLayoutAsync(dialog);
            checks["进入面板时连接顺序全部留空"]=Rows(dialog).All(r=>Order(r)=="");
            dialog.TrackList.SelectedIndex=0;T10ManualInvoke(dialog,"MoveSelected",1);T10ManualInvoke(dialog,"MoveSelected",-1);
            checks["空白时上下移动不会补号"]=Rows(dialog).All(r=>Order(r)=="");
            await CaptureT10ClientAsync(dialog,Path.Combine(folder,"连接轨迹-留空.png"));
            bool TryCreateWarning()
            {
                // 原生MessageBox不保证调度WPF计时器；使用独立线程观察并关闭本进程的测试警告。
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var completed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var watcher=new Thread(()=>
                {
                    for(var attempt=0;attempt<200&&!stop.IsCancellationRequested;attempt++)
                    {
                        var handle=T192Native.FindWindow(null,"连接顺序有误");
                        if(handle!=IntPtr.Zero){T192Native.GetWindowThreadProcessId(handle,out var pid);if(pid==Environment.ProcessId){T192Native.PostMessage(handle,0x10,IntPtr.Zero,IntPtr.Zero);completed.TrySetResult(true);return;}}
                        Thread.Sleep(50);
                    }
                    completed.TrySetResult(false);
                }){IsBackground=true};watcher.Start();
                try{dialog.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
                finally{stop.Cancel();}
                return completed.Task.GetAwaiter().GetResult()&&dialog.IsVisible;
            }
            checks["未填写顺序会弹警告并阻止创建"]=TryCreateWarning();
            var boxes=FavoritesPanel.Descendants<TextBox>(dialog.TrackList).Where(t=>t.IsVisible).ToArray();
            for(var i=0;i<3;i++){boxes[i].Text=new[]{"3","1","2"}[i];boxes[i].GetBindingExpression(TextBox.TextProperty)!.UpdateSource();}
            checks["实际顺序输入驱动最终来源顺序"]=dialog.SelectedSources.Select(s=>s.TrackId).SequenceEqual(new[]{b.Id,c.Id,a.Id});
            checks["连接预览使用填写顺序"]=dialog.PreviewText.Text.IndexOf("山脊西线 → 下撤支线",StringComparison.Ordinal)<dialog.PreviewText.Text.IndexOf("下撤支线 → 山脊东线",StringComparison.Ordinal);
            var third=Rows(dialog)[2];third.GetType().GetProperty("IsIncluded")!.SetValue(third,false);third.GetType().GetProperty("IsIncluded")!.SetValue(third,true);
            checks["切换勾选不会生成或改写手填顺序"]=Rows(dialog).Select(Order).SequenceEqual(new[]{"3","1","2"});
            await CaptureT10ClientAsync(dialog,Path.Combine(folder,"连接轨迹.png"));
            boxes[0].Text="1";boxes[0].GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            checks["错误编号弹出真实警告并保留编辑窗口"]=TryCreateWarning()&&dialog.PreviewText.Text.Contains("不能重复");dialog.Close();
            var dem=DemCatalog.Databases.FirstOrDefault(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            if(dem is not null){GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{DemDatabaseId=dem.Id};GlobalSettingsService.Current.Terrain3D=(GlobalSettingsService.Current.Terrain3D??new()) with{TerrainDemDatabaseId=dem.Id};}
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();T10ManualField<TabControl>(manager,"LibraryTabs").SelectedIndex=1;await T10ManualLayoutAsync(manager);
            checks["新用户地形阴影默认为30%"]=manager.TerrainOpacity.Value==30;
            await CaptureT10ClientAsync(manager,Path.Combine(folder,"等高线生成器.png"));manager.Close();
            var terrain=new Terrain3DWindow(((HikingGuide.App.ViewModels.MainViewModel)window.DataContext).Map){Owner=window};terrain.Show();
            for(var i=0;i<160&&T10ManualField<bool>(terrain,"_loading");i++)await Task.Delay(250);
            checks["3D状态回显30%阴影"]=!T10ManualField<bool>(terrain,"_loading")&&terrain.ShadeOpacity.Value==30;
            await T10ManualLayoutAsync(terrain);await CaptureT10ClientAsync(terrain,Path.Combine(folder,"3D地图设置.png"));terrain.Close();
            GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{HillshadeOpacity=.2};await GlobalSettingsService.SaveAsync();await GlobalSettingsService.LoadAsync();
            checks["既有20%自定义阴影不被覆盖"]=GlobalSettingsService.Current.OfflineTerrain.HillshadeOpacity==.2;
            checks["主攻略与原始几何未被编辑"]=project.MainGuideTrackId==main.Id&&project.Tracks.Count==4&&project.Tracks.All(t=>t.Segments.Count==2);
            var about=new AboutWindow{Owner=window};about.Show();await T10ManualLayoutAsync(about);checks["版本为T192"]=about.VersionLabel.Text=="软件版本：V1.9.2";await CaptureT10ClientAsync(about,Path.Combine(folder,"关于.png"));about.Close();
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
    private static class T192Native
    {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern IntPtr FindWindow(string? className,string title);
        [DllImport("user32.dll")]internal static extern uint GetWindowThreadProcessId(IntPtr window,out int processId);
        [DllImport("user32.dll")]internal static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    }
}
