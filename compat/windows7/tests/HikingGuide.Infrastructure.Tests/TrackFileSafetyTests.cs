using System.Text;
using HikingGuide.Application.TrackFiles;

namespace HikingGuide.Infrastructure.Tests;

public sealed class TrackFileSafetyTests
{
    [Fact]
    public async Task Import_MissingFile_ReturnsFriendlyChineseError()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(context.OutputPath("不存在.gpx"));

        Assert.False(result.IsSuccess);
        Assert.Equal("找不到指定的轨迹文件。", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Import_FormatMismatch_ReturnsFriendlyChineseError()
    {
        using var context = new TrackFileTestContext();
        var disguisedPath = context.OutputPath("错误扩展名.gpx");
        File.Copy(TrackFileTestContext.DataPath("KML", "01_LineString.kml"), disguisedPath);

        var result = await context.Service.ImportAsync(disguisedPath);

        Assert.False(result.IsSuccess);
        Assert.Contains("不是有效的 GPX 文件", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Import_ExtensionMatchingIsCaseInsensitive()
    {
        using var context = new TrackFileTestContext();
        var uppercasePath = context.OutputPath("TRACK.GPX");
        File.Copy(TrackFileTestContext.DataPath("GPX", "01_单轨迹.gpx"), uppercasePath);

        var result = await context.Service.ImportAsync(uppercasePath);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Tracks);
    }

    [Fact]
    public async Task Import_DtdAndExternalEntity_AreRejected()
    {
        using var context = new TrackFileTestContext();
        var secretPath = context.OutputPath("secret.txt");
        var maliciousPath = context.OutputPath("恶意文件.gpx");
        await File.WriteAllTextAsync(secretPath, "不应读取的内容", Encoding.UTF8);
        var uri = new Uri(secretPath).AbsoluteUri;
        await File.WriteAllTextAsync(
            maliciousPath,
            $"<?xml version=\"1.0\"?><!DOCTYPE gpx [<!ENTITY xxe SYSTEM \"{uri}\">]><gpx><metadata><name>&xxe;</name></metadata></gpx>",
            Encoding.UTF8);

        var result = await context.Service.ImportAsync(maliciousPath);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Code == "InvalidXml");
        Assert.DoesNotContain("不应读取的内容", string.Join(" ", result.Metadata.Values));
    }

    [Fact]
    public async Task Export_DefaultDoesNotOverwriteExistingFile()
    {
        using var context = new TrackFileTestContext();
        var output = context.OutputPath("已存在.gpx");
        await File.WriteAllTextAsync(output, "保留内容", Encoding.UTF8);

        var exception = await Assert.ThrowsAsync<TrackExportException>(() =>
            context.Service.ExportAsync(
                TrackFileFormat.Gpx,
                new TrackExportRequest(),
                output));

        Assert.Contains("未获得覆盖确认", exception.Message);
        Assert.Equal("保留内容", await File.ReadAllTextAsync(output, Encoding.UTF8));
    }

    [Fact]
    public async Task ChineseSpaceAndParenthesesPath_WorksForImportAndExport()
    {
        using var context = new TrackFileTestContext();
        var directory = Path.Combine(context.TemporaryDirectory, "徒步攻略", "船底顶（测试）");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "路线 01.kml");
        File.Copy(TrackFileTestContext.DataPath("KML", "06_中文名称.kml"), source);

        var imported = await context.Service.ImportAsync(source);
        var output = Path.Combine(directory, "路线 01 导出.kml");
        await context.Service.ExportAsync(
            TrackFileFormat.Kml,
            new TrackExportRequest
            {
                Tracks = imported.Tracks,
                Waypoints = imported.Waypoints,
                Metadata = imported.Metadata
            },
            output);
        var restored = await context.Service.ImportAsync(output);

        Assert.True(restored.IsSuccess);
        Assert.Equal(imported.TrackPointCount, restored.TrackPointCount);
        Assert.Equal(imported.Waypoints.Count, restored.Waypoints.Count);
    }
}
