using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
namespace HikingGuide.App;
public sealed class KilometerDirectionRow(TrackSegment segment,bool reversed):INotifyPropertyChanged
{
    private bool _reversed=reversed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid Id=>segment.Id;
    public string Name=>segment.Name??"未命名轨迹段";
    public bool Reversed {get=>_reversed;set{_reversed=value;PropertyChanged?.Invoke(this,new(null));}}
    public GeoCoordinate StartCoordinate=>(Reversed?segment.EndPoint!:segment.StartPoint!).Coordinate;
    public GeoCoordinate EndCoordinate=>(Reversed?segment.StartPoint!:segment.EndPoint!).Coordinate;
    public string Start=>$"{StartCoordinate.Latitude:F6}, {StartCoordinate.Longitude:F6}";
    public string End=>$"{EndCoordinate.Latitude:F6}, {EndCoordinate.Longitude:F6}";
}
public sealed class KilometerDirectionWindow:Window
{
    public IReadOnlyList<KilometerRouteDirections.Part>? Result {get;private set;}
    public KilometerDirectionWindow(Track track,Action<GeoCoordinate>? focus=null)
    {
        Title="设置公里标识起终方向";Width=820;Height=430;MinWidth=700;MinHeight=340;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var original=KilometerRouteDirections.Read(track)??KilometerRouteDirections.Suggest(track);var byId=track.Segments.ToDictionary(s=>s.Id);var rows=original.Select(p=>new KilometerDirectionRow(byId[p.SegmentId],p.Reversed)).ToList();
        var root=new DockPanel{Margin=new Thickness(14)};Content=root;
        var hint=new TextBlock{Text=$"轨迹：{track.Name}\n逐段核对起点、终点，可勾选反向。第一段起点为计程起点，最后一段终点为计程终点，间隙按直线计入。只用于公里标识，不改变原轨迹。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(hint,Dock.Top);root.Children.Add(hint);
        var buttons=new WrapPanel{Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);
        var grid=new DataGrid{ItemsSource=rows,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,RowHeight=28};root.Children.Add(grid);
        grid.Columns.Add(new DataGridTextColumn{Header="轨迹段",Binding=new Binding("Name"),IsReadOnly=true,Width=200});
        grid.Columns.Add(new DataGridCheckBoxColumn{Header="反向",Binding=new Binding("Reversed"){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=55});
        grid.Columns.Add(new DataGridTextColumn{Header="起点（纬度，经度）",Binding=new Binding("Start"),IsReadOnly=true,Width=230});grid.Columns.Add(new DataGridTextColumn{Header="终点（纬度，经度）",Binding=new Binding("End"),IsReadOnly=true,Width=230});
        void Add(string name,Action action){var b=new Button{Content=name,Margin=new Thickness(0,0,8,0),Padding=new Thickness(10,5,10,5)};b.Click+=(_,_)=>action();buttons.Children.Add(b);}
        Add("定位所选段起点",()=>{if(grid.SelectedItem is KilometerDirectionRow row)focus?.Invoke(row.StartCoordinate);});Add("定位所选段终点",()=>{if(grid.SelectedItem is KilometerDirectionRow row)focus?.Invoke(row.EndCoordinate);});
        Add("交换整体起终点",()=>{rows.Reverse();foreach(var row in rows)row.Reversed=!row.Reversed;grid.Items.Refresh();});
        Add("确认",()=>{grid.CommitEdit();Result=rows.Select(r=>new KilometerRouteDirections.Part(r.Id,r.Reversed)).ToArray();DialogResult=true;});Add("取消",()=>DialogResult=false);
    }
}
