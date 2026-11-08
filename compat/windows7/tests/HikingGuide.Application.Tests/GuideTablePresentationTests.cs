using HikingGuide.Application.Guides;
using HikingGuide.Core.Guides;

namespace HikingGuide.Application.Tests;

public sealed class GuideTablePresentationTests
{
    [Theory]
    [InlineData("Elevation", 12.5, "13")]
    [InlineData("Elevation", -12.5, "-13")]
    [InlineData("CumulativeAscent", 120.49, "120")]
    [InlineData("SegmentDescent", 10.5, "11")]
    [InlineData("DistanceFromStart", 12.25, "12.3")]
    [InlineData("UphillDistance", 0.04, "0.0")]
    [InlineData("FlatDistance", 0.05, "0.1")]
    [InlineData("PlannedDuration", 1.234, "1.23")]
    public void DisplayRoundingIsAwayFromZeroAndKeySpecific(string key, double value, string expected) =>
        Assert.Equal(expected, GuideTablePresentation.CellNumber(key, value));

    [Fact] public void BoundaryCellHidesSummaryButHoverKeepsFullOriginal()
    {
        const string full = "D1终点\nD1距离25.34KM，爬升2668米，下降2294米，计划总用时10.25h\nD2起点";
        Assert.Equal("D1终点\nD2起点", GuideTablePresentation.BoundaryLabel(full));
        Assert.Equal(full, GuideTablePresentation.BoundaryTooltip(full));
        Assert.DoesNotContain("点击设置", GuideTablePresentation.BoundaryTooltip("D1起点"));
        Assert.Contains("点击设置", GuideTablePresentation.BoundaryTooltip(""));
    }
    [Fact]
    public void StatusUsesOneSeparatorAndKeepsInternalPunctuation()
    {
        Assert.Equal("统计已更新；DEM完整度100.0%；部分计划未填写，抵达留空；路况未填写",
            GuideTablePresentation.JoinStatusMessages(["统计已更新 ·", "DEM完整度100.0%", "部分计划未填写，抵达留空。", "路况未填写。；", " ", "路况未填写。"]));
    }
    [Fact]
    public void StatusWithoutWarningsHasNoTrailingSeparator() =>
        Assert.Equal("统计已更新", GuideTablePresentation.JoinStatusMessages(["统计已更新。", ""]));
    [Theory]
    [InlineData("Plan:", "行进方案1", false)]
    [InlineData("Arrival:", "方案1计划抵达时间", false)]
    [InlineData("Location:", "营地", true)]
    public void DynamicColumnsShareWidthAcrossProjectIds(string prefix, string header, bool location)
    {
        var a = new GuideOutputColumn(prefix + Guid.NewGuid(), header, "prototype", Location: location);
        var b = a with { Key = prefix + Guid.NewGuid() };
        Assert.Equal(GuideTablePresentation.ColumnWidthKey(a), GuideTablePresentation.ColumnWidthKey(b));
    }
    [Fact]
    public void DifferentLocationsAndPlanSlotsNeverShareWidth()
    {
        var a = new GuideOutputColumn("Location:" + Guid.NewGuid(), "营地", "prototype", Location: true);
        Assert.NotEqual(GuideTablePresentation.ColumnWidthKey(a), GuideTablePresentation.ColumnWidthKey(a with { Header = "下撤" }));
        a = new("Plan:" + Guid.NewGuid(), "行进方案1", "prototype");
        Assert.NotEqual(GuideTablePresentation.ColumnWidthKey(a), GuideTablePresentation.ColumnWidthKey(a with { Header = "行进方案2" }));
    }
}
