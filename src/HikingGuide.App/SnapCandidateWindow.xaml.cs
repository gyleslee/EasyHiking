using System.Windows;
using HikingGuide.Core.Models;

namespace HikingGuide.App;

public partial class SnapCandidateWindow : Window
{
    public TrackPosition? SelectedPosition { get; private set; }

    public SnapCandidateWindow(IReadOnlyList<TrackPosition> positions)
    {
        InitializeComponent();
        CandidateGrid.ItemsSource = positions.Select((position, index) => new CandidateRow(index + 1, position)).ToArray();
        CandidateGrid.SelectedIndex = 0;
    }
    private void ConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (CandidateGrid.SelectedItem is not CandidateRow row) return;
        SelectedPosition = row.Position;
        DialogResult = true;
    }
    private sealed record CandidateRow(int Order, TrackPosition Position)
    {
        public string DistanceToClick => Position.DistanceToTrack.ToString("F1");
        public string DistanceKm => (Position.DistanceFromTrackStart / 1000).ToString("F2");
        public int SegmentIndex => Position.SegmentIndex + 1;
        public int PointIndex => Position.StartPointIndex;
        public string Fraction => Position.Fraction.ToString("F3");
    }
}
