using System.Windows.Input;
using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed partial class MainViewModel
{
    internal Dictionary<string, object> RunP91SelectionSmoke()
    {
        var checks = new Dictionary<string, object>(); var outputs = new List<string>();
        HikingProject Create(string name)
        {
            var p = new HikingProject { Name = name };
            foreach (var label in new[] { "A", "B", "C" })
            { var t = new Track { Name = name + label }; t.Segments.Add(new TrackSegment { Points = [new TrackPoint { Latitude = 23, Longitude = 113 }, new TrackPoint { Latitude = 23.01, Longitude = 113.01 }] }); t.RecalculateDistances(); p.AddTrack(t); }
            p.Waypoints.Add(new Waypoint { Name = "关联点", AssociatedTrackId = p.Tracks[1].Id, Latitude = 23, Longitude = 113 });
            p.Waypoints.Add(new Waypoint { Name = "未关联", Latitude = 23, Longitude = 113 });
            p.GuideNodes.Add(new GuideNode { Name = "攻略节点", TrackId = p.Tracks[1].Id, OriginalCoordinate = new(23, 113) }); p.MainGuideTrackId = p.Tracks[1].Id;
            p.MarkSaved(); return p;
        }
        var a = Create("项目甲"); var b = Create("项目乙"); _projects.UseRecovered(a, null); _projects.UseRecovered(b, null); RefreshProjectView(false);
        NavigationNode Node(object model) => NavigationNodes.SelectMany(Flatten).First(n => ReferenceEquals(n.Model, model) || model is SplitSection s && n.Model is SplitSection x && s.Id == x.Id);
        void Choose(object model) { SelectNavigationNode(Node(model), false); outputs.Add(CurrentSelectedTrackStatus); }
        bool consistent = true;
        foreach (var i in new[] { 0, 1, 0, 2, 1, 2, 0, 1, 2, 0, 2, 1 }) { Choose(a.Tracks[i]); consistent &= PrimarySelectedTrack == a.Tracks[i] && a.ActiveTrackId == a.Tracks[i].Id; }
        checks["TwelveTrackSwitches"] = consistent;
        OnMapSelectionRequested(this, new(HikingGuide.Application.Maps.MapSelectionKind.Track, a.Tracks[1].Id)); checks["MapTrackSelection"] = PrimarySelectedTrack == a.Tracks[1];
        Choose(a.Tracks[2].GetSplitSections()[0]); checks["SplitParentTrack"] = PrimarySelectedTrack == a.Tracks[2];
        Choose(a.GuideNodes[0]); checks["GuideParentTrack"] = PrimarySelectedTrack == a.Tracks[1];
        Choose(a.Waypoints[0]); checks["WaypointParentTrack"] = PrimarySelectedTrack == a.Tracks[1];
        Choose(a.Waypoints[1]); checks["UnassociatedClearsPrimary"] = CurrentSelectedTrackDisplayName == "无";
        var renamedNotified = false; Choose(a.Tracks[0]); PropertyChanged += Changed;
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(CurrentSelectedTrackDisplayName)) renamedNotified = true; }
        a.Tracks[0].Name = "项目甲A-new"; checks["RenameImmediate"] = renamedNotified && CurrentSelectedTrackDisplayName == "项目甲A-new"; PropertyChanged -= Changed;
        _selectedNavigationKeys.Clear(); _primaryCandidateKeys.Clear();
        foreach (var track in a.Tracks) UpdateProjectItemSelection(Node(track), ModifierKeys.Control, focusOnMap: false);
        checks["CtrlPrimaryLastClicked"] = PrimarySelectedTrack == a.Tracks[2];
        UpdateProjectItemSelection(Node(a.Tracks[2]), ModifierKeys.Control, focusOnMap: false); checks["CtrlCancelFallsBack"] = PrimarySelectedTrack == a.Tracks[1];
        Choose(a.Tracks[1]); Choose(b.Tracks[2]); bool switches = true;
        for (var i = 0; i < 5; i++) { Choose(a); switches &= PrimarySelectedTrack == a.Tracks[1]; Choose(b); switches &= PrimarySelectedTrack == b.Tracks[2]; }
        checks["FiveProjectRoundTrips"] = switches;
        var selected = b.Tracks[2]; b.Tracks.Remove(selected); RefreshProjectView(false); checks["DeleteClearsPrimary"] = PrimarySelectedTrack is null && CurrentSelectedTrackDisplayName == "无";
        SelectNavigationNode(null, false); checks["EmptySelection"] = CurrentSelectedTrackDisplayName == "无";
        foreach (var context in _projects.OpenProjects) context.Project.MarkSaved();
        checks["Outputs"] = outputs; return checks;
        static IEnumerable<NavigationNode> Flatten(NavigationNode node) { yield return node; foreach (var child in node.Children) foreach (var item in Flatten(child)) yield return item; }
    }
}
