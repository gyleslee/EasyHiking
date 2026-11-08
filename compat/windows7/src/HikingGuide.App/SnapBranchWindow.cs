using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HikingGuide.Core.Models;

namespace HikingGuide.App;

public sealed class SnapBranchWindow : Window
{
    public TrackPosition? Result {get;private set;}
    internal DataGrid CandidateGrid {get;}=new() {IsReadOnly=true,AutoGenerateColumns=false,SelectionMode=DataGridSelectionMode.Single,CanUserAddRows=false};
    internal Button ConfirmButton {get;}=new() {Content="吸附到选中路段",MinWidth=140,Margin=new(8,0,0,0)};
    public SnapBranchWindow(Track track,IReadOnlyList<TrackPosition> positions,double? totalDistanceMeters=null)
    {
        var totalDistance=totalDistanceMeters ?? track.GetTotalLength();
        Title="选择吸附路段（重合／折返路段）"; Width=740;Height=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel {Margin=new(16)}; Content=root;
        var hint=new TextBlock {Text=$"轨迹：{track.Name}\n同一地图位置可能经过多次。请按距起点／终点里程选择需要的去程或回程；这里只改变吸附位置，不修改轨迹。",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)};
        DockPanel.SetDock(hint,Dock.Top);root.Children.Add(hint);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};
        var cancel=new Button {Content="取消",MinWidth=90,IsCancel=true}; buttons.Children.Add(cancel);buttons.Children.Add(ConfirmButton);
        DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);
        foreach(var (header,path) in new[]{("候选路段","Number"),("距起点 km","Start"),("距终点 km","End"),("距标注 m","Offset"),("原始物理段","Part")})
            CandidateGrid.Columns.Add(new DataGridTextColumn {Header=header,Binding=new Binding(path),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        CandidateGrid.ItemsSource=positions.Select((p,i)=>new Row(p,i+1,(p.DistanceFromTrackStart/1000).ToString("F3"),((totalDistance-p.DistanceFromTrackStart)/1000).ToString("F3"),p.DistanceToTrack.ToString("F2"),p.SegmentIndex+1)).ToArray();root.Children.Add(CandidateGrid);
        ConfirmButton.Click+=(_,_)=> {if(CandidateGrid.SelectedItem is Row row){Result=row.Position;DialogResult=true;}else MessageBox.Show(this,"请先选择需要吸附的候选路段。","未选择路段");};
    }
    private sealed record Row(TrackPosition Position,int Number,string Start,string End,string Offset,int Part);
}
