using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HikingGuide.Core.Favorites;
namespace HikingGuide.App;

public sealed class FavoriteExportWindow:Window
{
    public sealed class Choice:INotifyPropertyChanged
    {
        public Guid Id{get;init;}public string Name{get;init;}="";public string Detail{get;init;}="";
        private bool _selected;public bool Selected{get=>_selected;set{_selected=value;PropertyChanged?.Invoke(this,new(nameof(Selected)));}}
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly TabControl _tabs=new();private readonly TextBox _search=new(){MinWidth=160};
    private readonly TextBlock _summary=new(){Margin=new Thickness(0,8,0,8),TextWrapping=TextWrapping.Wrap};
    private readonly Choice[] _collections,_points;private readonly DataGrid _collectionGrid=new(),_pointGrid=new();private bool _bulk;
    public FavoriteExportSelection? Selection{get;private set;}
    public FavoriteExportWindow(IReadOnlyList<FavoriteCollection> collections,IReadOnlyList<FavoritePoint> points,IReadOnlyList<Guid> selected,Guid? currentCollection)
    {
        Title="选择收藏点导出范围";Width=760;Height=600;MinWidth=560;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var collectionMap=collections.ToDictionary(c=>c.Id,c=>c.Name);var counts=points.GroupBy(p=>p.CollectionId).ToDictionary(g=>g.Key,g=>g.Count());var chosen=selected.ToHashSet();
        _collections=collections.Select(c=>new Choice{Id=c.Id,Name=c.Name,Detail=$"{counts.GetValueOrDefault(c.Id)} 个收藏点",Selected=c.Id==currentCollection}).ToArray();
        _points=points.Select(p=>new Choice{Id=p.Id,Name=p.Name,Detail=collectionMap.GetValueOrDefault(p.CollectionId,"未分类"),Selected=chosen.Contains(p.Id)}).ToArray();
        var root=new DockPanel{Margin=new Thickness(14)};Content=root;
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);bottom.Children.Add(_summary);var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};bottom.Children.Add(buttons);
        buttons.Children.Add(FavoriteEditorWindow.Button("继续导出",()=>{_collectionGrid.CommitEdit();_pointGrid.CommitEdit();Selection=BuildSelection();Selection.Validate();DialogResult=true;return Task.CompletedTask;}));buttons.Children.Add(FavoriteEditorWindow.Button("取消",()=>{Close();return Task.CompletedTask;}));
        var tools=new WrapPanel{Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);tools.Children.Add(new TextBlock{Text="搜索：",VerticalAlignment=VerticalAlignment.Center});tools.Children.Add(_search);
        tools.Children.Add(FavoriteEditorWindow.Button("全选当前列表",()=>{ToggleAll(true);return Task.CompletedTask;}));tools.Children.Add(FavoriteEditorWindow.Button("清空当前列表",()=>{ToggleAll(false);return Task.CompletedTask;}));
        _tabs.Items.Add(new TabItem{Header="全部收藏库",Content=new TextBlock{Text=$"导出全部 {collections.Count} 个收藏夹、{points.Count} 个收藏点及其完整本地图片。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(16)}});
        Configure(_collectionGrid,_collections,"收藏夹名称","内容");Configure(_pointGrid,_points,"收藏点名称","所属收藏夹");
        _tabs.Items.Add(new TabItem{Header="多选收藏夹",Content=_collectionGrid});_tabs.Items.Add(new TabItem{Header="多选收藏点",Content=_pointGrid});root.Children.Add(_tabs);
        foreach(var row in _collections.Concat(_points))row.PropertyChanged+=(_,_)=>{if(!_bulk)Summary();};
        _search.TextChanged+=(_,_)=>{CollectionViewSource.GetDefaultView(_collectionGrid.ItemsSource).Refresh();CollectionViewSource.GetDefaultView(_pointGrid.ItemsSource).Refresh();};
        _tabs.SelectionChanged+=(_,e)=>{if(e.Source==_tabs){_search.IsEnabled=_tabs.SelectedIndex!=0;Summary();}};
        _tabs.SelectedIndex=selected.Count>0?2:currentCollection.HasValue?1:0;Summary();
    }
    private void Configure(DataGrid grid,Choice[] rows,string title,string detail){grid.AutoGenerateColumns=false;grid.CanUserAddRows=false;grid.CanUserDeleteRows=false;grid.SelectionMode=DataGridSelectionMode.Extended;grid.HeadersVisibility=DataGridHeadersVisibility.Column;grid.RowHeight=28;
        var check=new FrameworkElementFactory(typeof(CheckBox));check.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(Choice.Selected)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});check.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);check.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        grid.Columns.Add(new DataGridTemplateColumn{Header="选择",Width=55,CellTemplate=new DataTemplate{VisualTree=check}});grid.Columns.Add(new DataGridTextColumn{Header=title,Binding=new Binding(nameof(Choice.Name)),IsReadOnly=true,MinWidth=220,Width=new DataGridLength(1,DataGridLengthUnitType.Star)});grid.Columns.Add(new DataGridTextColumn{Header=detail,Binding=new Binding(nameof(Choice.Detail)),IsReadOnly=true,Width=180});grid.ItemsSource=rows;CollectionViewSource.GetDefaultView(rows).Filter=o=>o is Choice c&&Matches(c);}
    private bool Matches(Choice c)=>string.IsNullOrWhiteSpace(_search.Text)||c.Name.Contains(_search.Text.Trim(),StringComparison.CurrentCultureIgnoreCase)||c.Detail.Contains(_search.Text.Trim(),StringComparison.CurrentCultureIgnoreCase);
    private void ToggleAll(bool value){_bulk=true;try{foreach(var row in CurrentRows().Where(Matches))row.Selected=value;}finally{_bulk=false;Summary();}}
    private IEnumerable<Choice> CurrentRows()=>_tabs.SelectedIndex==1?_collections:_tabs.SelectedIndex==2?_points:[];
    private FavoriteExportSelection BuildSelection()=>new((FavoriteExportScope)Math.Max(0,_tabs.SelectedIndex),_collections.Where(c=>c.Selected).Select(c=>c.Id).ToArray(),_points.Where(c=>c.Selected).Select(c=>c.Id).ToArray());
    private void Summary(){var selection=BuildSelection();_summary.Text=selection.Scope switch{FavoriteExportScope.Collections=>$"已勾选 {selection.CollectionIds.Count} 个收藏夹；包含所选收藏夹内全部收藏点与本地图片。",FavoriteExportScope.Points=>$"已勾选 {selection.PointIds.Count} 个收藏点；包含这些点的本地图片。",_=>"全部收藏库；网络图片仅引用仍保留原链接。"};}
    internal void SelectForAutomation(FavoriteExportScope scope,params Guid[] ids){_tabs.SelectedIndex=(int)scope;foreach(var row in CurrentRows())row.Selected=ids.Contains(row.Id);Selection=BuildSelection();Selection.Validate();}
}
