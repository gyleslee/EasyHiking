using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HikingGuide.Infrastructure.Projects;

if(args.Length!=2) throw new ArgumentException("用法：生命周期校验 <只读项目副本> <输出报告>");
var source=Path.GetFullPath(args[0]); var output=Path.GetFullPath(args[1]);
if(!source.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase)||!output.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("只允许隔离副本");
async Task<string> FileHash(){await using var s=File.OpenRead(source);return Convert.ToHexString(await SHA256.HashDataAsync(s));}
var before=await FileHash();
var allocated=GC.GetTotalAllocatedBytes(true); var clock=Stopwatch.StartNew();
var project=await new ProjectFileService().LoadAsync(source);
var elapsed=clock.Elapsed.TotalMilliseconds;var bytes=GC.GetTotalAllocatedBytes(true)-allocated;
async Task<string> Digest<T>(T value)
{
    using var hash=SHA256.Create();await using var sink=new CryptoStream(Stream.Null,hash,CryptoStreamMode.Write);
    await JsonSerializer.SerializeAsync(sink,value);await sink.FlushFinalBlockAsync();return Convert.ToHexString(hash.Hash!);
}
// 既有模型在装载集合时会更新ModifiedAt；比较持久业务内容，不比较该运行时装载时间。
var content=await Digest(new{project.Id,project.Name,project.Description,project.CreatedAt,project.ProjectVersion,
    project.Tracks,project.ActiveTrackId,project.MainGuideTrackId,project.Waypoints,project.GuideNodes,project.GuideSegments,project.RetiredGuideSegments,
    project.ManualData,project.GuideData,project.AnalysisProfile,project.AnalysisResult,project.Settings,project.RouteControlPoints,
    project.ElevationAnalyses,project.MediaBlobs,project.MediaAttachments,project.ExternalMediaReferences});
var components=new Dictionary<string,string>();
var sections=new Dictionary<string,object?>
{
    ["Tracks"]=project.Tracks,["Waypoints"]=project.Waypoints,["GuideNodes"]=project.GuideNodes,["GuideSegments"]=project.GuideSegments,
    ["RetiredGuideSegments"]=project.RetiredGuideSegments,["ManualData"]=project.ManualData,["GuideData"]=project.GuideData,
    ["AnalysisProfile"]=project.AnalysisProfile,["AnalysisResult"]=project.AnalysisResult,["Settings"]=project.Settings,
    ["RouteControlPoints"]=project.RouteControlPoints,["ElevationAnalyses"]=project.ElevationAnalyses,["MediaBlobs"]=project.MediaBlobs,
    ["MediaAttachments"]=project.MediaAttachments,["ExternalMediaReferences"]=project.ExternalMediaReferences
};
foreach(var pair in sections) components[pair.Key]=await Digest(pair.Value);
var unchanged=before==await FileHash();
await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=unchanged,Milliseconds=elapsed,AllocatedBytes=bytes,ContentSha256=content,
    LoadedModifiedAt=project.ModifiedAt,Components=components,Tracks=project.Tracks.Count,Points=project.Tracks.Sum(t=>t.GetPointCount()),Analyses=project.ElevationAnalyses.Count,
    Samples=project.ElevationAnalyses.Sum(a=>(long)a.Series.Count),SourceUnchanged=unchanged},new JsonSerializerOptions{WriteIndented=true}));
