using System.Reflection;

namespace HikingGuide.Core.Software;

/// <summary>用户可见版本来自构建元数据，避免标题、帮助和交付包各自声明不同版本。</summary>
public static class SoftwareIdentity
{
    public const string DisplayName = "易行徒步规划制作器";
    public static string DisplayVersion => typeof(SoftwareIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "V1.9.5";
    public static string DisplayTitle => $"{DisplayName} {DisplayVersion}";
}
