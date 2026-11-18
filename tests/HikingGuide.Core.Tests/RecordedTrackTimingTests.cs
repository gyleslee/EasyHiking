using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
namespace HikingGuide.Core.Tests;
public class RecordedTrackTimingTests
{
    private static Track TrackOf(params double?[] seconds)=>new(){Segments=[new(){Points=seconds.Select((s,i)=>new TrackPoint{Latitude=20,Longitude=110+i*.001,Timestamp=s.HasValue?DateTimeOffset.UnixEpoch.AddSeconds(s.Value):null,DistanceFromStart=i*100}).ToList()}]};
    [Fact] public void CapsGapsAndPreservesFullDays(){var r=RecordedTrackTiming.Calculate(TrackOf(0,1,21,121,90000));Assert.Equal(61,r.Moving!.Value.TotalSeconds);Assert.Equal(90000,r.Elapsed!.Value.TotalSeconds);Assert.Equal(89939,r.Rest!.Value.TotalSeconds);}
    [Fact] public void MissingTimesAreNotInvented(){var t=TrackOf(0,null,100);var r=RecordedTrackTiming.Calculate(t);Assert.Equal(0,r.Moving!.Value.TotalSeconds);Assert.Equal(2,r.TimedPoints);Assert.Null(RecordedTrackTiming.TimeAtDistance(t,50));Assert.Null(RecordedTrackTiming.Calculate(TrackOf(null,null)).Elapsed);}
    [Fact] public void ReverseRetainsDurations(){var t=TrackOf(0,1,21,121);var r=RecordedTrackTiming.Calculate(t);t.Segments[0].Points.Reverse();Assert.Equal(r.Moving,RecordedTrackTiming.Calculate(t).Moving);}
    [Fact] public void DuplicateIntervalsNeverDoubleCount(){var t=TrackOf(0,10,100);t.Segments.Add(t.Segments[0]);var r=RecordedTrackTiming.Calculate(t);Assert.Equal(30,r.Moving!.Value.TotalSeconds);}
    [Fact] public void TimeInterpolationStaysInsideSegment(){var t=TrackOf(0,60,120);Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30),RecordedTrackTiming.TimeAtDistance(t,50));Assert.Null(RecordedTrackTiming.TimeAtDistance(t,250));}
    [Fact] public void ParameterCanBeCalibrated(){Assert.Equal(6,RecordedTrackTiming.Calculate(TrackOf(0,100,200),new(3)).Moving!.Value.TotalSeconds);Assert.Throws<ArgumentException>(()=>RecordedTrackTiming.Calculate(TrackOf(0,1),new(0)));}
    [Fact] public void DisorderedTimesDoNotCreateNegativeRest(){var r=RecordedTrackTiming.Calculate(TrackOf(0,10,5,20));Assert.Equal(1,r.InvalidIntervals);Assert.True(r.Rest>=TimeSpan.Zero);}
}
