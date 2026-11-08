using System.Text.RegularExpressions;

namespace HikingGuide.Application.Maps;

public static partial class SensitiveDataRedactor
{
    public static string RedactUrl(string url) => SecretQueryRegex().Replace(url, "$1=***");

    public static bool IsSensitiveHeader(string name) =>
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex CachedSecretQueryRegex = new("(?i)([?&](?:key|api[_-]?key|token|access[_-]?token|secret))=[^&#]*", RegexOptions.CultureInvariant);
    private static Regex SecretQueryRegex() => CachedSecretQueryRegex;
}
