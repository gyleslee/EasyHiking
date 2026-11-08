using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;
public sealed class TrackConnectionOrderTests
{
    [Theory]
    [InlineData("", "2")]
    [InlineData("1", "1")]
    [InlineData("1", "3")]
    [InlineData("0", "1")]
    [InlineData("-1", "2")]
    [InlineData("1.5", "2")]
    [InlineData("甲", "2")]
    [InlineData("2147483648", "2")]
    [InlineData("01", "2")]
    [InlineData("+1", "2")]
    public void InvalidSequenceCannotCreateConnection(string a,string b)=>
        Assert.Throws<ArgumentException>(()=>TrackConnectionOrder.OrderedIndices([a,b]));

    [Fact]public void ManualOrderAndDirectionDriveTheConnectedGeometry()
    {
        Track Make(string name,double x)=>new(){Name=name,Segments=[new(){Points=[new(){Latitude=23,Longitude=x},new(){Latitude=23,Longitude=x+.001}]}]};
        var project=new HikingProject();var a=Make("甲",113);var b=Make("乙",114);var c=Make("丙",115);
        foreach(var t in new[]{a,b,c}){t.RecalculateDistances();project.AddTrack(t,false);}project.MainGuideTrackId=null;
        var sources=new[]{new TrackMergeSource(a.Id,false),new TrackMergeSource(b.Id,false),new TrackMergeSource(c.Id,true)};
        var ordered=TrackConnectionOrder.OrderedIndices(["3","1","2"]).Select(i=>sources[i]).ToArray();
        var plan=new TrackMergeService().Prepare(project,ordered,"按编号连接",true);
        var points=plan.NewTrack.Segments.SelectMany(s=>s.Points).Select(p=>p.Longitude).ToArray();
        Assert.Equal(114,points[0]);Assert.Equal(113.001,points[^1],6);
        Assert.True(Array.IndexOf(points,115.001)<Array.IndexOf(points,115));
        Assert.Equal(3,project.Tracks.Count);
    }

    [Fact]public void SpacesAroundValidNumbersAreAccepted()=>Assert.Equal(new[]{1,2,0},TrackConnectionOrder.OrderedIndices([" 3 ","1","2"]));
}
