using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;
public sealed class TrackSplitNamingTests
{
    private readonly TrackEditService _editor=new();
    private static (HikingProject Project,Track Track) Fixture(string? name="1")
    {
        var track=new Track{Name=name,Segments=[new(){Name="导入原名称",Points=Enumerable.Range(0,9).Select(i=>new TrackPoint{Latitude=23,Longitude=113+i*.001,SourceElevation=100+i}).ToList()}]};
        track.RecalculateDistances();var project=new HikingProject();project.AddTrack(track,false);project.MainGuideTrackId=null;
        return(project,track);
    }
    private TrackEditPlan Split(HikingProject p,Track t,int section=0)
    {
        var segment=t.GetSplitSections()[section].GeometryParts[0];
        return _editor.PrepareSplitSegment(p,t.Id,segment.Id,0,.5);
    }
    [Fact]public void FirstSplitUsesTrackNameAndPrepareDoesNotMutateSource()
    {
        var(p,t)=Fixture();var points=t.Segments[0].Points.Select(x=>x.Id).ToArray();var beforeLength=t.GetTotalLength();
        var plan=Split(p,t);Assert.Single(t.Segments);Assert.Empty(t.Metadata);Assert.Null(t.Segments[0].SplitSectionName);
        plan.Apply();Assert.Equal(new[]{"1（1）","1（2）"},t.GetSplitSections().Select(s=>s.Name));
        Assert.Equal("2",t.Metadata[TrackSplitNaming.LastNumberKey]);Assert.Equal(beforeLength,t.GetTotalLength(),5);
        Assert.All(points,id=>Assert.Contains(t.Segments.SelectMany(s=>s.Points),point=>point.Id==id));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DeletedMiddleOrHighestNumberIsNeverReused(bool highest)
    {
        var(p,t)=Fixture();Split(p,t).Apply();Split(p,t,1).Apply();
        var remove=t.GetSplitSections()[highest?2:1].Id;_editor.PrepareDeleteSplitSection(p,t.Id,remove).Apply();
        Split(p,t,0).Apply();var names=t.GetSplitSections().Select(s=>s.Name).ToArray();
        Assert.Contains("1（4）",names);Assert.DoesNotContain(highest?"1（3）":"1（2）",names);
        Assert.Equal("4",t.Metadata[TrackSplitNaming.LastNumberKey]);
    }
    [Fact]public void SplitUndoRedoRestoresNamesAndCounterWithoutChangingOtherMetadata()
    {
        var(p,t)=Fixture();t.Metadata["人工说明"]="保留";var plan=Split(p,t);plan.Apply();plan.Undo();
        Assert.Single(t.Segments);Assert.Null(t.Segments[0].SplitSectionName);Assert.False(t.Metadata.ContainsKey(TrackSplitNaming.LastNumberKey));
        plan.Apply();Assert.Equal(new[]{"1（1）","1（2）"},t.GetSplitSections().Select(s=>s.Name));Assert.Equal("保留",t.Metadata["人工说明"]);
    }
    [Fact]public void RenamingParentKeepsSequenceAndOnlyRenamesTheSplitSection()
    {
        var(p,t)=Fixture();Split(p,t).Apply();t.Name="山脊线路";Split(p,t).Apply();
        Assert.Equal(new[]{"山脊线路（1）","山脊线路（3）","1（2）"},t.GetSplitSections().Select(s=>s.Name));
    }
    [Fact]public void MultiPartLogicalSectionGetsOneNamePerSide()
    {
        var(p,t)=Fixture();var section=Guid.NewGuid();t.Segments[0].SplitSectionId=section;
        t.Segments.Add(new(){SplitSectionId=section,SplitSectionName="旧逻辑名称",Points=[new(){Latitude=24,Longitude=113},new(){Latitude=24,Longitude=113.01}]});
        t.RecalculateDistances();_editor.PrepareSplitSegment(p,t.Id,t.Segments[1].Id,0,.5).Apply();
        Assert.Equal(new[]{"1（1）","1（2）"},t.GetSplitSections().Select(s=>s.Name));
        Assert.All(t.GetSplitSections()[0].GeometryParts,part=>Assert.Equal("1（1）",part.SplitSectionName));
    }
    [Fact]public void OldNamedSectionsBootstrapCounterAndBadCounterCannotChangeProject()
    {
        var(p,t)=Fixture();t.Segments[0].SplitSectionName="1（7）";Split(p,t).Apply();
        Assert.Equal(new[]{"1（7）","1（8）"},t.GetSplitSections().Select(s=>s.Name));
        t.Metadata[TrackSplitNaming.LastNumberKey]="损坏";var count=t.Segments.Count;Assert.Throws<InvalidOperationException>(()=>Split(p,t));Assert.Equal(count,t.Segments.Count);
    }
    [Fact]public void UnnamedTrackUsesChineseFallback()
    {
        var(p,t)=Fixture(null);Split(p,t).Apply();Assert.Equal(new[]{"未命名轨迹（1）","未命名轨迹（2）"},t.GetSplitSections().Select(s=>s.Name));
    }
    [Fact]public void StaleNumberingPlanCannotOverwriteAChangedSequence()
    {
        var(p,t)=Fixture();var plan=Split(p,t);t.Metadata[TrackSplitNaming.LastNumberKey]="9";
        Assert.Throws<InvalidOperationException>(()=>plan.Apply());Assert.Single(t.Segments);Assert.Null(t.Segments[0].SplitSectionName);
        Assert.Equal("9",t.Metadata[TrackSplitNaming.LastNumberKey]);
    }
}
