using System.Windows;
using HikingGuide.Core.Models;

namespace HikingGuide.App;

public sealed record MarkerEditResult(string? Name, string? Description, WaypointType Type,
    string? CustomTypeName, bool IsGuideNode, Guid? TrackId);

public partial class MarkerEditorWindow : Window
{
    private readonly HikingProject _project;
    public MarkerEditResult? Result { get; private set; }

    public MarkerEditorWindow(MapMarkerBase marker, HikingProject project, bool? requestedGuide = null, Guid? requestedTrackId = null)
    {
        InitializeComponent();
        _project = project;
        NameBox.Text = marker.Name ?? "";
        DescriptionBox.Text = marker.Description ?? "";
        CustomTypeBox.Text = marker.CustomTypeName ?? "";
        TypeCombo.ItemsSource = Enum.GetValues<WaypointType>().Select(type => new MarkerTypeChoice(type, NameOf(type))).ToArray();
        TypeCombo.SelectedValue = marker.Type;
        TrackCombo.ItemsSource = project.Tracks.Select(track => new TrackChoice(track.Id, track.Name ?? "未命名轨迹")).ToArray();
        GuideCheck.IsChecked = requestedGuide ?? (marker is GuideNode);
        TrackCombo.SelectedValue = requestedTrackId ?? (marker is GuideNode node
            ? requestedGuide == false ? node.ToWaypoint(project).AssociatedTrackId : project.MainGuideTrackId
            : (marker as Waypoint)?.AssociatedTrackId);
        if (GuideCheck.IsChecked == true) TrackCombo.SelectedValue = project.MainGuideTrackId;
        CoordinateLabel.Text = marker switch
        {
            Waypoint waypoint => $"坐标：{waypoint.Latitude:F7}, {waypoint.Longitude:F7}；原始海拔：{waypoint.SourceElevation?.ToString("F1") ?? "—"} m",
            GuideNode guide => $"原始坐标：{guide.OriginalCoordinate.Latitude:F7}, {guide.OriginalCoordinate.Longitude:F7}；原始海拔：{guide.SourceElevation?.ToString("F1") ?? "—"} m",
            _ => ""
        };
        TrackCombo.IsEnabled = GuideCheck.IsChecked != true;
        if (GuideCheck.IsChecked == true) TrackCombo.SelectedValue = project.MainGuideTrackId;
    }

    internal static string NameOf(WaypointType type) => type switch
    {
        WaypointType.Normal => "普通", WaypointType.Start => "起点", WaypointType.Finish => "终点",
        WaypointType.Summit => "山峰", WaypointType.Pass => "垭口", WaypointType.Junction => "岔路",
        WaypointType.Camp => "营地", WaypointType.Water => "水源", WaypointType.Resupply => "补给",
        WaypointType.Village => "村庄", WaypointType.Accommodation => "住宿", WaypointType.Transport => "交通（旧版）", WaypointType.PublicTransport => "公共交通",
        WaypointType.Evacuation => "下撤点", WaypointType.Danger => "危险点", WaypointType.Viewpoint => "观景点",
        WaypointType.Checkpoint => "检查站", WaypointType.Toilet => "卫生间", WaypointType.Parking => "停车",
        WaypointType.Other => "其它", WaypointType.Custom => "自定义", _ => "普通"
    };
    private void GuideChecked(object sender, RoutedEventArgs e)
    {
        if (TrackCombo is not null)
        {
            TrackCombo.IsEnabled = GuideCheck.IsChecked != true;
            if (GuideCheck.IsChecked == true) TrackCombo.SelectedValue = _project.MainGuideTrackId;
        }
    }
    private void ConfirmClicked(object sender, RoutedEventArgs e)
    {
        var guide = GuideCheck.IsChecked == true;
        var trackId = guide ? _project.MainGuideTrackId : TrackCombo.SelectedValue as Guid?;
        if (guide && trackId is null)
        {
            MessageBox.Show(this, "请先选择攻略轨迹。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var type = TypeCombo.SelectedValue is WaypointType value ? value : WaypointType.Normal;
        var customName = CustomTypeBox.Text.Trim();
        if (type == WaypointType.Custom && customName.Length == 0)
        {
            MessageBox.Show(this, "请填写自定义类型名称。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = new MarkerEditResult(NameBox.Text.Trim(), DescriptionBox.Text.Trim(), type,
            type == WaypointType.Custom ? customName : null, guide, trackId);
        DialogResult = true;
    }
    private sealed record MarkerTypeChoice(WaypointType Code, string Name);
    private sealed record TrackChoice(Guid Id, string Name);
}
