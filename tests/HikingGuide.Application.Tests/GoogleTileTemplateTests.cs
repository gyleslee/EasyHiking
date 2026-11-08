using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class GoogleTileTemplateTests
{
    private const string Google = "http://mt{$s}.google.com/vt/lyrs=y&hl=zh-CN&gl=cn&scale=2&x={x}&y={y}&z={z}";

    [Theory]
    [InlineData(Google)]
    [InlineData("https://MT{$S}.GOOGLE.COM/vt/lyrs=y&x={X}&y={Y}&z={zoom}")]
    public void GoogleAliasUsesUnpaddedServersAndKeepsRawTemplate(string raw)
    {
        var parsed = MapTileTemplateParser.Parse(raw);
        Assert.Equal(raw, parsed.RawTemplate);
        Assert.Equal(new[] { "0", "1", "2", "3" }, parsed.ServerTokens);
        for (var i = 0; i < 4; i++)
        {
            var uri = new Uri(MapTileTemplateParser.BuildTileUrl(parsed.NormalizedTemplates[i], 14, 13358, 7114));
            Assert.Equal($"mt{i}.google.com", uri.Host);
            Assert.DoesNotContain("{", uri.AbsoluteUri);
        }
        Assert.True(new MapSourceTemplateService().Validate(new() { Name = "Google测试", UrlTemplate = raw }).IsValid);
    }

    [Fact]
    public void AllLayerRegistryRequestsResolveToCorrectGoogleHostsAndShareTileCache()
    {
        var source = new MapSourceDefinition { Name = "Google测试", UrlTemplate = Google };
        var registry = new MapTileRequestRegistry();
        var slot = registry.Register(MapLayerStackService.Build(new() { BaseSourceId = source.Id }, [source]))[0];
        TileCacheRequest? cache = null;
        Assert.Equal(4, slot.Templates.Count);
        for (var i = 0; i < 4; i++)
        {
            using var request = registry.Resolve(new(MapTileTemplateParser.BuildTileUrl(slot.Templates[i], 14, 13358, 7114)));
            Assert.NotNull(request);
            Assert.Equal($"http://mt{i}.google.com/vt/lyrs=y&hl=zh-CN&gl=cn&scale=2&x=13358&y=7114&z=14", request.RequestUri!.OriginalString);
            Assert.True(request.Options.TryGetValue(MapTileRequestRegistry.CacheRequestKey, out var current));
            if (cache is not null) Assert.Equal(cache, current);
            cache = current;
        }
    }

    [Theory]
    [InlineData("http://mt{switch:1,2,3}.google.com/vt/lyrs=y&x={x}&y={y}&z={z}", "1", "3")]
    [InlineData("http://mt{switch:01,02}.google.com/vt/lyrs=y&x={x}&y={y}&z={z}", "01", "02")]
    [InlineData("http://webst{$s}.is.autonavi.com/appmaptile?x={x}&y={y}&z={z}", "01", "04")]
    [InlineData("https://tiles{$s}.example/{z}/{x}/{y}?hint=mt.google.com", "01", "04")]
    [InlineData("https://mt{$s}.google.com.example/{z}/{x}/{y}", "01", "04")]
    public void ExplicitSwitchesAndOtherProvidersKeepExistingRules(string raw, string first, string last)
    {
        var parsed = MapTileTemplateParser.Parse(raw);
        Assert.Equal(first, parsed.ServerTokens[0]);
        Assert.Equal(last, parsed.ServerTokens[^1]);
    }

    [Fact]
    public void FixedGoogleAddressRemainsUnchanged()
    {
        var fixedUrl = Google.Replace("{$s}", "1", StringComparison.Ordinal);
        Assert.Equal(fixedUrl, Assert.Single(MapTileTemplateParser.Parse(fixedUrl).NormalizedTemplates));
    }
}
