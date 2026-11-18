using System.Globalization;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.TrackEditing;

/// <summary>将旧的逻辑拆分边界提升为独立轨迹；物理断点和原始轨迹点保持不变。</summary>
public sealed class IndependentTracks
{
    public const string FamilyKey = "hg:split-family";
    public const string PrefixKey = "hg:split-prefix";
    public const string LastKey = "hg:independent-split-last-number";
    public const string NumberKey = "hg:independent-split-number";
    private readonly HikingProject _project;
    private readonly Track _source;
    private readonly TrackSegment[] _before;
    private readonly string? _name;
    private readonly Dictionary<string,string?> _metadata;
    private readonly Dictionary<Track,Dictionary<string,string?>> _familyMetadata = [];
    private readonly (Waypoint Point,Guid? Owner,TrackPosition? Position)[] _waypoints = [];
    private readonly List<Track> _results = [];
    private readonly List<(Waypoint Point,Guid Owner,TrackPosition? Position)> _targets=[];
    private readonly Dictionary<string,bool?> _previousVisibility=[];
    private readonly Dictionary<Guid,bool> _visibility=[];
    private bool _applied;
    public IReadOnlyList<Track> Results => _results;

    public IndependentTracks(HikingProject project, Track source)
    {
        _project=project;_source=source;_before=source.Segments.ToArray();_name=source.Name;
        _metadata=new(source.Metadata);
        var sections=source.GetSplitSections();
        if(sections.Count<2) return;
        // 主攻略轨迹的历史分段保留主线，不隐式转换攻略节点或破坏人工攻略关系。
        if(project.MainGuideTrackId==source.Id)
            throw new InvalidOperationException("主攻略轨迹包含历史拆分边界，请先转为普通轨迹，再转换为独立轨迹。");
        var family=source.Metadata.GetValueOrDefault(FamilyKey)??source.Id.ToString("D");
        var prefix=source.Metadata.GetValueOrDefault(PrefixKey)??source.Name??"未命名轨迹";
        var relatives=project.Tracks.Where(t=>t==source||t.Metadata.GetValueOrDefault(FamilyKey)==family).ToArray();
        _familyMetadata=relatives.ToDictionary(t=>t,t=>new Dictionary<string,string?>(t.Metadata));
        long last=source.Metadata.ContainsKey(FamilyKey)?relatives.Select(t=>long.TryParse(t.Metadata.GetValueOrDefault(LastKey),out var n)?n:0).DefaultIfEmpty().Max():0;
        // 旧拆分已经分配过编号，其高水位也保留。新独立轨迹使用唯一的家族序号。
        long own=long.TryParse(source.Metadata.GetValueOrDefault(NumberKey),out var current)?current:0;
        long LegacyNumber(string name) => name.StartsWith(prefix+"（",StringComparison.Ordinal)&&name.EndsWith("）",StringComparison.Ordinal)&&
            long.TryParse(name.Substring(prefix.Length+1,name.Length-prefix.Length-2),out var n)&&n>0?n:0;
        if(!source.Metadata.ContainsKey(FamilyKey))
            last=Math.Max(sections.Select(x=>LegacyNumber(x.Name)).DefaultIfEmpty().Max(),
                long.TryParse(source.Metadata.GetValueOrDefault(TrackSplitNaming.LastNumberKey),out var old)?old:0);
        var colors=project.Tracks.ToList();
        for(int i=0;i<sections.Count;i++)
        {
            var legacy=source.Metadata.ContainsKey(FamilyKey)?0:LegacyNumber(sections[i].Name);
            var number=i==0&&own>0?own:legacy>0?legacy:checked(++last);
            var track=new Track { Id=i==0?source.Id:Guid.NewGuid(), Name=$"{prefix}（{number}）", Description=source.Description,
                SourceFile=source.SourceFile,SourceFormat=source.SourceFormat,
                DisplayColor=i==0?source.DisplayColor:TrackDisplayColor.NextUnique(colors),
                Segments=sections[i].GeometryParts.ToList(),Metadata=new(source.Metadata) };
            track.Metadata[FamilyKey]=family;track.Metadata[PrefixKey]=prefix;
            track.Metadata[NumberKey]=number.ToString(CultureInfo.InvariantCulture);
            _results.Add(track);colors.Add(track);
            var key=MapLayerVisibility.TrackKey(track.Id);
            _previousVisibility[key]=project.Settings.Map.LayerVisibility.TryGetValue(key,out var visible)?visible:null;
            _visibility[track.Id]=MapLayerVisibility.IsTrackVisible(project.Settings.Map,source.Id)&&
                MapLayerVisibility.IsSplitSectionVisible(project.Settings.Map,sections[i].Id);
        }
        foreach(var t in _results)t.Metadata[LastKey]=last.ToString(CultureInfo.InvariantCulture);
        _waypoints=project.Waypoints.Where(p=>p.AssociatedTrackId==source.Id)
            .Select(p=>(p,p.AssociatedTrackId,p.PreferredTrackPosition)).ToArray();
        var snap=new TrackSnapService();
        var fingerprints=_results.ToDictionary(t=>t.Id,t=>snap.GetGeometryFingerprint(t));
        foreach(var (point,_,preferred) in _waypoints)
        {
            var candidates=snap.SnapCandidates(source,preferred?.SnappedCoordinate??point.Coordinate,32)
                .Where(x=>x.Position is not null).Select(x=>x.Position!).ToArray();
            var minimum=candidates.Min(x=>x.DistanceToTrack);
            var at=candidates.Where(x=>x.DistanceToTrack<=minimum+.01)
                .MinBy(x=>preferred is null?x.DistanceFromTrackStart:Math.Abs(x.DistanceFromTrackStart-preferred.DistanceFromTrackStart))!;
            var owner=_results.First(t=>t.Segments.Any(s=>s.Id==at.TrackSegmentId));
            var offset=source.Segments.TakeWhile(s=>s.Id!=owner.Segments[0].Id).Sum(s=>s.GetLength());
            _targets.Add((point,owner.Id,at with {TrackId=owner.Id,
                SegmentIndex=owner.Segments.FindIndex(s=>s.Id==at.TrackSegmentId),
                DistanceFromTrackStart=at.DistanceFromTrackStart-offset,
                TrackGeometryFingerprint=fingerprints[owner.Id]}));
        }
    }

    public void Apply()
    {
        if(_applied||_results.Count==0)return;
        if(!_project.Tracks.Contains(_source)||!_source.Segments.SequenceEqual(_before))
            throw new InvalidOperationException("轨迹已经变化，请重新执行拆分。");
        _project.ExecuteAtomicUpdate(p=>
        {
            var index=p.Tracks.IndexOf(_source);
            // 保留第一条的稳定ID，避免既有引用因移除整轨而被清空。
            var first=_results[0];
            _source.Name=first.Name;_source.Metadata=new(first.Metadata);_source.Segments=first.Segments;
            _source.RecalculateDistances();
            foreach(var t in _familyMetadata.Keys)t.Metadata[LastKey]=first.Metadata[LastKey];
            for(int i=1;i<_results.Count;i++){_results[i].RecalculateDistances();p.Tracks.Insert(index+i,_results[i]);}
            foreach(var (id,visible) in _visibility)MapLayerVisibility.SetTrackVisible(p.Settings.Map,id,visible);
            foreach(var (point,owner,position) in _targets)
            {
                point.AssociatedTrackId=owner;
                point.PreferredTrackPosition=position;
            }
            p.NotifyTrackChanged();_applied=true;
        });
    }

    public void Undo()
    {
        if(!_applied)return;
        _project.ExecuteAtomicUpdate(p=>
        {
            foreach(var (point,owner,position) in _waypoints){point.AssociatedTrackId=owner;point.PreferredTrackPosition=position;}
            foreach(var t in _results.Skip(1))p.Tracks.Remove(t);
            foreach(var (key,value) in _previousVisibility)
                if(value.HasValue)p.Settings.Map.LayerVisibility[key]=value.Value;else p.Settings.Map.LayerVisibility.Remove(key);
            _source.Name=_name;_source.Segments=_before.ToList();_source.Metadata=new(_metadata);
            foreach(var (t,data) in _familyMetadata)t.Metadata=new(data);
            _source.RecalculateDistances();p.NotifyTrackChanged();_applied=false;
        });
    }
}
