using System.Text.Json;
using HikingGuide.Core.Models;
namespace HikingGuide.Core.Tests;
public sealed class OsmRoadSettingsTests
{
    [Fact]public void OneRowPerRoadTypeAndOriginalThemeZoomBandsRemainSeparate()
    {
        var rows=OsmRoadDefaults.CreateStyles();Assert.InRange(rows.Count,20,30);Assert.Equal(rows.Count,rows.Select(s=>s.Name).Distinct().Count());
        Assert.Single(rows,s=>s.Name=="徒步小径");Assert.Single(rows,s=>s.Name=="机耕道");Assert.Equal(13,rows.Single(s=>s.Id=="highway-path").MinZoom);Assert.Equal(12,rows.Single(s=>s.Id=="highway-track").MinZoom);
        var strokes=OsmRoadDefaults.ThemeStrokes();Assert.Equal(165,strokes.Count);Assert.Contains(strokes,s=>s.MinZoom==15&&s.MaxZoom==15);Assert.Contains(strokes,s=>s.MinZoom==16&&s.MaxZoom==16);Assert.All(strokes,s=>Assert.False(s.CasingEnabled));
    }
    [Fact]public void ReimportDiscardsOldEditableAppearanceAndDuplicateRowsOnlyOnce()
    {
        var a=new OsmRoadSettings(2){Enabled=true,Opacity=.37,CacheCapacityMb=123,MinimumZoom=11};a.Styles.Add(a.Styles[0]);a.UpgradeDisplayRules();
        Assert.True(a.IsValid);Assert.Equal(OsmRoadDefaults.DisplayRulesVersion,a.DisplayRulesVersion);Assert.True(a.Enabled);Assert.Equal(.37,a.Opacity);Assert.Equal(123,a.CacheCapacityMb);Assert.Equal(11,a.MinimumZoom);
        a.Styles.Single(s=>s.Id=="highway-path").MinZoom=11;a.RememberDisplayLevels();a.UpgradeDisplayRules();Assert.Equal(11,a.Styles.Single(s=>s.Id=="highway-path").MinZoom);
    }
    [Fact]public void LockedAppearanceCannotEnterRenderStylesAndSavedZoomSurvivesRestore()
    {
        var a=new OsmRoadSettings();var row=a.Styles.Single(s=>s.Id=="highway-path");row.MinZoom=11;row.MaxZoom=20;row.Color="#123456";row.Width=30;
        a.RememberDisplayLevels();var reloaded=a.DeepCopy();reloaded.RestoreDefaultStyles();row=reloaded.Styles.Single(s=>s.Id=="highway-path");Assert.Equal(11,row.MinZoom);Assert.Equal(20,row.MaxZoom);Assert.NotEqual("#123456",row.Color);
        var render=OsmRoadDefaults.BuildRenderStyles(a);Assert.DoesNotContain(render,s=>s.Color=="#123456"||s.Width==30);Assert.All(render,s=>Assert.True(s.IsValid));
    }
    [Fact]public void JsonRoundTripPreservesTypeRangesAndDefaultLevels()
    {var a=new OsmRoadSettings{Enabled=true,Opacity=.42};a.Styles[0].MaxZoom=20;a.RememberDisplayLevels();Assert.Equal(JsonSerializer.Serialize(a),JsonSerializer.Serialize(a.DeepCopy()));}
    [Fact]public void EveryRoadTypeHasReadOnlyThemePreviewWithVisibleInk()
    {foreach(var type in OsmRoadDefaults.CreateStyles())Assert.Contains(OsmRoadDefaults.PreviewStrokes(type.Id),s=>s.Alpha>0&&!s.Color.Equals("#FFFFFF",StringComparison.OrdinalIgnoreCase));}
    private static bool Matches(OsmRoadStyle stroke,Dictionary<string,string> tags)=>stroke.Conditions.All(c=>c.Key.Split('|').Any(k=>c.Values.Split('|').Any(v=>v=="~"?!tags.ContainsKey(k):v=="*"?tags.ContainsKey(k):tags.GetValueOrDefault(k)==v)));
    [Theory][InlineData(false)][InlineData(true)]
    public void EveryRoadTypeAndSurfaceHasVisibleInkAtEachConfiguredZoom(bool extended)
    {
        var settings=new OsmRoadSettings();if(extended)foreach(var row in settings.Styles){row.MinZoom=10;row.MaxZoom=22;}
        var render=OsmRoadDefaults.BuildRenderStyles(settings);
        foreach(var row in settings.Styles)
        foreach(var variant in new[]{"normal","bridge","tunnel","grade1","grade2","grade3","grade4","grade5","sac"})
        {
            var tags=new Dictionary<string,string>{{"highway",row.Conditions[0].Values}};
            if(variant is "bridge" or "tunnel")tags[variant]="yes";
            if(variant.StartsWith("grade"))tags["tracktype"]=variant;
            if(variant=="sac")tags["sac_scale"]="mountain_hiking";
            for(var zoom=Math.Max(10,row.MinZoom);zoom<=row.MaxZoom;zoom++)
                Assert.True(render.Any(s=>s.MinZoom<=zoom&&s.MaxZoom>=zoom&&s.Color.ToUpperInvariant()!="#FFFFFF"&&s.Alpha>0&&Matches(s,tags)),row.Id+" "+variant+" Z"+zoom+"缺少可见主题线条");
        }
    }
    [Fact]public void PathHasExactlyOneBlackDetailBandAtEveryZoomAndNoLastBandOverlap()
    {
        var render=OsmRoadDefaults.BuildRenderStyles(new());var tags=new Dictionary<string,string>{{"highway","path"}};
        for(var zoom=13;zoom<=22;zoom++)
        {var black=render.Where(s=>s.MinZoom<=zoom&&s.MaxZoom>=zoom&&s.Color=="#000000"&&Matches(s,tags)).ToArray();Assert.Single(black);Assert.Equal(zoom<15?.45:zoom<16?.40:zoom<17?.35:.30,black[0].Width);}
    }
    [Theory][InlineData("0,3",true)][InlineData("实线",false)][InlineData("",true)][InlineData("0,0",false)][InlineData("NaN,2",false)][InlineData("-1,2",false)][InlineData("8,5,2,5",true)]
    public void DashValidation(string text,bool valid)=>Assert.Equal(valid,OsmRoadStyle.ValidDash(text));
    [Theory][InlineData("#123456",true)][InlineData("#FF000000",false)][InlineData("red",false)][InlineData("#GG1122",false)]
    public void ColorValidation(string text,bool valid)=>Assert.Equal(valid,OsmRoadStyle.ValidColor(text));
}
