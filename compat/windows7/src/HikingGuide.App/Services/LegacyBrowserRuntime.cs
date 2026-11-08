namespace HikingGuide.App.Services;

/// <summary>兼容构建固定使用随包的微软109运行时，不替换系统浏览器。</summary>
internal static class LegacyBrowserRuntime
{
    public static string ExecutableFolder=>System.IO.Path.Combine(AppContext.BaseDirectory,"WebView2Runtime");
}
