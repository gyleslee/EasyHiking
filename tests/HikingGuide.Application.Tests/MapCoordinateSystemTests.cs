using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class MapCoordinateSystemTests
{
    [Fact]public void UserAutonaviTemplateExpandsFourServersAndPreservesRaw()
    {
        const string raw="http://webst{$s}.is.autonavi.com/appmaptile?x={x}&y={y}&z={z}&lang=zh_cn&size=1&scale=1&style=8";
        var parsed=MapTileTemplateParser.Parse(raw);
        Assert.Equal(raw,parsed.RawTemplate);Assert.Equal(new[]{"01","02","03","04"},parsed.ServerTokens);
        Assert.Contains("webst04.is.autonavi.com",parsed.NormalizedTemplates[3]);Assert.DoesNotContain("{$s}",parsed.NormalizedTemplates[0]);
    }
    [Fact]public void CoordinateChangesInvalidateVirtualUrlsButKeepRawCacheReusable()
    {
        var source=new MapSourceDefinition{UrlTemplate="https://tiles.example/{z}/{x}/{y}",CoordinateSystem=MapSourceCoordinateSystem.Wgs84};
        var registry=new MapTileRequestRegistry();
        Uri Register()=>new(MapTileTemplateParser.BuildTileUrl(registry.Register(MapLayerStackService.Build(new(){BaseSourceId=source.Id},[source]))[0].Templates[0],16,53940,26421));
        var old=Register();using var normal=registry.Resolve(old)!;
        Assert.False(normal.Options.TryGetValue(MapTileRequestRegistry.ReprojectionRequestKey,out _));
        Assert.True(normal.Options.TryGetValue(MapTileRequestRegistry.CacheRequestKey,out var raw));
        source.CoordinateSystem=MapSourceCoordinateSystem.Gcj02;var updated=Register();Assert.NotEqual(old,updated);Assert.Null(registry.Resolve(old));
        using var gcj=registry.Resolve(updated)!;Assert.True(gcj.Options.TryGetValue(MapTileRequestRegistry.ReprojectionRequestKey,out var correction));
        Assert.Equal(MapSourceCoordinateSystem.Gcj02,correction!.Source.CoordinateSystem);
        gcj.Options.TryGetValue(MapTileRequestRegistry.CacheRequestKey,out var sharedRaw);Assert.Equal(raw,sharedRaw);
        source.Headers["Authorization"]="new";Assert.False(correction.Source.Headers.ContainsKey("Authorization"));
    }
    [Fact]public void UnknownCoordinateSystemRejectedAndCopyPreservesIt()
    {
        var source=new MapSourceDefinition{UrlTemplate="https://tiles.example/{z}/{x}/{y}",CoordinateSystem=MapSourceCoordinateSystem.Bd09};
        Assert.Equal(MapSourceCoordinateSystem.Bd09,source.DeepCopy().CoordinateSystem);
        source.CoordinateSystem=(MapSourceCoordinateSystem)99;Assert.False(new MapSourceTemplateService().Validate(source).IsValid);
    }
}
