using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
namespace HikingGuide.App;
public sealed class KilometerProjectRow(HikingProject project,int eligible,int enabled)
{
    public HikingProject Project {get;}=project;public bool Selected {get;set;}=true;
    public string Name=>Project.Name??"未命名项目";public int Eligible {get;}=eligible;
    public int Enabled {get;set;}=enabled;
}
public sealed class KilometerSettingsWindow:Window
{
    public KilometerSettingsWindow(IReadOnlyList<KilometerProjectRow> rows,Action<HikingProject,IEnumerable<Track>,bool> apply)
    {
        Title="已加载项目的轨迹公里数标识";Width=680;Height=420;MinWidth=560;MinHeight=320;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(14)};Content=root;
        var hint=new TextBlock{Text="勾选项目后批量显示或隐藏。仅处理符合条件的轨迹；设置保存在各自项目，新导入轨迹默认关闭。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(hint,Dock.Top);root.Children.Add(hint);
        var tools=new WrapPanel{Margin=new Thickness(0,10,0,0)};DockPanel.SetDock(tools,Dock.Bottom);root.Children.Add(tools);
        var grid=new DataGrid{AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,ItemsSource=rows,RowHeight=28};
        grid.Columns.Add(new DataGridCheckBoxColumn{Header="选择",Binding=new Binding(nameof(KilometerProjectRow.Selected)){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=55});
        foreach(var column in new[]{("项目名称","Name",300d),("可设置轨迹","Eligible",110d),("已显示轨迹","Enabled",110d)})grid.Columns.Add(new DataGridTextColumn{Header=column.Item1,Binding=new Binding(column.Item2),IsReadOnly=true,Width=column.Item3});root.Children.Add(grid);
        void Button(string title,Action action){var b=new Button{Content=title,Padding=new Thickness(12,5,12,5),Margin=new Thickness(0,0,8,0)};b.Click+=(_,_)=>action();tools.Children.Add(b);}
        Button("全选",()=>{foreach(var row in rows)row.Selected=true;grid.Items.Refresh();});Button("全不选",()=>{foreach(var row in rows)row.Selected=false;grid.Items.Refresh();});
        void Apply(bool show){grid.CommitEdit();foreach(var row in rows.Where(r=>r.Selected)){apply(row.Project,row.Project.Tracks,show);row.Enabled=row.Project.Tracks.Count(t=>TrackKilometerService.IsEnabled(t)&&new TrackKilometerService().Assess(t).Eligible);}grid.Items.Refresh();}
        Button("显示所选项目",()=>Apply(true));Button("隐藏所选项目",()=>Apply(false));Button("关闭",Close);
    }
}
