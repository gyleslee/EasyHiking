using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Projects;

if (args.Length < 2)
{
    Console.Error.WriteLine("用法：HikingGuide.P4Verifier <P3项目文件> <输出目录>");
    return 2;
}

var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!File.Exists(input)) { Console.Error.WriteLine("找不到 P3 真实项目文件。"); return 3; }
Directory.CreateDirectory(output);
var reportPath = Path.Combine(output, "P4真实项目核心验证.json");
var savedProjectPath = Path.Combine(output, "环船百大纵走_P4地图状态验证.hikeproj");
if (File.Exists(reportPath) || File.Exists(savedProjectPath)) { Console.Error.WriteLine("验证输出已存在，请使用空目录。"); return 4; }

var inputHashBefore = await HashFileAsync(input);
var projectFiles = new ProjectFileService();
var openTimer = Stopwatch.StartNew();
var project = await projectFiles.LoadAsync(input);
openTimer.Stop();
var pointCount = project.Tracks.Sum(track => track.GetPointCount());
var originalGeometryHash = GeometryHash(project);
var manualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(project.ManualData))));

var profile = MapDisplayProfile.Default;
var simplifier = new DouglasPeuckerTrackDisplaySimplifier(profile);
var displayService = new MapDisplayDataService(simplifier);
var tierResults = new List<object>();
MapDisplayData? middleData = null;
foreach (var zoom in new[] { 6d, 10d, 14d, 17d, 19d })
{
    var timer = Stopwatch.StartNew();
    var data = displayService.Create(project, zoom, (long)zoom);
    timer.Stop();
    if (zoom == 14) middleData = data;
    var geoTimer = Stopwatch.StartNew();
    var tierGeoJson = new MapGeoJsonBuilder().Build(data);
    geoTimer.Stop();
    tierResults.Add(new
    {
        缩放级别 = zoom,
        容差米 = profile.GetToleranceMeters(zoom),
        原始点数 = data.Tracks.Sum(track => track.OriginalPointCount),
        显示点数 = data.Tracks.Sum(track => track.DisplayPointCount),
        简化毫秒 = timer.Elapsed.TotalMilliseconds,
        GeoJSON毫秒 = geoTimer.Elapsed.TotalMilliseconds,
        GeoJSON字节 = Encoding.UTF8.GetByteCount(tierGeoJson.TracksGeoJson)
    });
}

var geoJsonTimer = Stopwatch.StartNew();
var geoJson = new MapGeoJsonBuilder().Build(middleData!);
geoJsonTimer.Stop();
var geometryUnchanged = originalGeometryHash == GeometryHash(project);

var sourceId = Guid.Parse("6c785f14-4c4f-45af-9e70-d8aa7af5f76c");
project.ExecuteAtomicUpdate(value =>
{
    value.Settings.Map.SelectedMapSourceId = sourceId;
    value.Settings.Map.Center = new GeoCoordinate(23.123456, 113.654321);
    value.Settings.Map.Zoom = 15.25;
    value.Settings.Map.Bearing = 12.5;
    value.Settings.Map.Pitch = 22;
    value.MarkDirty();
});
await projectFiles.SaveAsync(project, savedProjectPath, true);
var reopened = await projectFiles.LoadAsync(savedProjectPath);
var mapStatePreserved = reopened.Settings.Map.SelectedMapSourceId == sourceId &&
    reopened.Settings.Map.Center == new GeoCoordinate(23.123456, 113.654321) &&
    reopened.Settings.Map.Zoom == 15.25 && reopened.Settings.Map.Bearing == 12.5 && reopened.Settings.Map.Pitch == 22;
var manualPreserved = manualHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reopened.ManualData))));
var savedGeometryPreserved = originalGeometryHash == GeometryHash(reopened);
var inputHashAfter = await HashFileAsync(input);

var report = new
{
    输入项目 = input,
    输入项目SHA256 = inputHashBefore,
    输入项目未改写 = inputHashBefore == inputHashAfter,
    项目打开毫秒 = openTimer.Elapsed.TotalMilliseconds,
    轨迹数量 = project.Tracks.Count,
    轨迹段数量 = project.Tracks.Sum(track => track.Segments.Count),
    原始轨迹点数 = pointCount,
    标注点数量 = project.Waypoints.Count,
    分级简化 = tierResults,
    GeoJSON生成毫秒 = geoJsonTimer.Elapsed.TotalMilliseconds,
    轨迹GeoJSON字节 = Encoding.UTF8.GetByteCount(geoJson.TracksGeoJson),
    标注GeoJSON字节 = Encoding.UTF8.GetByteCount(geoJson.WaypointsGeoJson),
    显示处理后内存中原轨迹不变 = geometryUnchanged,
    地图状态保存重开保持 = mapStatePreserved,
    保存副本中原轨迹保持 = savedGeometryPreserved,
    人工攻略内容保持 = manualPreserved,
    进程峰值工作集字节 = Process.GetCurrentProcess().PeakWorkingSet64,
    验证通过 = pointCount == 125_910 && inputHashBefore == inputHashAfter && geometryUnchanged && mapStatePreserved && savedGeometryPreserved && manualPreserved
};

await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
}), new UTF8Encoding(false));
Console.WriteLine(await File.ReadAllTextAsync(reportPath));
return report.验证通过 ? 0 : 5;

static async Task<string> HashFileAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

static string GeometryHash(HikingGuide.Core.Models.HikingProject project)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var track in project.Tracks)
    {
        hash.AppendData(track.Id.ToByteArray());
        foreach (var segment in track.Segments)
        {
            hash.AppendData(segment.Id.ToByteArray());
            foreach (var point in segment.Points)
            {
                hash.AppendData(point.Id.ToByteArray());
                hash.AppendData(BitConverter.GetBytes(point.Longitude));
                hash.AppendData(BitConverter.GetBytes(point.Latitude));
                hash.AppendData(BitConverter.GetBytes(point.SourceElevation ?? double.NaN));
                hash.AppendData(BitConverter.GetBytes(point.DemElevation ?? double.NaN));
            }
        }
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}
