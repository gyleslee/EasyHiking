using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Tests;

public sealed class WaypointConversionTests
{
    [Fact]
    public void GuideNode_WaypointRoundTrip_PreservesSharedAndManualData()
    {
        var node = new GuideNode
        {
            Name = "营地",
            Description = "背风处",
            Type = WaypointType.Camp,
            OriginalCoordinate = new GeoCoordinate(30, 110),
            SnappedCoordinate = new GeoCoordinate(30.001, 110.001),
            SourceElevation = 1200,
            DemElevation = 1210,
            Timestamp = new DateTimeOffset(2026, 9, 24, 8, 30, 0, TimeSpan.FromHours(8)),
            OriginalAttributes = new Dictionary<string, string?> { ["symbol"] = "camp" },
            ManualData = new ManualGuideData { Water = "附近有水源", Notes = "人工备注" }
        };

        var restored = node.ToWaypoint().ToGuideNode();

        Assert.Equal(node.Id, restored.Id);
        Assert.Equal(node.Name, restored.Name);
        Assert.Equal(node.Description, restored.Description);
        Assert.Equal(node.Type, restored.Type);
        Assert.Equal(node.OriginalCoordinate, restored.OriginalCoordinate);
        Assert.Equal(node.SnappedCoordinate, restored.SnappedCoordinate);
        Assert.Equal(node.SourceElevation, restored.SourceElevation);
        Assert.Equal(node.DemElevation, restored.DemElevation);
        Assert.Equal(node.Timestamp, restored.Timestamp);
        Assert.Equal("camp", restored.OriginalAttributes["symbol"]);
        Assert.Equal("附近有水源", restored.ManualData?.Water);
        Assert.Equal("人工备注", restored.ManualData?.Notes);
    }
}
