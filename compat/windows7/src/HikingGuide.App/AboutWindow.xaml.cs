using System.Windows;
using HikingGuide.Core.Software;

namespace HikingGuide.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Title = "关于｜" + SoftwareIdentity.DisplayTitle;
        NameLabel.Text = SoftwareIdentity.DisplayName;
        VersionLabel.Text = "软件版本：" + SoftwareIdentity.DisplayVersion;
    }

    private void OnOpenManual(object sender, RoutedEventArgs e) => MainWindow.OpenManual(this);
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

