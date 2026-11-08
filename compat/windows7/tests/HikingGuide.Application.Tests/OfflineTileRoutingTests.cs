using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
namespace HikingGuide.Application.Tests;
public sealed class OfflineTileRoutingTests
{
    [Fact] public void BaseAndOverlayRequestsCarryIndependentOfflineIdentitiesAndXyz()
    {
        var a=new MapSourceDefinition{Name="底图",UrlTemplate="https://example.invalid/a/{z}/{x}/{y}.png"};var b=new MapSourceDefinition{Name="叠加",UrlTemplate="https://example.invalid/b/{z}/{x}/{y}.png"};
        var registry=new MapTileRequestRegistry();var slots=registry.Register([new("base",a,[a.UrlTemplate],true,1),new("overlay1",b,[b.UrlTemplate],true,.5)]);
        using var one=registry.Resolve(new Uri(slots[0].Templates[0].Replace("{z}","13").Replace("{x}","100").Replace("{y}","200")))!;
        using var two=registry.Resolve(new Uri(slots[1].Templates[0].Replace("{z}","13").Replace("{x}","100").Replace("{y}","200")))!;
        Assert.True(one.Options.TryGetValue(MapTileRequestRegistry.OfflineRequestKey,out var offline));Assert.Equal(new(OfflineMapSourceIdentity.Fingerprint(a),13,100,200),offline);
        Assert.True(two.Options.TryGetValue(MapTileRequestRegistry.OfflineRequestKey,out var overlay));Assert.NotEqual(offline!.Fingerprint,overlay!.Fingerprint);
    }
    [Fact] public void GcjOfflineIdentityIsSeparateWhileRawOnlineCacheIsShared()
    {var a=new MapSourceDefinition{UrlTemplate="https://example.invalid/{z}/{x}/{y}.png"};var b=a.DeepCopy();b.CoordinateSystem=MapSourceCoordinateSystem.Gcj02;Assert.Equal(OfflineMapSourceIdentity.RawCache(a),OfflineMapSourceIdentity.RawCache(b));Assert.NotEqual(OfflineMapSourceIdentity.Fingerprint(a),OfflineMapSourceIdentity.Fingerprint(b));}
}
