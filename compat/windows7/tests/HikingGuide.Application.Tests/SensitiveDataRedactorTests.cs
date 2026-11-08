using HikingGuide.Application.Maps;

namespace HikingGuide.Application.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData("https://example.test/tile?key=abc&x=1")]
    [InlineData("https://example.test/tile?token=abc&x=1")]
    [InlineData("https://example.test/tile?api_key=abc&x=1")]
    public void RedactUrl_RemovesSecretValues(string url)
    {
        var redacted = SensitiveDataRedactor.RedactUrl(url);
        Assert.DoesNotContain("abc", redacted, StringComparison.Ordinal);
        Assert.Contains("***", Uri.UnescapeDataString(redacted), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("access-token")]
    public void IsSensitiveHeader_RecognizesSecretHeaders(string name) =>
        Assert.True(SensitiveDataRedactor.IsSensitiveHeader(name));
}
