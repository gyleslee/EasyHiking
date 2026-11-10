using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunSplitNamingSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;var folder=Path.GetDirectoryName(output)!;
        try
        {
            var track=new Track{Name="1",Segments=[new(){Name="原轨迹段",Points=Enumerable.Range(0,9).Select(i=>new TrackPoint{Latitude=23,Longitude=113+i*.001,SourceElevation=100+i}).ToList()}]};
            track.RecalculateDistances();var project=new HikingProject{Name="拆分编号示例"};project.AddTrack(track,false);project.MainGuideTrackId=null;
            var raw=Path.Combine(folder,"原始轨迹.gpx");await new GpxTrackExporter().ExportAsync(new TrackExportRequest{Tracks=[track],DocumentName="1"},raw);
            var rawHash=SHA256.HashData(await File.ReadAllBytesAsync(raw));var editor=new TrackEditService();
            editor.PrepareSplitSegment(project,track.Id,track.Segments[0].Id,3,.5).Apply();
            checks["第一次拆分使用原名加全角括号序号"]=track.GetSplitSections().Select(s=>s.Name).SequenceEqual(new[]{"1（1）","1（2）"});
            editor.PrepareSplitSegment(project,track.Id,track.Segments[1].Id,0,.5).Apply();
            var middle=track.GetSplitSections()[1];editor.PrepareDeleteSplitSection(project,track.Id,middle.Id).Apply();
            checks["删除中间分段后保留编号缺口"]=track.GetSplitSections().Select(s=>s.Name).SequenceEqual(new[]{"1（1）","1（3）"});
            var path=Path.Combine(folder,"拆分编号.hikeproj");var file=new ProjectFileService();await file.SaveAsync(project,path,false);
            project=await file.LoadAsync(path);track=project.Tracks[0];
            checks["保存重开保留已用最大编号"]=track.Metadata[TrackSplitNaming.LastNumberKey]=="3";
            editor.PrepareSplitSegment(project,track.Id,track.Segments[1].Id,0,.5).Apply();
            checks["重开后新增分段继续到4且不补2"]=track.GetSplitSections().Select(s=>s.Name).SequenceEqual(new[]{"1（1）","1（3）","1（4）"});
            await file.SaveAsync(project,path,true);await main.OpenForAutomationAsync(path);await main.WaitForMapRefreshForAutomationAsync();
            foreach(var node in T10ManualNodes(main.NavigationNodes))node.IsExpanded=true;
            var names=T10ManualNodes(main.NavigationNodes).Where(n=>n.Model is SplitSection).Select(n=>((SplitSection)n.Model!).Name).ToArray();
            checks["项目面板显示新命名"]=names.SequenceEqual(new[]{"1（1）","1（3）","1（4）"});
            window.Width=1500;window.Height=950;window.BottomPanelRow.Height=new GridLength(260);await main.Map.FocusObjectAsync(main.MapContextProject!.Tracks[0]);await Task.Delay(800);await T10ManualLayoutAsync(window);
            await CaptureT10ClientAsync(window,Path.Combine(folder,"拆分分段编号.png"));
            var rawAfter=await File.ReadAllBytesAsync(raw);
            checks["原始GPX未覆盖"]=rawHash.SequenceEqual(SHA256.HashData(rawAfter));
            checks["版本仍为T192"]=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion=="V1.9.2";
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}

