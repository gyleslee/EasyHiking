using System.Text;
using HikingGuide.Application.Routing;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class ChinaTileManifestTests
{
    [Fact]
    public void ApprovedRd5Manifest_ContainsExactlyTheSeventyConfirmedFiles()
    {
        var assembly = typeof(ChinaTileManifestTests).Assembly;
        using var input = assembly.GetManifestResourceStream(Assert.Single(assembly.GetManifestResourceNames(), n => n.EndsWith("ChinaRd5Tiles.txt", StringComparison.Ordinal)))!;
        using var buffer = new MemoryStream(); input.CopyTo(buffer);
        var manifest = ChinaTileManifest.ParseRd5(buffer.ToArray());
        Assert.Equal(70, manifest.SourceLineCount);
        Assert.Equal(70, manifest.Tiles.Count);
        Assert.Equal(ChinaTileManifest.ApprovedRd5SourceSha256, manifest.SourceSha256);
        Assert.True(manifest.IsApprovedRd5Range);
        foreach (var file in new[] { "E110_N10.rd5", "E115_N10.rd5", "E115_N15.rd5" })
            Assert.Contains(manifest.Tiles, t => t.FileName == file);
        Assert.Equal(manifest.Tiles.OrderBy(t => t.SouthLatitude).ThenBy(t => t.WestLongitude), manifest.Tiles);
    }

    [Fact]
    public void Rd5Manifest_PreservesExactFiveDegreeTilesWithoutExpandingNeighbors()
    {
        var manifest = ChinaTileManifest.ParseRd5(Encoding.UTF8.GetBytes("\uFEFFE110_N0.rd5\r\nE115_N10.rd5\r\n"));
        Assert.Equal(new[] { "E110_N0.rd5", "E115_N10.rd5" }, manifest.Tiles.Select(t => t.FileName));
        Assert.Equal(0, manifest.Tiles[0].Bounds.MinLatitude);
        Assert.False(manifest.IsApprovedRd5Range);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("N24E113")]
    [InlineData("E111_N10.rd5")]
    [InlineData("E110_N10.rd5\nE110_N10.rd5")]
    [InlineData("E110_N10.rd5\nwrong.rd5")]
    [InlineData("E110_N10.rd5\n../E115_N10.rd5")]
    public void Rd5Manifest_RejectsInvalidOrDuplicateFiles(string text)
        => Assert.Throws<FormatException>(() => ChinaTileManifest.ParseRd5(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Rd5Manifest_RejectsInvalidUtf8()
        => Assert.Throws<DecoderFallbackException>(() => ChinaTileManifest.ParseRd5([0xff]));

    [Fact]
    public void HistoricalOneDegreeManifest_RemainsReadableButIsNotCurrentApprovedRange()
    {
        var assembly = typeof(ChinaTileManifestTests).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(),
            item => item.EndsWith("China1DegreeTiles.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var manifest = ChinaTileManifest.Parse(buffer.ToArray());
        Assert.Equal(1138, manifest.SourceLineCount);
        Assert.Equal("49E35C4156D9DFA14525B8BE03185C1FFCDA630CC23D8B153A3E19CB26286ABC",
            manifest.SourceSha256);
        Assert.Equal(67, manifest.Tiles.Count);
        Assert.False(manifest.IsApprovedRd5Range);
        Assert.Contains(manifest.Tiles, tile => tile.FileName == "E110_N20.rd5");
        Assert.Contains(manifest.Tiles, tile => tile.FileName == "E115_N25.rd5");
    }

    [Fact]
    public void ManifestOrderAndDuplicateLines_DoNotChangeDerivedSet()
    {
        var forward = ChinaTileManifest.Parse(Encoding.UTF8.GetBytes("N24E113\nN25E115\nN24E113"));
        var reverse = ChinaTileManifest.Parse(Encoding.UTF8.GetBytes("N25E115\nN24E113"));
        Assert.Equal(forward.Tiles, reverse.Tiles);
    }
}
