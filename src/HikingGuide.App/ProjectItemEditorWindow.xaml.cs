using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.App;

public partial class ProjectItemEditorWindow : Window
{
    private string _color = "#2877C7";
    private readonly bool _guideSegment;
    private Track? _kilometerTrack;
    public string? EditedKilometerKind=>RouteKindCombo.SelectedIndex==1?"route":RouteKindCombo.SelectedIndex==2?"collection":null;
    public string? EditedKilometerDirections {get;private set;}

    public string? EditedName { get; private set; }
    public string? EditedDescription { get; private set; }
    public string? EditedRoadCondition { get; private set; }
    public TimeSpan? EditedDuration { get; private set; }
    public string EditedColor => _color;
    public bool? EditedKilometers=>RouteKindCombo.SelectedIndex==2?false:KilometerBox.Visibility==Visibility.Visible?KilometerBox.IsChecked==true:null;

    public ProjectItemEditorWindow(Track track)
    {
        InitializeComponent();
        Title = "编辑轨迹属性";
        _kilometerTrack=track;RouteKindPanel.Visibility=track.Segments.Count>1?Visibility.Visible:Visibility.Collapsed;RouteKindCombo.SelectedIndex=track.Metadata.GetValueOrDefault(HikingGuide.Core.Services.TrackKilometerService.RouteKindKey)=="collection"?2:track.Metadata.GetValueOrDefault(HikingGuide.Core.Services.TrackKilometerService.RouteKindKey)=="route"?1:0;
        ObjectKindText.Text = "轨迹属性";
        NameInput.Text = track.Name ?? string.Empty;
        DescriptionInput.Text = track.Description ?? string.Empty;
        NameHint.Text = "名称和说明会保存在项目中，不会改写原始轨迹文件。";
        SafetyHint.Text = "轨迹点、长度、来源高程和项目主攻略轨迹设置不在此窗口修改。";
        ColorPanel.Visibility = Visibility.Visible;
        if(new HikingGuide.Core.Services.TrackKilometerService().Assess(track).Eligible){KilometerDirectionButton.Visibility=Visibility.Visible;_kilometerTrack=track;EditedKilometerDirections=track.Metadata.GetValueOrDefault(HikingGuide.Core.Services.KilometerRouteDirections.Key);KilometerBox.Visibility=Visibility.Visible;KilometerBox.IsChecked=HikingGuide.Core.Services.TrackKilometerService.IsEnabled(track);}
        SetColor(TrackDisplayColor.IsValid(track.DisplayColor) ? track.DisplayColor! : "#2877C7");
    }

    public ProjectItemEditorWindow(SplitSection section)
    {
        InitializeComponent();
        Title = "编辑拆分分段属性";
        ObjectKindText.Text = "拆分分段属性";
        NameInput.Text = section.Name;
        DescriptionInput.Text = section.GeometryParts.FirstOrDefault()?.SplitSectionDescription ?? string.Empty;
        NameHint.Text = "同一逻辑分段的所有物理 Part 会共用此名称与说明。";
        SafetyHint.Text = "修改名称和说明不会改变原始轨迹点、分段顺序或几何间隙。";
    }

    public ProjectItemEditorWindow(GuideSegment segment, string defaultName)
    {
        InitializeComponent();
        _guideSegment = true;
        Title = "编辑攻略分段属性";
        ObjectKindText.Text = "攻略分段属性";
        NameInput.Text = segment.CustomName ?? string.Empty;
        DescriptionInput.Text = segment.ManualData?.Description ?? string.Empty;
        RoadConditionInput.Text = segment.ManualData?.RoadCondition ?? string.Empty;
        DurationInput.Text = segment.ManualData?.PlannedDuration?.TotalMinutes.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
        NameHint.Text = $"留空则自动显示“{defaultName}”，分段距离仍会附在名称后。";
        SafetyHint.Text = "路况、描述和计划用时属于人工攻略内容；起终节点、实际距离和 DEM 统计不可在此直接改写。";
        RoadConditionPanel.Visibility = Visibility.Visible;
        DurationPanel.Visibility = Visibility.Visible;
    }

    private void SetColor(string color)
    {
        _color = color;
        ColorPreview.Background = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        var picker = new TrackColorWindow(_color) { Owner = this };
        if (picker.ShowDialog() == true) SetColor(picker.SelectedColor);
    }

    private void OnRouteKindChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(_kilometerTrack is not {} track||KilometerBox is null||KilometerDirectionButton is null)return;
        var show=RouteKindCombo.SelectedIndex!=2&&(RouteKindCombo.SelectedIndex==1||new HikingGuide.Core.Services.TrackKilometerService().Assess(track,true).Eligible);
        KilometerBox.Visibility=KilometerDirectionButton.Visibility=show?Visibility.Visible:Visibility.Collapsed;if(!show)KilometerBox.IsChecked=false;
    }
    private void OnKilometerDirections(object sender,RoutedEventArgs e)
    {
        if(_kilometerTrack is not {} track)return;
        var preview=new Track{Name=track.Name,Segments=track.Segments,Metadata=new(track.Metadata)};preview.RestoreGeometryRevision(track.GeometryRevision);if(EditedKilometerDirections is {} directions)preview.Metadata[HikingGuide.Core.Services.KilometerRouteDirections.Key]=directions;var dialog=new KilometerDirectionWindow(preview){Owner=this};if(dialog.ShowDialog()==true)EditedKilometerDirections=HikingGuide.Core.Services.KilometerRouteDirections.Serialize(track,dialog.Result!);
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if(KilometerBox.IsChecked==true&&RouteKindCombo.SelectedIndex==0&&_kilometerTrack is {} pending&&HikingGuide.Core.Services.TrackKilometerService.RequiresKindConfirmation(pending)){MessageBox.Show(this,"请先选择路线性质。","请选择路线性质");return;}
        if(KilometerBox.IsChecked==true&&_kilometerTrack is {} track&&HikingGuide.Core.Services.KilometerRouteDirections.NeedsConfirmation(track)&&EditedKilometerDirections is null){OnKilometerDirections(sender,e);if(EditedKilometerDirections is null)return;}

        var name = NameInput.Text.Trim();
        if (!_guideSegment && name.Length == 0)
        {
            MessageBox.Show(this, "名称不能为空。", "请检查名称", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameInput.Focus();
            return;
        }
        TimeSpan? duration = null;
        if (_guideSegment && !string.IsNullOrWhiteSpace(DurationInput.Text))
        {
            if (!double.TryParse(DurationInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var minutes) ||
                !double.IsFinite(minutes) || minutes < 0 || minutes > 100000)
            {
                MessageBox.Show(this, "计划用时请输入 0 到 100000 之间的分钟数，或留空。",
                    "请检查计划用时", MessageBoxButton.OK, MessageBoxImage.Warning);
                DurationInput.Focus();
                return;
            }
            duration = TimeSpan.FromMinutes(minutes);
        }
        EditedName = name.Length == 0 ? null : name;
        EditedDescription = string.IsNullOrWhiteSpace(DescriptionInput.Text) ? null : DescriptionInput.Text.Trim();
        EditedRoadCondition = string.IsNullOrWhiteSpace(RoadConditionInput.Text) ? null : RoadConditionInput.Text.Trim();
        EditedDuration = duration;
        DialogResult = true;
    }
}
