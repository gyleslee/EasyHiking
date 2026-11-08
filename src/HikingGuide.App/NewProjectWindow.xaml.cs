using System.IO;
using System.Windows;
using HikingGuide.App.Services;
using Microsoft.Win32;

namespace HikingGuide.App;

public partial class NewProjectWindow : Window
{
    public NewProjectWindow() => InitializeComponent();

    public NewProjectRequest? Result { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(ProjectNameBox.Text) ? "未命名项目" : ProjectNameBox.Text.Trim();
        var dialog = new SaveFileDialog
        {
            Title = "选择项目保存位置",
            Filter = "徒步攻略项目 (*.hikeproj)|*.hikeproj",
            DefaultExt = ".hikeproj",
            AddExtension = true,
            FileName = Sanitize(name) + ".hikeproj"
        };
        if (dialog.ShowDialog(this) == true) LocationBox.Text = dialog.FileName;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = ProjectNameBox.Text.Trim();
        var path = LocationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "项目名称不能为空。", "无法创建项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            ProjectNameBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "请选择项目保存位置。", "无法创建项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.Equals(Path.GetExtension(path), ".hikeproj", StringComparison.OrdinalIgnoreCase)) path += ".hikeproj";
        Result = new NewProjectRequest(name, Path.GetFullPath(path));
        DialogResult = true;
    }

    private static string Sanitize(string value) => new(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
}
