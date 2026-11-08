using System.Windows;

namespace HikingGuide.App;

public partial class RouteTrackInfoWindow : Window
{
    public RouteTrackInfoWindow(string suggestedName)
    {
        InitializeComponent();
        NameInput.Text = suggestedName;
        NameInput.Focus();
        NameInput.SelectAll();
    }

    public string TrackName => NameInput.Text.Trim();
    public string? TrackDescription => string.IsNullOrWhiteSpace(DescriptionInput.Text)
        ? null : DescriptionInput.Text.Trim();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (TrackName.Length == 0)
        {
            MessageBox.Show(this, "请填写轨迹名称。", "完成路线规划",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
