using System.Globalization;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Core.Elevation;
using HikingGuide.Infrastructure.Elevation;

namespace HikingGuide.App.ViewModels;
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _trackPropertiesCancellation;
    private static string FormatRecordedTime(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy/M/d H:mm", CultureInfo.InvariantCulture) ?? "无记录";
    private static string Duration(TimeSpan? value) => value is { } t ? $"{(long)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : "无记录";
    private void ShowTrackProperties(Track track)
    {
        SelectedProperties.Add(new("名称",track.Name??"未命名轨迹"));
        SelectedProperties.Add(new("主攻略轨迹",track.Id==CurrentProject?.MainGuideTrackId?"是":"否"));
        var color=TrackDisplayColor.IsValid(track.DisplayColor)?track.DisplayColor!:"#2877C7";
        SelectedProperties.Add(new("显示颜色",color,color,track));
        SelectedProperties.Add(new("总距离","正在计算…"));
        foreach(var label in new[]{"运动用时","运动均速","全程用时","全程均速","休息时间","累计爬升\n(GPS)","累计爬升\n(DEM)","累计下降\n(GPS)","累计下降\n(DEM)","最高海拔","最低海拔"})SelectedProperties.Add(new(label,"正在计算…"));
        SelectedProperties.Add(new("标注点数",CurrentProject is { } p ? (p.Waypoints.Count(w=>w.AssociatedTrackId==track.Id)+p.GuideNodes.Count(n=>n.TrackId==track.Id)).ToString(CultureInfo.CurrentCulture):"0"));
        SelectedProperties.Add(new("轨迹段数",track.Segments.Count.ToString(CultureInfo.CurrentCulture)));
        SelectedProperties.Add(new("轨迹点数",track.GetPointCount().ToString(CultureInfo.CurrentCulture)));
        SelectedProperties.Add(new("说明",track.Description??"—"));
        SelectedProperties.Add(new("原始来源",track.SourceFile??"无"));
        SelectedProperties.Add(new("轨迹ID",track.Id.ToString()));
        _trackPropertiesCancellation=new();
        _=LoadTrackPropertiesAsync(track,_trackPropertiesCancellation.Token);
    }
    internal void RefreshSelectedTrackProperties()
    {
        if(_selectedModel is not Track track)return;
        _trackPropertiesCancellation?.Cancel();SelectedProperties.Clear();ShowTrackProperties(track);
    }
    private async Task LoadTrackPropertiesAsync(Track track,CancellationToken token)
    {
        var project = CurrentProject;
        if (project is null || System.Windows.Application.Current is not App host) return;
        // Record-time calculation uses an independent snapshot.
        var copy=new Track{Id=track.Id,Segments=track.Segments.Select(s=>new TrackSegment{Id=s.Id,Points=s.Points.Select(p=>new TrackPoint{Id=p.Id,Latitude=p.Latitude,Longitude=p.Longitude,Timestamp=p.Timestamp,SourceElevation=p.SourceElevation}).ToList()}).ToList()};
        var app=System.Windows.Application.Current as App;
        var demId=app?.GlobalSettingsService.Current.Terrain3D?.TerrainDemDatabaseId;
        void Set(string name,string value,string? hint=null){if(token.IsCancellationRequested||!ReferenceEquals(_selectedModel,track))return;var i=SelectedProperties.ToList().FindIndex(r=>r.Name==name);if(i>=0)SelectedProperties[i]=new(name,value,Hint:hint);}
        string Meters(double? value)=>value is double v?$"{v:F0} m":"无数据";
        try
        {
            var basic=await Task.Run(()=>{token.ThrowIfCancellationRequested();return (Distance:copy.GetTotalLength(),Timing:RecordedTrackTiming.Calculate(copy));},token);
            Set("总距离",$"{basic.Distance/1000:F2} km");Set("运动用时",Duration(basic.Timing.Moving));Set("全程用时",Duration(basic.Timing.Elapsed));Set("休息时间",Duration(basic.Timing.Rest));
            Set("运动均速",basic.Timing.MovingSpeed(basic.Distance) is double m?$"{m:F2} km/h":"无记录");Set("全程均速",basic.Timing.ElapsedSpeed(basic.Distance) is double e?$"{e:F2} km/h":"无记录");
            var timingHint=$"记录时间点 {basic.Timing.TimedPoints}/{basic.Timing.TotalPoints}；异常时间间隔 {basic.Timing.InvalidIntervals}。相邻间隔最多计20秒运动时间，其余计休息。时间缺失部分不补造。均速使用本轨迹总距离。";
            foreach(var name in new[]{"运动用时","运动均速","全程用时","全程均速","休息时间"}){var row=SelectedProperties.FirstOrDefault(r=>r.Name==name);if(row is not null)Set(name,row.Value,timingHint);}
            var target=ElevationAnalysisCoordinator.ForTrack(project,track);
            var profile=new LocusTerrainDistanceProfile();var analysisProfile=new ElevationAnalysisProfile(profile.SampleSpacingMeters,profile.SmoothingDistanceMeters,profile.SlopeWindowMeters);
            var coordinator=new ElevationAnalysisCoordinator(host.DemCatalog, (IElevationProvider?)host.DemProvider ?? GpsPropertiesProvider.Instance);
            var gps=await coordinator.AnalyzeGpsAsync(project,target,analysisProfile,cancellationToken:token);
            Set("累计爬升\n(GPS)",Meters(gps.Summary.AscentMeters)+(gps.Summary.Availability==ElevationAvailability.Partial?"（覆盖不完整）":""));Set("累计下降\n(GPS)",Meters(gps.Summary.DescentMeters)+(gps.Summary.Availability==ElevationAvailability.Partial?"（覆盖不完整）":""));
            var database=app?.DemCatalog.Databases.FirstOrDefault(d=>d.Id==demId);
            if(app?.DemProvider is null||database is null)
            {foreach(var n in new[]{"累计爬升\n(DEM)","累计下降\n(DEM)","最高海拔","最低海拔"})Set(n,"未选择地形DEM");return;}
            var dem=await coordinator.AnalyzeDemAsync(project,target,database.Id,analysisProfile,cancellationToken:token);
            var suffix=dem.Summary.Availability==ElevationAvailability.Partial?"（覆盖不完整）":"";
            Set("累计爬升\n(DEM)",Meters(dem.Summary.AscentMeters)+suffix,database.Name);Set("累计下降\n(DEM)",Meters(dem.Summary.DescentMeters)+suffix,database.Name);
            Set("最高海拔",Meters(dem.Summary.MaximumElevationMeters)+suffix,database.Name);Set("最低海拔",Meters(dem.Summary.MinimumElevationMeters)+suffix,database.Name);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){foreach(var row in SelectedProperties.ToArray().Where(r=>r.Value=="正在计算…"))Set(row.Name,"计算失败："+ex.Message);}
    }
    private sealed class GpsPropertiesProvider : IElevationProvider
    {
        public static readonly GpsPropertiesProvider Instance = new();
        public ValueTask<double?> SampleAsync(Guid databaseId, HikingGuide.Core.Geometry.GeoCoordinate coordinate, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<double?>(null);
    }

}

