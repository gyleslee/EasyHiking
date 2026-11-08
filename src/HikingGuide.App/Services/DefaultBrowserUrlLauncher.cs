using System.Diagnostics;

namespace HikingGuide.App.Services;

public static class DefaultBrowserUrlLauncher
{
    public static void OpenInDefaultBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
