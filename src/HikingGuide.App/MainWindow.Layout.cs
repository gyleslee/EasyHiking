using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HikingGuide.Core.Models;

namespace HikingGuide.App;

public partial class MainWindow
{
    private bool _panelLayoutReady;
    private MainWindowPanelLayout _expandedLayout = MainWindowPanelLayout.Default;
    private MainWindowPanelLayout _defaultPanelLayout = MainWindowPanelLayout.Default;
    private PanelVisibilitySettings _appliedPanelVisibility = new();
    public Task PanelLayoutSaveTask { get; private set; } = Task.CompletedTask;

    private void RestorePanelLayout()
    {
        _panelLayoutReady = true;
        _defaultPanelLayout = MainWindowPanelLayout.Default;
        _expandedLayout = ((App)System.Windows.Application.Current).GlobalSettingsService.Current.PanelLayout is {IsValid:true} saved ? saved : _defaultPanelLayout;
        ApplyPanelVisibility();
    }

    public MainWindowPanelLayout? CapturePanelLayout()
    {
        if (!_panelLayoutReady || WindowState == WindowState.Minimized) return null;
        var visible = _appliedPanelVisibility;
        double Width(FrameworkElement panel,bool shown) => shown ? UpperPanels.ColumnDefinitions[Grid.GetColumn(panel)].ActualWidth : 0;
        var projectWidth=Width(ProjectPanel,visible.Project);var mapWidth=Width(MapPanel,visible.Map);var propertiesWidth=Width(PropertiesPanel,visible.Properties);
        var width=projectWidth+mapWidth+propertiesWidth;
        var available=1-(!visible.Project?_expandedLayout.ProjectFraction:0)-(!visible.Properties?_expandedLayout.PropertiesFraction:0)-(!visible.Map?1-_expandedLayout.ProjectFraction-_expandedLayout.PropertiesFraction:0);
        var height=UpperPanelRow.ActualHeight+BottomPanelRow.ActualHeight;
        var layout = new MainWindowPanelLayout(
            visible.AnyUpper&&visible.AnyBottom&&height>0?BottomPanelRow.ActualHeight/height:_expandedLayout.BottomFraction,
            visible.Project&&width>0?projectWidth/width*available:_expandedLayout.ProjectFraction,
            visible.Properties&&width>0?propertiesWidth/width*available:_expandedLayout.PropertiesFraction);
        return layout.IsValid ? layout : null;
    }

    private async void OnPanelSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.Canceled) return;
        PanelLayoutSaveTask = SavePanelLayoutSafelyAsync();
        await PanelLayoutSaveTask;
    }

    private async Task SavePanelLayoutSafelyAsync()
    {
        var layout = CapturePanelLayout();
        if (layout is null) return;
        _expandedLayout=layout;
        var service = ((App)System.Windows.Application.Current).GlobalSettingsService;
        service.Current.PanelLayout = layout;
        try { await service.SaveAsync(); }
        catch (Exception error)
        {
            MessageBox.Show(this, "界面布局未能保存，下次启动可能恢复默认大小。\n" + error.Message,
                "界面布局保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyPanelVisibility()
    {
        var v=((App)System.Windows.Application.Current).GlobalSettingsService.Current.PanelVisibility;
        _appliedPanelVisibility=v;
        static Visibility Shown(bool value)=>value?Visibility.Visible:Visibility.Collapsed;
        var panels=new (FrameworkElement Panel,bool Visible,double Weight,double Minimum)[]{
            (ProjectPanel,v.Project,_expandedLayout.ProjectFraction,160),
            (MapPanel,v.Map,1-_expandedLayout.ProjectFraction-_expandedLayout.PropertiesFraction,200),
            (PropertiesPanel,v.Properties,_expandedLayout.PropertiesFraction,160)};
        foreach(var column in UpperPanels.ColumnDefinitions){column.MinWidth=0;column.Width=new GridLength(0);}
        var index=0;
        foreach(var p in panels)
        {
            p.Panel.Visibility=Shown(p.Visible);if(!p.Visible)continue;
            var slot=index++*2;Grid.SetColumn(p.Panel,slot);
            UpperPanels.ColumnDefinitions[slot].MinWidth=p.Minimum;
            UpperPanels.ColumnDefinitions[slot].Width=new GridLength(p.Weight,GridUnitType.Star);
        }
        UpperPanels.ColumnDefinitions[1].Width=new GridLength(index>=2?5:0);
        UpperPanels.ColumnDefinitions[3].Width=new GridLength(index>=3?5:0);
        ProjectPanelSplitter.Visibility=Shown(index>=2);PropertiesPanelSplitter.Visibility=Shown(index>=3);
        UpperPanels.Visibility=Shown(v.AnyUpper);
        UpperPanelRow.MinHeight=v.AnyUpper?120:0;BottomPanelRow.MinHeight=v.AnyBottom?200:0;
        UpperPanelRow.Height=v.AnyUpper?new GridLength(1-_expandedLayout.BottomFraction,GridUnitType.Star):new GridLength(0);
        BottomPanelRow.Height=v.AnyBottom?new GridLength(_expandedLayout.BottomFraction,GridUnitType.Star):new GridLength(0);
        UpperBottomSplitterRow.Height=new GridLength(v.AnyUpper&&v.AnyBottom?6:0);
        UpperBottomSplitter.Visibility=Shown(v.AnyUpper&&v.AnyBottom);
        GuideTab.Visibility=Shown(v.Guide);PlanningTab.Visibility=Shown(v.Planning);AnalysisTab.Visibility=Shown(v.Analysis);FavoritesTab.Visibility=Shown(v.Favorites);
        BottomTabs.Visibility=Shown(v.AnyBottom);MainStatusBar.Visibility=Shown(v.Status);
        if(BottomTabs.SelectedItem is not TabItem {Visibility:Visibility.Visible})
            BottomTabs.SelectedItem=BottomTabs.Items.OfType<TabItem>().FirstOrDefault(t=>t.Visibility==Visibility.Visible&&t.IsEnabled);
        ProjectPanelMenu.IsChecked=v.Project;MapPanelMenu.IsChecked=v.Map;PropertiesPanelMenu.IsChecked=v.Properties;
        GuidePanelMenu.IsChecked=v.Guide;PlanningPanelMenu.IsChecked=v.Planning;AnalysisPanelMenu.IsChecked=v.Analysis;
        FavoritesPanelMenu.IsChecked=v.Favorites;StatusPanelMenu.IsChecked=v.Status;
        ElevationAnalysis.SetAnalysisActive(IsAnalysisTabActive);
    }

    private async void OnPanelVisibilityClick(object sender,RoutedEventArgs e)
    {
        if(sender is MenuItem {Tag:string panel} menu)await SetPanelVisibilityAsync(panel,menu.IsChecked);
    }
    internal async Task SetPanelVisibilityAsync(string panel,bool shown)
    {
        if(panel=="Guide"&&!shown&&!GuideWorkbench.CommitEdits())
        {ApplyPanelVisibility();return;}
        _expandedLayout=CapturePanelLayout()??_expandedLayout;
        var service=((App)System.Windows.Application.Current).GlobalSettingsService;
        var v=service.Current.PanelVisibility;
        service.Current.PanelVisibility=panel switch{
            "Project"=>v with{Project=shown},"Map"=>v with{Map=shown},"Properties"=>v with{Properties=shown},
            "Guide"=>v with{Guide=shown},"Planning"=>v with{Planning=shown},"Analysis"=>v with{Analysis=shown},
            "Favorites"=>v with{Favorites=shown},"Status"=>v with{Status=shown},_=>throw new ArgumentException("未知面板。",nameof(panel))};
        ApplyPanelVisibility();UpdateLayout();
        PanelLayoutSaveTask=SavePanelLayoutSafelyAsync();await PanelLayoutSaveTask;
    }
    private async void OnResetPanelLayout(object sender,RoutedEventArgs e)=>await ResetPanelLayoutAsync();
    internal async Task ResetPanelLayoutAsync()
    {
        ((App)System.Windows.Application.Current).GlobalSettingsService.Current.PanelVisibility=new();
        _expandedLayout=_defaultPanelLayout;ApplyPanelVisibility();UpdateLayout();
        PanelLayoutSaveTask=SavePanelLayoutSafelyAsync();await PanelLayoutSaveTask;
    }
    private async void ShowBottomPanel(int index,bool showMap=false)
    {
        var name=index switch{0=>"Guide",1=>"Planning",2=>"Analysis",3=>"Favorites",_=>throw new ArgumentOutOfRangeException(nameof(index))};
        var savePanel=SetPanelVisibilityAsync(name,true);
        var saveMap=showMap?SetPanelVisibilityAsync("Map",true):Task.CompletedTask;
        BottomTabs.SelectedIndex=index;
        await savePanel;await saveMap;
    }
}
