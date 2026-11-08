using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.TrackFiles;

namespace HikingGuide.Infrastructure.Tests;

public sealed class P5MarkerTypeTests
{
    [Theory]
    [InlineData("summit", WaypointType.Summit)]
    [InlineData("parking", WaypointType.Parking)]
    [InlineData("accommodation", WaypointType.Accommodation)]
    [InlineData("public transport", WaypointType.PublicTransport)]
    [InlineData("unknown-special-type", WaypointType.Normal)]
    public async Task GpxExplicitTypeMapsWithoutGuessingName(string sourceType, WaypointType expected)
    {
        var file = Path.Combine(Path.GetTempPath(), $"p5-type-{Guid.NewGuid():N}.gpx");
        try
        {
            await File.WriteAllTextAsync(file, $"<gpx version=\"1.1\" creator=\"test\" xmlns=\"http://www.topografix.com/GPX/1/1\"><wpt lat=\"30\" lon=\"110\"><name>起点：名字不应决定类型</name><type>{sourceType}</type></wpt></gpx>");
            var result = await new GpxTrackImporter().ImportAsync(file);
            var waypoint = Assert.Single(result.Waypoints);
            Assert.Equal(expected, waypoint.Type);
            if (expected == WaypointType.Normal) Assert.Contains(waypoint.OriginalAttributes.Values, value => value?.Contains(sourceType) == true);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Theory]
    [InlineData("parking", WaypointType.Parking)]
    [InlineData("summit", WaypointType.Summit)]
    [InlineData("not-a-known-code", WaypointType.Normal)]
    public async Task KmlExtendedDataTypeMapsOnlyKnownExplicitValues(string sourceType, WaypointType expected)
    {
        var file = Path.Combine(Path.GetTempPath(), $"p5-type-{Guid.NewGuid():N}.kml");
        try
        {
            await File.WriteAllTextAsync(file, $"<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><Placemark><name>山峰名称不应决定类型</name><Point><coordinates>110,30</coordinates></Point><ExtendedData><Data name=\"markerType\"><value>{sourceType}</value></Data></ExtendedData></Placemark></Document></kml>");
            var waypoint = Assert.Single((await new KmlTrackImporter().ImportAsync(file)).Waypoints);
            Assert.Equal(expected, waypoint.Type);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
