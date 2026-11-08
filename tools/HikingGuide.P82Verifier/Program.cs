using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;

if (args.Length is not (2 or 3) ||
    (args.Length == 3 && args[2] is not ("active" or "active-kml")))
{
    Console.Error.WriteLine("用法：HikingGuide.P82Verifier <项目.hikeproj> <输出目录> [active|active-kml]");
    return 2;
}
var input = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDirectory);
HikingProject project;
if (Path.GetExtension(input).Equals(".kmz", StringComparison.OrdinalIgnoreCase))
{
    var source = await new KmzTrackImporter().ImportAsync(input);
    project = new HikingProject { Name = Path.GetFileNameWithoutExtension(input) };
    var session = ProjectImportSession.Create(source, input, project);
    new ProjectImportService().Commit(session, project, true);
}
else project = await new ProjectFileService().LoadAsync(input);
var selection = new MergedExportSelection();
foreach (var track in project.Tracks) selection.TrackIds.Add(track.Id);
foreach (var waypoint in project.Waypoints) selection.MarkerIds.Add(waypoint.Id);
foreach (var node in project.GuideNodes) selection.MarkerIds.Add(node.Id);
var activeOnly = args.Length == 3;
var kmlOnly = args.Length == 3 && args[2] == "active-kml";
var format = kmlOnly ? TrackFileFormat.Kml : TrackFileFormat.Kmz;
var output = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) +
    (activeOnly ? "-当前轨迹完整导出" : "-P8-2验证导出") +
    (kmlOnly ? ".kml" : ".kmz"));
var exporter = new ProjectMergedExportService();
if (activeOnly)
{
    var active = project.ActiveTrack ?? throw new InvalidDataException("项目没有当前工作轨迹。");
    await exporter.ExportTrackAsync(project, active, null, format, output, true);
}
else await exporter.ExportAsync(project, selection, format, output, true);
var parsed = kmlOnly ? await new KmlTrackImporter().ImportAsync(output) :
    await new KmzTrackImporter().ImportAsync(output);
using var archive = kmlOnly ? null : ZipFile.OpenRead(output);
var doc = archive?.GetEntry("doc.kml");
if (!kmlOnly && doc is null) throw new InvalidDataException("KMZ 缺少 doc.kml");
var xml = kmlOnly ? await File.ReadAllTextAsync(output) : await ReadKmzXmlAsync(doc!);
var imageSources = Regex.Matches(xml, "<img\\s+src=\"([^\"]+)\"", RegexOptions.IgnoreCase)
    .Select(match => match.Groups[1].Value).ToArray();
var missing = imageSources.Where(source => source.StartsWith("files/", StringComparison.Ordinal) &&
    archive?.GetEntry(source) is null).ToArray();
var owners = project.MediaAttachments.GroupBy(item => item.OwnerId).ToArray();
var expectedOwnerIds = activeOnly
    ? project.Waypoints.Where(item => item.AssociatedTrackId == project.ActiveTrackId).Select(item => item.Id)
        .Concat(project.GuideNodes.Where(item => item.TrackId == project.ActiveTrackId).Select(item => item.Id)).ToHashSet()
    : project.Waypoints.Select(item => item.Id).Concat(project.GuideNodes.Select(item => item.Id)).ToHashSet();
var expectedAttachments = project.MediaAttachments.Count(item => expectedOwnerIds.Contains(item.OwnerId));
var report = new
{
    Source = input,
    ProjectName = project.Name,
    TrackCount = project.Tracks.Count,
    WaypointCount = project.Waypoints.Count,
    GuideNodeCount = project.GuideNodes.Count,
    ActiveOnly = activeOnly,
    ExpectedMarkerCount = expectedOwnerIds.Count,
    ExpectedAttachmentCount = expectedAttachments,
    ImageMarkerCount = owners.Length,
    MaximumImagesOnMarker = owners.Max(group => group.Count()),
    MediaAttachmentCount = project.MediaAttachments.Count,
    BlobCount = project.MediaBlobs.Count,
    ExportedMediaEntries = archive?.Entries.Count(entry => entry.FullName.StartsWith("files/", StringComparison.Ordinal)) ?? 0,
    HtmlImageTagCount = imageSources.Length,
    EscapedImageTagCount = Regex.Matches(xml, "&lt;img|&amp;lt;img", RegexOptions.IgnoreCase).Count,
    EmptyLinkCount = Regex.Matches(xml, "<a\\s+href=\"\"", RegexOptions.IgnoreCase).Count,
    EscapedDivCount = Regex.Matches(xml, "&lt;div|&amp;lt;div", RegexOptions.IgnoreCase).Count,
    MissingMediaPaths = missing,
    ImportedTrackCount = parsed.Tracks.Count,
    ImportedWaypointCount = parsed.Waypoints.Count,
    ImportedAttachmentCount = parsed.MediaAttachments.Count,
    ImportedBlobCount = parsed.MediaBlobs.Count,
    Output = output
};
var reportPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) +
    (activeOnly ? (kmlOnly ? "-当前轨迹KML导出.json" : "-当前轨迹完整导出.json") : "-P8-2验证.json"));
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return missing.Length == 0 && report.EscapedImageTagCount == 0 &&
    report.EmptyLinkCount == 0 && report.EscapedDivCount == 0 &&
    parsed.MediaAttachments.Count == (kmlOnly ? 0 : expectedAttachments) &&
    parsed.Waypoints.Count == expectedOwnerIds.Count ? 0 : 1;

static async Task<string> ReadKmzXmlAsync(ZipArchiveEntry entry)
{
    using var reader = new StreamReader(entry.Open());
    return await reader.ReadToEndAsync();
}
