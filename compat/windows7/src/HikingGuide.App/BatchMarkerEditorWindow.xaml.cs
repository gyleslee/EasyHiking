using System.Windows;
using HikingGuide.Core.Models;
using HikingGuide.Application.Guides;

namespace HikingGuide.App;

public partial class BatchMarkerEditorWindow : Window
{
    public MarkerFieldPatch? Result { get; private set; }
    private readonly HikingProject? _project;
    public BatchMarkerEditorWindow(IReadOnlyList<MapMarkerBase> markers, HikingProject? project = null)
    {
        InitializeComponent();
        _project = project;
        SelectionLabel.Text = $"已选择 {markers.Count} 个点：普通标注 {markers.Count(m => m is Waypoint)} 个，攻略节点 {markers.Count(m => m is GuideNode)} 个";
        TypeCombo.ItemsSource = Enum.GetValues<WaypointType>().Select(t => new TypeChoice(t, MarkerEditorWindow.NameOf(t))).ToArray();
        TypeCombo.SelectedValue = WaypointType.Normal;
        TrackCombo.ItemsSource = new[] { new TrackChoice(null, "未关联标注") }.Concat(
            project?.Tracks.Select(t => new TrackChoice(t.Id, t.Name ?? "未命名轨迹")) ?? []).ToArray();
        TrackCombo.SelectedIndex = 0;
        GuideStateChanged(this, new RoutedEventArgs());
    }
    private void GuideStateChanged(object sender, RoutedEventArgs e)
    {
        if (ApplyTrack is null) return;
        var promote = ApplyGuideState.IsChecked == true && GuideCheck.IsChecked == true;
        ApplyTrack.IsEnabled = !promote;
        if (promote) ApplyTrack.IsChecked = false;
    }
    private void ConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (ApplyName.IsChecked != true && ApplyDescription.IsChecked != true && ApplyType.IsChecked != true &&
            ApplyGuideState.IsChecked != true && ApplyTrack.IsChecked != true)
        { MessageBox.Show(this, "请勾选至少一个需要修改的字段。", "无法应用"); return; }
        var type = (WaypointType)TypeCombo.SelectedValue;
        if (ApplyType.IsChecked == true && type == WaypointType.Custom && string.IsNullOrWhiteSpace(CustomTypeBox.Text))
        { MessageBox.Show(this, "请填写自定义类型名称。", "无法应用"); return; }
        if (ApplyGuideState.IsChecked == true && GuideCheck.IsChecked == true && _project?.MainGuideTrack is null)
        { MessageBox.Show(this, "请先设置主攻略轨迹，再设为攻略节点。", "无法应用"); return; }
        Result = new(ApplyName.IsChecked == true, NameBox.Text.Trim(), ApplyDescription.IsChecked == true,
            DescriptionBox.Text.Trim(), ApplyType.IsChecked == true, type, CustomTypeBox.Text.Trim(),
            ApplyGuideState.IsChecked == true, GuideCheck.IsChecked == true, ApplyTrack.IsChecked == true,
            (TrackCombo.SelectedItem as TrackChoice)?.Id);
        DialogResult = true;
    }
    private sealed record TypeChoice(WaypointType Code, string Name);
    private sealed record TrackChoice(Guid? Id, string Name);
}
