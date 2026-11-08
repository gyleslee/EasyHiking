using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Tests;

public sealed class P10GuideWorkbenchTests
{
    private static readonly Guid TrackId = Guid.NewGuid();
    private static GuideSegmentStatistics Stats() => new(new(TrackId, Guid.NewGuid(), Guid.NewGuid()), 7000, 500, 100, 2000, 3000, 1000, 1500, 500, 0, true);
    private static GuideCalculationSettings Speeds() => new() { FlatDownSpeedKmh = 4, GentleUpSpeedKmh = 3, SteepUpSpeedKmh = 2 };
    private static (Guid[] Nodes, GuideManualDataStore Manual) Chain(params double?[] hours)
    {
        var ids = Enumerable.Range(0, hours.Length + 1).Select(_ => Guid.NewGuid()).ToArray(); var manual = new GuideManualDataStore();
        for (var i = 0; i < hours.Length; i++) { var key = new GuideSegmentKey(TrackId, ids[i], ids[i + 1]); manual.Segments[key.StorageKey] = new() { SegmentKey = key, PlannedDurationHours = hours[i], RoadCondition = "原文\n道路" }; }
        return (ids, manual);
    }

    [Fact] public void NewProjectHasBlankSpeedsTwoIndependentPlansAndFourConditions()
    {
        var a = new HikingProject(); var b = new HikingProject();
        Assert.Null(a.GuideData.Settings.FlatDownSpeedKmh); Assert.Null(a.GuideData.Settings.GentleUpSpeedKmh); Assert.Null(a.GuideData.Settings.SteepUpSpeedKmh);
        Assert.Equal(2, a.GuideData.Plans.Count); Assert.NotEqual(a.GuideData.Plans[0].PlanId, a.GuideData.Plans[1].PlanId);
        Assert.Equal(new[] { "营地", "下撤", "补给", "补生水" }, a.GuideData.Settings.LocationConditions.Select(d => d.Name));
        a.GuideData.Plans[0].DailyStartNodeIds.Add(Guid.NewGuid()); Assert.Empty(a.GuideData.Plans[1].DailyStartNodeIds); Assert.Empty(b.GuideData.Plans[0].DailyStartNodeIds);
        Assert.All(a.GuideData.Plans, p => Assert.Empty(p.DepartureTimes)); Assert.False(a.IsDirty);
    }
    [Fact] public void AverageUsesThreeSpeedsAndProportionalRest() => Assert.Equal(1.8958333333333333, GuideTimingService.AverageHours(Stats(), Speeds())!.Value, 10);
    [Fact] public void NaismithUsesOnlyDistanceAndAscent() => Assert.Equal(2.2333333333333334, GuideTimingService.NaismithHours(Stats())!.Value, 10);
    [Fact] public void MissingSpeedDoesNotDefaultToExample() { Assert.Null(GuideTimingService.AverageHours(Stats(), new())); Assert.Null(GuideTimingService.AverageHours(Stats(), Speeds() with { SteepUpSpeedKmh = null })); }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidSpeedsRejected(double value) => Assert.Throws<ArgumentException>(() => GuideTimingService.AverageHours(Stats(), Speeds() with { FlatDownSpeedKmh = value }));
    [Fact] public void IncompleteElevationCannotBecomeZeroTiming()
    { var stats = Stats() with { IsComplete = false, AscentMeters = null }; Assert.Null(GuideTimingService.AverageHours(stats, Speeds())); Assert.Null(GuideTimingService.NaismithHours(stats)); }
    [Theory] [InlineData(2.1, 0, 0, 10)] [InlineData(2.100001, 10, 0, 0)] [InlineData(15, 10, 0, 0)] [InlineData(25, 10, 0, 0)] [InlineData(34.99999, 10, 0, 0)] [InlineData(35, 0, 10, 0)] [InlineData(35.00001, 0, 10, 0)]
    public void ExactSlopeBoundariesUseP9Intervals(double slope, double gentle, double steep, double flat)
    {
        var classification = new TerrainDistanceClassificationResult(new(), "geometry", "source", [], 0, 0, 0, 0, DateTimeOffset.UtcNow) { Intervals = [new(0, 0, 10, slope)] };
        var result = GuideTimingService.SplitTerrain(classification, 0, 10); Assert.Equal(gentle, result.GentleUphillMeters); Assert.Equal(steep, result.SteepUphillMeters); Assert.Equal(flat, result.FlatMeters); Assert.Equal(result.UphillMeters, result.GentleUphillMeters + result.SteepUphillMeters);
    }
    [Fact] public void MissingTerrainIsNotFlatAndPartialIntervalIsClipped()
    {
        var result = new TerrainDistanceClassificationResult(new(), "g", "s", [], 0, 0, 0, 0, DateTimeOffset.UtcNow) { Intervals = [new(0, 0, 10, 10), new(0, 10, 20, null)] };
        var clipped = GuideTimingService.SplitTerrain(result, 5, 25); Assert.Equal(5, clipped.GentleUphillMeters); Assert.Equal(15, clipped.UnclassifiedMeters); Assert.Equal(0, clipped.FlatMeters);
    }
    [Fact] public void SharedBoundarySegmentsBelongExactlyToOneDay()
    {
        var (ids, manual) = Chain(1, 2, 3, 4, 5); var plan = new GuideTravelPlan { DailyStartNodeIds = [ids[3], ids[0]], DepartureTimes = new() { [ids[0]] = new(6, 30), [ids[3]] = new(7, 0) } };
        var result = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual); Assert.Equal(2, result.Days.Count);
        Assert.Equal(3, result.Days[0].Segments.Count); Assert.Equal(2, result.Days[1].Segments.Count); Assert.Equal(5, result.Days.SelectMany(d => d.Segments).Distinct().Count());
        Assert.Equal(ids[3], result.Days[0].EndNodeId); Assert.Equal(ids[3], result.Days[1].StartNodeId); Assert.Equal(6, result.Days[0].PlannedDurationHours); Assert.Equal(9, result.Days[1].PlannedDurationHours);
    }
    [Fact] public void EachDayRestartsFromItsOwnDepartureAndBoundaryKeepsArrival()
    {
        var (ids, manual) = Chain(1.5, 2, 1.25, 2, 1); var plan = new GuideTravelPlan { DailyStartNodeIds = [ids[0], ids[3]], DepartureTimes = new() { [ids[0]] = new(6, 30), [ids[3]] = new(7, 0) } };
        var result = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual);
        Assert.Null(result.Arrivals[ids[0]]); Assert.Equal(new TimeOnly(8, 0), result.Arrivals[ids[1]]!.Time); Assert.Equal(new TimeOnly(10, 0), result.Arrivals[ids[2]]!.Time);
        Assert.Equal(new TimeOnly(11, 15), result.Arrivals[ids[3]]!.Time); Assert.Equal(new TimeOnly(9, 0), result.Arrivals[ids[4]]!.Time);
    }
    [Fact] public void P101DeparturesRemainSeparateFromPreviousDayArrivalsAndRecalculate()
    {
        var (ids, manual) = Chain(1, 2, 3); var plan = new GuideTravelPlan { DailyStartNodeIds = [ids[1]], DepartureTimes = new() { [ids[0]] = new(6, 0), [ids[1]] = new(7, 0) } };
        var result = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual);
        Assert.Equal(new TimeOnly(6, 0), result.Departures[ids[0]].Time); Assert.Equal(new TimeOnly(7, 0), result.Arrivals[ids[1]]!.Time);
        Assert.Equal(2, result.Departures[ids[1]].PlanDayNumber); Assert.Equal(new TimeOnly(9, 0), result.Arrivals[ids[2]]!.Time);
        plan.DepartureTimes[ids[1]] = new(8, 15); result = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual);
        Assert.Equal(new TimeOnly(10, 15), result.Arrivals[ids[2]]!.Time); Assert.Equal(new TimeOnly(7, 0), result.Arrivals[ids[1]]!.Time);
    }
    [Fact] public void P101SummaryUsesOneDecimalAndKeepsMissingAsMissing()
    {
        var day = new GuideDailyItinerary(1, Guid.NewGuid(), Guid.NewGuid(), [], null, 25250, 100.5, 80.5, 1.25);
        Assert.Equal("D1距离25.3KM，爬升101米，下降81米，计划总用时1.3h", GuideDisplayFormats.Summary(day));
        Assert.Contains("待计算", GuideDisplayFormats.Summary(day with { DistanceMeters = null }));
    }
    [Fact] public void MidnightKeepsOffset()
    { var (ids, manual) = Chain(2); var plan = new GuideTravelPlan { DepartureTimes = new() { [ids[0]] = new(23, 30) } }; var arrival = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual).Arrivals[ids[1]]!; Assert.Equal(new TimeOnly(1, 30), arrival.Time); Assert.Equal(1, arrival.DayOffset); Assert.Equal(1, arrival.PlanDayNumber); }
    [Fact] public void MissingDurationStopsOnlyCurrentDay()
    {
        var (ids, manual) = Chain(1, null, 3, 2, 1); var plan = new GuideTravelPlan { DailyStartNodeIds = [ids[3]], DepartureTimes = new() { [ids[0]] = new(6, 0), [ids[3]] = new(7, 0) } }; var result = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual);
        Assert.NotNull(result.Arrivals[ids[1]]); Assert.Null(result.Arrivals[ids[2]]); Assert.Null(result.Arrivals[ids[3]]); Assert.Equal(new TimeOnly(9, 0), result.Arrivals[ids[4]]!.Time); Assert.Null(result.Days[0].PlannedDurationHours); Assert.Equal(3, result.Days[1].PlannedDurationHours);
    }
    [Fact] public void MultiplePlansAreIndependentAndDoNotMutateManualDurations()
    {
        var (ids, manual) = Chain(1, 2, 3, 4, 5); var a = new GuideTravelPlan { DailyStartNodeIds = [ids[3]], DepartureTimes = new() { [ids[0]] = new(6, 0), [ids[3]] = new(7, 0) } }; var b = new GuideTravelPlan { DailyStartNodeIds = [ids[2], ids[4]], DepartureTimes = new() { [ids[0]] = new(8, 0), [ids[2]] = new(9, 0), [ids[4]] = new(10, 0) } };
        var bBefore = GuideTimingService.CalculatePlan(TrackId, ids, b, manual); GuideTimingService.CalculatePlan(TrackId, ids, a, manual); a.DepartureTimes[ids[0]] = new(11, 0);
        var bAfter = GuideTimingService.CalculatePlan(TrackId, ids, b, manual); Assert.Equal(bBefore.Arrivals, bAfter.Arrivals); Assert.Equal(3, bAfter.Days.Count); Assert.Equal(5, manual.Segments.Count);
    }
    [Fact] public void ZeroDurationIsValidButMissingDepartureIsNotZero()
    { var (ids, manual) = Chain(0, 1); var plan = new GuideTravelPlan(); var result = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual); Assert.All(result.Arrivals.Values, a => Assert.Null(a)); Assert.Equal(1, result.Days[0].PlannedDurationHours); }
    [Theory] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidManualHoursRejected(double value) => Assert.Throws<ArgumentException>(() => new GuideSegmentManualData { PlannedDurationHours = value }.Validate());
    [Fact] public void InsertNodeArchivesOldSegmentWithoutCopyingItToNewSegments()
    {
        var (ids, manual) = Chain(2.5); var data = new GuideWorkbenchData { Manual = manual }; var mid = Guid.NewGuid(); var result = GuideDataReconciler.Reconcile(data, TrackId, [ids[0], mid, ids[1]]);
        Assert.Equal(1, result.ArchivedSegmentCount); Assert.Empty(data.Manual.Segments); Assert.Equal("原文\n道路", Assert.Single(data.Manual.ArchivedSegments).Data.RoadCondition); Assert.Equal(2.5, data.Manual.ArchivedSegments[0].Data.PlannedDurationHours);
        Assert.Equal(0, GuideDataReconciler.Reconcile(data, TrackId, [ids[0], mid, ids[1]]).ArchivedSegmentCount);
    }
    [Fact] public void ReconcileKeepsNodeManualDataAndFlagsDeletedDailyStart()
    {
        var (ids, manual) = Chain(1, 2); var definition = Guid.NewGuid(); manual.Nodes[ids[1]] = new() { GuideNodeId = ids[1], ActualArrival = new(17, 45), LocationConditions = new() { [definition] = LocationConditionValue.Yes } };
        var data = new GuideWorkbenchData { Manual = manual }; data.Plans[0].DailyStartNodeIds.Add(ids[1]); GuideDataReconciler.Reconcile(data, TrackId, [ids[0], ids[2]]);
        Assert.True(data.Plans[0].NeedsReview); Assert.Contains(ids[1], data.Plans[0].DailyStartNodeIds); Assert.Equal(new TimeOnly(17, 45), data.Manual.Nodes[ids[1]].ActualArrival); Assert.Equal(LocationConditionValue.Yes, data.Manual.Nodes[ids[1]].LocationConditions[definition]);
    }
    [Fact] public void InvalidPlanDoesNotInventReplacementArrival()
    { var (ids, manual) = Chain(1, 2); var plan = new GuideTravelPlan { DailyStartNodeIds = [Guid.NewGuid()], DepartureTimes = new() { [ids[0]] = new(6, 0) } }; var result = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual); Assert.True(result.NeedsReview); Assert.All(result.Arrivals.Values, Assert.Null); }
    [Fact] public void DeletedDailyStartBlocksUntilExplicitConfirmationThenRestoresSavedTimes()
    {
        var (ids, manual) = Chain(.8, 2.3, 1.25); var deleted = Guid.NewGuid();
        var plan = new GuideTravelPlan { NeedsReview = true, DailyStartNodeIds = [ids[0], deleted, ids[2]],
            DepartureTimes = new() { [ids[0]] = new(6, 0), [ids[2]] = new(7, 0), [deleted] = new(8, 0) },
            DailyNotes = new() { [ids[0]] = "保留人工备注", [deleted] = "失效节点备注也保留" } };
        var beforeTimes = plan.DepartureTimes.ToArray(); var beforeNotes = plan.DailyNotes.ToArray(); var beforeManual = manual.Segments.ToArray();
        var blocked = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual); Assert.All(blocked.Arrivals.Values, Assert.Null);
        Assert.Equal(1, GuideDataReconciler.ConfirmExistingDailyStarts(plan, ids)); Assert.False(plan.NeedsReview); Assert.DoesNotContain(deleted, plan.DailyStartNodeIds);
        var result = GuideTimingService.CalculatePlan(TrackId, ids, plan, manual); Assert.Equal(new TimeOnly(6, 48), result.Arrivals[ids[1]]!.Time);
        Assert.Equal(new TimeOnly(9, 6), result.Arrivals[ids[2]]!.Time); Assert.Equal(new TimeOnly(8, 15), result.Arrivals[ids[3]]!.Time);
        Assert.Equal(beforeTimes, plan.DepartureTimes.ToArray()); Assert.Equal(beforeNotes, plan.DailyNotes.ToArray()); Assert.Equal(beforeManual, manual.Segments.ToArray());
        Assert.Equal(0, GuideDataReconciler.ConfirmExistingDailyStarts(plan, ids));
    }
    [Fact] public void ConfirmDailyStartsRemovesLastNodeReferenceButDoesNotInventDeparture()
    {
        var (ids, _) = Chain(1, 2); var plan = new GuideTravelPlan { NeedsReview = true, DailyStartNodeIds = [ids[^1]] };
        Assert.Equal(1, GuideDataReconciler.ConfirmExistingDailyStarts(plan, ids)); Assert.Equal(new[] { ids[0] }, plan.DailyStartNodeIds); Assert.Empty(plan.DepartureTimes);
        Assert.Throws<ArgumentException>(() => GuideDataReconciler.ConfirmExistingDailyStarts(plan, [ids[0]]));
    }
    [Fact] public void SoftRemoveAndRestoreConditionKeepsStableIdentityAndNodeValues()
    {
        var data = new GuideWorkbenchData(); var camp = data.Settings.LocationConditions[0]; var id = Guid.NewGuid(); data.Manual.Nodes[id] = new() { GuideNodeId = id, LocationConditions = new() { [camp.DefinitionId] = LocationConditionValue.Yes } };
        GuideDataReconciler.RemoveCondition(data.Settings, camp.DefinitionId); Assert.False(data.Settings.LocationConditions[0].IsActive); var restored = GuideDataReconciler.AddCondition(data.Settings, "营地", "营地"); Assert.Equal(camp.DefinitionId, restored.DefinitionId); Assert.Equal(LocationConditionValue.Yes, data.Manual.Nodes[id].LocationConditions[restored.DefinitionId]);
        Assert.Throws<ArgumentException>(() => GuideDataReconciler.AddCondition(data.Settings, "营地")); Assert.Throws<ArgumentException>(() => GuideDataReconciler.AddCondition(data.Settings, "  "));
    }
    [Fact] public void AllRequiredPresetsExist() { Assert.Equal(19, GuideDataReconciler.LocationPresets.Count); Assert.Contains("Wi-Fi", GuideDataReconciler.LocationPresets); Assert.Contains("烧烤炉", GuideDataReconciler.LocationPresets); }
    [Fact] public void CannotSaveNoPlansOrDuplicatePlanIdentity() { var data = new GuideWorkbenchData(); data.Plans.Clear(); Assert.Throws<ArgumentException>(data.Validate); data.Plans.Add(new()); data.Plans.Add(new() { PlanId = data.Plans[0].PlanId }); Assert.Throws<ArgumentException>(data.Validate); }
    [Fact] public void ProjectUpdatesMarkOnlyOwningProjectDirty() { var a = new HikingProject(); var b = new HikingProject(); a.UpdateGuideData(d => d.Settings = Speeds()); Assert.True(a.IsDirty); Assert.False(b.IsDirty); Assert.Null(b.GuideData.Settings.FlatDownSpeedKmh); }
}
