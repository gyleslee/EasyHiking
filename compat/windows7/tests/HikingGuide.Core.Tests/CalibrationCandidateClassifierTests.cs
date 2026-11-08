using HikingGuide.Core.Calibration;
using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Tests;

public sealed class CalibrationCandidateClassifierTests
{
    private static CalibrationCandidateProfile Profile(CalibrationModel model = CalibrationModel.LocalSlope,
        CalibrationTailPolicy tail = CalibrationTailPolicy.ActualSlope, double threshold = 5,
        CalibrationDirectionPolicy direction = CalibrationDirectionPolicy.KeepAnchor) =>
        new(model, 100, 0, 100, 2, threshold, tail, direction);
    private static CalibrationDemPoint[] Series(params double?[] heights) => heights.Select((h, i) =>
        new CalibrationDemPoint(0, i * 100, new GeoCoordinate(0, i * 0.001), h)).ToArray();

    [Theory]
    [InlineData(CalibrationModel.LocalSlope)]
    [InlineData(CalibrationModel.ElevationEvent)]
    [InlineData(CalibrationModel.Hybrid)]
    public void ExactPositiveBoundaryIsUphillAndNegativeBoundaryDownhill(CalibrationModel model)
    {
        var a = CalibrationCandidateClassifier.Classify(Series(0, 2, 4), Profile(model, threshold: 0));
        var b = CalibrationCandidateClassifier.Classify(Series(4, 2, 0), Profile(model, threshold: 0));
        Assert.Equal(200, CalibrationCandidateClassifier.Measure(a, 0, 200).UphillMeters);
        Assert.Equal(200, CalibrationCandidateClassifier.Measure(b, 0, 200).DownhillMeters);
    }

    [Fact]
    public void NoDataRemainsUnclassifiedAndTotalsConserveDistance()
    {
        var intervals = CalibrationCandidateClassifier.Classify(Series(0, 10, null, 100), Profile());
        var distances = CalibrationCandidateClassifier.Measure(intervals, 0, 300);
        Assert.Equal(200, distances.UnclassifiedMeters);
        Assert.Equal(300, distances.TotalMeters);
    }

    [Theory]
    [InlineData(CalibrationTailPolicy.Flat, 100, 100)]
    [InlineData(CalibrationTailPolicy.ActualSlope, 100, 0)]
    [InlineData(CalibrationTailPolicy.MergePrevious, 200, 0)]
    public void TailPoliciesAreDistinct(CalibrationTailPolicy policy, double uphill, double flat)
    {
        var intervals = CalibrationCandidateClassifier.Classify(Series(0, 10, 7), Profile(CalibrationModel.ElevationEvent, policy));
        var d = CalibrationCandidateClassifier.Measure(intervals, 0, 200);
        Assert.Equal(uphill, d.UphillMeters);
        Assert.Equal(flat, d.FlatMeters);
        Assert.Equal(200, d.TotalMeters);
    }

    [Fact]
    public void MeasureClipsIntervalAtExactReferenceBoundaries()
    {
        var result = CalibrationCandidateClassifier.Measure([new(0, 0, 100, CalibrationTerrain.Uphill), new(0, 100, 200, CalibrationTerrain.Flat)], 25, 175);
        Assert.Equal(75, result.UphillMeters);
        Assert.Equal(75, result.FlatMeters);
    }

    [Theory]
    [InlineData(CalibrationDirectionPolicy.KeepAnchor)]
    [InlineData(CalibrationDirectionPolicy.ResetOnDirectionChange)]
    [InlineData(CalibrationDirectionPolicy.WaitHysteresis)]
    public void DirectionChangesDoNotDuplicateOrLoseDistance(CalibrationDirectionPolicy direction)
    {
        var intervals = CalibrationCandidateClassifier.Classify(Series(0, 3, 1, 10, 2), Profile(CalibrationModel.Hybrid, direction: direction));
        Assert.Equal(400, CalibrationCandidateClassifier.Measure(intervals, 0, 400).TotalMeters);
        for (var i = 1; i < intervals.Count; i++) Assert.Equal(intervals[i - 1].EndMeters, intervals[i].StartMeters);
    }

    [Fact]
    public void CachedEffectiveSlopesReclassifyIdenticallyWithoutResampling()
    {
        var series = Series(0, 4, 12, 9, 3);
        foreach (var model in Enum.GetValues<CalibrationModel>())
        foreach (var tail in Enum.GetValues<CalibrationTailPolicy>())
        foreach (var direction in Enum.GetValues<CalibrationDirectionPolicy>())
        {
            var profile = Profile(model, tail, direction: direction);
            var baseIntervals = CalibrationCandidateClassifier.Classify(series, profile with { SlopeThresholdPercent = 0 });
            foreach (var threshold in new[] { 0.5, 2d, 3d, 5d })
            {
                var classified = CalibrationCandidateClassifier.Classify(series, profile with { SlopeThresholdPercent = threshold });
                var cached = baseIntervals.Select(i => i with { Terrain = i.EffectiveSlopePercent >= threshold ? CalibrationTerrain.Uphill :
                    i.EffectiveSlopePercent <= -threshold ? CalibrationTerrain.Downhill : CalibrationTerrain.Flat }).ToArray();
                Assert.Equal(CalibrationCandidateClassifier.Measure(classified, 0, 400), CalibrationCandidateClassifier.Measure(cached, 0, 400));
            }
        }
    }

    [Fact]
    public void CoarserSamplingCannotHideNonFiniteDemSample()
    {
        var result = CalibrationCandidateClassifier.Classify(Series(0, double.NaN, 4, 7), Profile() with { SamplingMeters = 300 });
        Assert.Equal(200, CalibrationCandidateClassifier.Measure(result, 0, 300).UnclassifiedMeters);
    }
}
