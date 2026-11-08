using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class MapSourceTemplateServiceTests
{
    private readonly MapSourceTemplateService _service = new();

    [Theory]
    [InlineData("https://tiles.example/{z}/{x}/{y}.png")]
    [InlineData("http://tiles.example/{zoom}/{x}/{y}.jpg?key=abc")]
    public void Validate_AcceptsSupportedTemplates(string url)
    {
        Assert.True(_service.Validate(Create(url)).IsValid);
    }

    [Theory]
    [InlineData("https://tiles.example/{z}/{y}.png")]
    [InlineData("https://tiles.example/{z}/{x}.png")]
    [InlineData("https://tiles.example/{x}/{y}.png")]
    [InlineData("file:///tiles/{z}/{x}/{y}.png")]
    [InlineData("https://{s}.example/{z}/{x}/{y}.png")]
    [InlineData("https://tiles.example/{z}/{x}/{-y}.png")]
    [InlineData("https://tiles.example/{quadkey}.png?x={x}&y={y}&z={z}")]
    public void Validate_RejectsUnsupportedTemplates(string url)
    {
        Assert.False(_service.Validate(Create(url)).IsValid);
    }

    [Fact]
    public void Validate_RejectsInvalidRangesAndHeaderInjection()
    {
        var source = Create("https://tiles.example/{z}/{x}/{y}.png");
        source.MinZoom = 20;
        source.MaxZoom = 10;
        source.Opacity = 1.1;
        source.Headers["X-Test"] = "value\r\nInjected: true";

        var result = _service.Validate(source);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 3);
    }

    [Fact]
    public void NormalizeAndBuildTileUrl_SupportZoomAlias()
    {
        const string template = "https://tiles.example/{zoom}/{x}/{y}.png";
        Assert.Equal("https://tiles.example/{z}/{x}/{y}.png", _service.NormalizeForMapLibre(template));
        Assert.Equal("https://tiles.example/12/345/678.png", _service.BuildTileUrl(template, 12, 345, 678));
    }

    private static MapSourceDefinition Create(string url) => new()
    {
        Name = "测试图源",
        UrlTemplate = url,
        MinZoom = 0,
        MaxZoom = 19,
        Opacity = 1
    };
}
