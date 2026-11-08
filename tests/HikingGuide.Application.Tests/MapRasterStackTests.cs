using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;
public sealed class MapRasterStackTests
{
    private static MapSourceDefinition Source(MapSourceUsage usage=MapSourceUsage.Both)=>new(){UrlTemplate="https://tiles.example/{zoom}/{x}/{y}.png",Usage=usage};
    [Theory]
    [InlineData("a,b,c",3)] [InlineData("a, b, c",3)] [InlineData("server1,server2",2)] [InlineData("1,2,3",3)]
    public void SwitchExpandsAndPreservesRaw(string values,int count)
    {var raw=$"https://{{switch:{values}}}.example.com/{{zoom}}/{{x}}/{{y}}.png";var p=MapTileTemplateParser.Parse(raw);Assert.Equal(raw,p.RawTemplate);Assert.Equal(count,p.NormalizedTemplates.Count);Assert.All(p.NormalizedTemplates,t=>{Assert.Contains("{z}",t);Assert.DoesNotContain("switch",t);});}
    [Theory]
    [InlineData("a")] [InlineData("a,,b")] [InlineData("a,b,a")] [InlineData("")] [InlineData("a,{b}")] [InlineData("a,b/c")] [InlineData("a,b\\c")] [InlineData("a,b?c")] [InlineData("a,b#c")] [InlineData("a,b\nc")]
    public void InvalidSwitchRejected(string token)=>Assert.Throws<ArgumentException>(()=>MapTileTemplateParser.Parse($"https://example.com/{{switch:{token}}}/{{z}}/{{x}}/{{y}}"));
    [Fact]public void MultipleSwitchRejected()=>Assert.Contains("只支持一个",Assert.Throws<ArgumentException>(()=>MapTileTemplateParser.Parse("https://{switch:a,b}.{switch:1,2}/tiles/{z}/{x}/{y}" )).Message);
    [Fact]public void SwitchWorksInPath()=>Assert.Equal("https://example.com/a/{z}/{x}/{y}",MapTileTemplateParser.Parse("https://example.com/{switch:a,b}/{z}/{x}/{y}").NormalizedTemplates[0]);
    [Fact]public void OrdinaryLegacyStillSingle()=>Assert.Single(MapTileTemplateParser.Parse("https://tile.openstreetmap.de/{zoom}/{x}/{y}.png").NormalizedTemplates);
    [Theory] [InlineData("file:///a/{z}/{x}/{y}")] [InlineData("ftp://host/{z}/{x}/{y}")] [InlineData("javascript:{z}{x}{y}")] [InlineData("data:{z}{x}{y}")] [InlineData("https://example.com/{s}/{z}/{x}/{y}")] [InlineData("https://example.com/{z}/{x}/{-y}")]
    public void ForbiddenUrlsRejected(string value)=>Assert.Throws<ArgumentException>(()=>MapTileTemplateParser.Parse(value));
    [Fact]public void LegacyUsageMigratesWithoutExplicitUsage(){Assert.Equal(MapSourceUsage.Both,new MapSourceDefinition().EffectiveUsage);Assert.Equal(MapSourceUsage.Overlay,new MapSourceDefinition{Role=MapSourceRole.Overlay}.EffectiveUsage);}
    [Fact]public void StackKeepsThreeSlotsInOrder(){var a=Source();var b=Source();var c=Source();var s=new GlobalMapLayerStackSettings{BaseSourceId=a.Id,Overlay1SourceId=b.Id,Overlay2SourceId=c.Id,Overlay1Visible=false,Overlay2Visible=true,Overlay1Opacity=.4,Overlay2Opacity=.7};var slots=MapLayerStackService.Build(s,[a,b,c]);Assert.Equal(new[]{"base","overlay1","overlay2"},slots.Select(v=>v.Slot));Assert.False(slots[1].Visible);Assert.True(slots[2].Visible);Assert.Equal(new[]{1d,.4,.7},slots.Select(v=>v.Opacity));}
    [Fact]public void DuplicateEvenWhenHiddenRejected(){var a=Source();var s=new GlobalMapLayerStackSettings{BaseSourceId=a.Id,Overlay1SourceId=a.Id,Overlay1Visible=false};Assert.Contains("已经",MapLayerStackService.Validate(s,[a]));}
    [Fact]public void DuplicateOverlaysRejected(){var a=Source();var b=Source();Assert.NotNull(MapLayerStackService.Validate(new(){BaseSourceId=a.Id,Overlay1SourceId=b.Id,Overlay2SourceId=b.Id},[a,b]));}
    [Fact]public void NormalizePrioritizesBaseThenOverlay1(){var a=Source();var b=Source();var s=MapLayerStackService.Normalize(new(){BaseSourceId=a.Id,Overlay1SourceId=b.Id,Overlay2SourceId=b.Id,Overlay2Visible=true},[a,b]);Assert.Equal(b.Id,s.Overlay1SourceId);Assert.Null(s.Overlay2SourceId);Assert.False(s.Overlay2Visible);}
    [Fact]public void CorruptStackClampsAndFallsBack(){var a=Source();a.Id=GlobalMapLayerStackSettings.GermanyId;var s=MapLayerStackService.Normalize(new(){BaseSourceId=Guid.NewGuid(),BaseOpacity=2,Overlay1Opacity=-1,Overlay1SourceId=Guid.NewGuid(),Overlay1Visible=true},[a]);Assert.Equal(a.Id,s.BaseSourceId);Assert.Equal(1,s.BaseOpacity);Assert.Equal(0,s.Overlay1Opacity);Assert.Null(s.Overlay1SourceId);Assert.False(s.Overlay1Visible);}
    [Fact]public void UsageAndEnabledFilter(){var a=Source(MapSourceUsage.Overlay);Assert.False(MapLayerStackService.IsAvailable(a,MapSourceUsage.Base));Assert.True(MapLayerStackService.IsAvailable(a,MapSourceUsage.Overlay));a.Enabled=false;Assert.False(MapLayerStackService.IsAvailable(a,MapSourceUsage.Overlay));}
    [Fact]public void RequestHeadersIsolatedForIdenticalHostAndPath()
    {
        var a=Source();var b=Source();a.Headers["Authorization"]="A-secret";b.Headers["Authorization"]="B-secret";a.UserAgent="agent-a";b.UserAgent="agent-b";
        var registry=new MapTileRequestRegistry();var slots=registry.Register(MapLayerStackService.Build(new(){BaseSourceId=a.Id,Overlay1SourceId=b.Id,Overlay1Visible=true},[a,b]));
        using var ra=registry.Resolve(new(MapTileTemplateParser.BuildTileUrl(slots[0].Templates[0],1,1,1)));using var rb=registry.Resolve(new(MapTileTemplateParser.BuildTileUrl(slots[1].Templates[0],1,1,1)));
        Assert.Equal(ra!.RequestUri,rb!.RequestUri);Assert.Equal("A-secret",Assert.Single(ra.Headers.GetValues("Authorization")));Assert.Equal("B-secret",Assert.Single(rb.Headers.GetValues("Authorization")));Assert.Equal("agent-b",rb.Headers.UserAgent.ToString());
    }
    [Fact]public void OldRequestsNeverGetNewCredentials(){var a=Source();a.Headers["Authorization"]="old";var registry=new MapTileRequestRegistry();var slots=registry.Register(MapLayerStackService.Build(new(){BaseSourceId=a.Id},[a]));var uri=new Uri(MapTileTemplateParser.BuildTileUrl(slots[0].Templates[0],1,1,1));a.Headers["Authorization"]="new";registry.Register(MapLayerStackService.Build(new(){BaseSourceId=a.Id},[a]));Assert.Null(registry.Resolve(uri));}
    [Fact]public void SwitchServersEachGetSameOwnProfile(){var a=Source();a.UrlTemplate="https://{switch:a,b,c}.example.com/{z}/{x}/{y}";a.Headers["X-Test"]="only-a";var registry=new MapTileRequestRegistry();var slots=registry.Register(MapLayerStackService.Build(new(){BaseSourceId=a.Id},[a]));foreach(var t in slots[0].Templates){using var r=registry.Resolve(new(MapTileTemplateParser.BuildTileUrl(t,1,1,1)));Assert.Equal("only-a",Assert.Single(r!.Headers.GetValues("X-Test")));}}
}
