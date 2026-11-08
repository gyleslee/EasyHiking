using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Guides;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;

public sealed class P10GuideExportAndCalculationTests : IDisposable
{
    private readonly string _folder = Path.Combine(@"F:\Codex\Temp\HikingGuide.P10.Tests", Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private string Template => Path.Combine(AppContext.BaseDirectory, "Templates", "攻略数据模板.xlsx");
    public P10GuideExportAndCalculationTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);
    public static HikingProject Fixture(int count = 4, bool gps = true)
    {
        var project = new HikingProject { Name = "P10中文测试" };
        var track = new Track { Name = "测试攻略" };
        track.Segments.Add(new() { Points = Enumerable.Range(0, count).Select(i => new TrackPoint { Latitude = 23 + i * 0.001, Longitude = 113, SourceElevation = gps ? 100 + i * 10 : null }).ToList() });
        track.RecalculateDistances(); project.AddTrack(track); project.MainGuideTrackId = track.Id;
        var structure = new GuideStructureService();
        foreach (var (point, i) in track.Segments[0].Points.Select((p, i) => (p, i)))
        { var node = new GuideNode { Name = "节点" + i, TrackId = track.Id, OriginalCoordinate = point.Coordinate }; structure.SnapNode(node, track); project.GuideNodes.Add(node); }
        GuideStructureService.ReplaceSegments(project, structure.RebuildSegments(project));
        project.GuideData.Settings = project.GuideData.Settings with { ElevationSourceKind = GuideElevationSourceKind.Gps };
        return project;
    }
    private GuideDataCoordinator Coordinator(IElevationProvider? provider = null) => new(new(new(_folder)), provider ?? new FakeProvider());
    private sealed class FakeProvider : IElevationProvider
    { public int Calls; public ValueTask<double?> SampleAsync(Guid id, HikingGuide.Core.Geometry.GeoCoordinate coordinate, CancellationToken cancellationToken = default) { Calls++; return ValueTask.FromResult<double?>(999); } }
    private static XDocument Xml(ZipArchive archive, string path) { using var input = archive.GetEntry(path)!.Open(); return XDocument.Load(input); }
    private static XDocument Sheet(ZipArchive archive) => Xml(archive, "xl/worksheets/sheet2.xml");
    private static string Text(XElement cell) => string.Concat(cell.Descendants(Ns + "t").Select(t => t.Value));

    [Fact] public async Task GpsUsesExistingEngineAndNeverReadsDem()
    {
        var project = Fixture(); var provider = new FakeProvider(); var result = await Coordinator(provider).CalculateAsync(project);
        Assert.Equal(0, provider.Calls); Assert.Equal(4, result.Nodes.Count); Assert.Equal(100, result.Nodes[0].ElevationMeters);
        Assert.All(result.Nodes.Skip(1), n => Assert.True(n.IncomingSegment!.IsComplete));
        Assert.All(result.Nodes.Skip(1), n => Assert.Equal(n.IncomingSegment!.UphillDistanceMeters, n.IncomingSegment.GentleUphillDistanceMeters + n.IncomingSegment.SteepUphillDistanceMeters));
        var target = ElevationAnalysisCoordinator.ForGuideSegment(project, project.GuideSegments[0]);
        var p = new LocusTerrainDistanceProfile(); var expected = await new StandardElevationAnalysisV1().AnalyzeAsync(target, new(ElevationSourceType.Gps, null, "GPS", "source"), new(p.SampleSpacingMeters, p.SmoothingDistanceMeters, p.SlopeWindowMeters));
        Assert.Equal(expected.Summary.AscentMeters, result.Nodes[1].IncomingSegment!.AscentMeters);
    }
    [Fact] public async Task MissingGpsRemainsMissingInsteadOfDemOrZero()
    {
        var project = Fixture(gps: false); var provider = new FakeProvider(); var result = await Coordinator(provider).CalculateAsync(project);
        Assert.Equal(0, provider.Calls); Assert.All(result.Nodes, n => Assert.Null(n.ElevationMeters));
        Assert.All(result.Nodes.Skip(1), n => { Assert.Null(n.IncomingSegment!.AscentMeters); Assert.Null(n.IncomingSegment.FlatDistanceMeters); Assert.False(n.IncomingSegment.IsComplete); });
    }
    [Fact] public async Task SpeedAndManualEditsReuseDerivedWithoutNewRevision()
    {
        var project = Fixture(); var coordinator = Coordinator(); var first = await coordinator.CalculateAsync(project);
        project.GuideData.Settings = project.GuideData.Settings with { FlatDownSpeedKmh = 4, GentleUpSpeedKmh = 3, SteepUpSpeedKmh = 2 };
        var key = first.Nodes[1].IncomingSegment!.Key; project.GuideData.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, PlannedDurationHours = 2 };
        var next = await coordinator.CalculateAsync(project); Assert.Same(first, next); Assert.Equal(first.ResultRevision, next.ResultRevision);
    }
    [Fact] public async Task GeometryAndNodePositionChangeInvalidateFingerprint()
    {
        var project = Fixture(); var coordinator = Coordinator(); var fingerprint = coordinator.Fingerprint(project);
        var node = project.GuideNodes[1]; node.ExactPosition = node.ExactPosition! with { DistanceFromTrackStart = node.ExactPosition.DistanceFromTrackStart + 1 };
        Assert.NotEqual(fingerprint, coordinator.Fingerprint(project));
        var result = await coordinator.CalculateAsync(project); Assert.False(result.IsStale);
    }
    [Fact] public async Task CancelCannotPublishDerivedOrChangeManual()
    {
        var project = Fixture(); var manual = project.GuideData.Manual; using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Coordinator().CalculateAsync(project, cancellationToken: cancel.Token));
        Assert.Null(project.GuideData.Derived); Assert.Same(manual, project.GuideData.Manual);
    }
    [Fact] public async Task OldFifteenPercentTerrainCacheRecalculatesAtThirtyFiveWithoutChangingManualData()
    {
        var project = Fixture(); var coordinator = Coordinator(); var nodes = GuideDataReconciler.OrderedNodes(project);
        foreach (var (point, i) in project.MainGuideTrack!.Segments[0].Points.Select((point, i) => (point, i))) point.SourceElevation = 100 + i * 25;
        var first = await coordinator.CalculateAsync(project); var settings = project.GuideData.Settings;
        var oldIdentity = string.Join('|', project.MainGuideTrack.Id, first.GeometryFingerprint, settings.ElevationSourceKind,
            settings.DemDatabaseId, first.ElevationSourceFingerprint, "P10-P8V2-P9Approved-v1",
            string.Join(';', nodes.Select(n => FormattableString.Invariant($"{n.Id:D}:{n.ExactPosition!.SegmentIndex}:{n.ExactPosition.StartPointIndex}:{n.ExactPosition.EndPointIndex}:{n.ExactPosition.Fraction:R}:{n.ExactPosition.DistanceFromTrackStart:R}:{n.ExactPosition.SnappedCoordinate.Latitude:R}:{n.ExactPosition.SnappedCoordinate.Longitude:R}"))));
        var oldFingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(oldIdentity)));
        project.GuideData.Derived = first with { InputFingerprint = oldFingerprint, ProfileVersion = "P10-P8V2-P9Approved-v1",
            Nodes = first.Nodes.Select(n => n.IncomingSegment is not { } s ? n : n with { IncomingSegment = s with { GentleUphillDistanceMeters = 0, SteepUphillDistanceMeters = s.UphillDistanceMeters } }).ToArray() };
        var key = first.Nodes[1].IncomingSegment!.Key;
        project.GuideData.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, PlannedDurationHours = 2.5, RoadCondition = "保留人工路况" };
        project.GuideData.Plans[0].DepartureTimes[nodes[0].Id] = new(6, 0); project.GuideData.Plans[0].DailyNotes[nodes[0].Id] = "保留人工备注";
        var manual = System.Text.Json.JsonSerializer.Serialize(project.GuideData.Manual); var plans = System.Text.Json.JsonSerializer.Serialize(project.GuideData.Plans);
        var next = await coordinator.CalculateAsync(project);
        Assert.NotEqual(oldFingerprint, next.InputFingerprint); Assert.Equal(GuideDataCoordinator.Version, next.ProfileVersion); Assert.Equal(first.ResultRevision + 1, next.ResultRevision);
        Assert.All(next.Nodes.Skip(1), n => { Assert.True(n.IncomingSegment!.GentleUphillDistanceMeters > 0); Assert.Equal(0, n.IncomingSegment.SteepUphillDistanceMeters); });
        Assert.Equal(manual, System.Text.Json.JsonSerializer.Serialize(project.GuideData.Manual)); Assert.Equal(plans, System.Text.Json.JsonSerializer.Serialize(project.GuideData.Plans));
    }
    [Fact] public async Task DynamicPlansLocationsAndTypedValuesMatchSnapshot()
    {
        var project = Fixture(); await Coordinator().CalculateAsync(project);
        project.GuideData.Plans.Add(new() { Order = 2 });
        GuideDataReconciler.AddCondition(project.GuideData.Settings, "狗出没"); GuideDataReconciler.AddCondition(project.GuideData.Settings, "摄影点");
        var nodes = GuideDataReconciler.OrderedNodes(project); var data = project.GuideData;
        foreach (var plan in data.Plans) { plan.DailyStartNodeIds = [nodes[0].Id, nodes[2].Id]; plan.DepartureTimes[nodes[0].Id] = new(23, 30); plan.DepartureTimes[nodes[2].Id] = new(7, 0); }
        for (var i = 1; i < nodes.Count; i++) { var key = new GuideSegmentKey(project.MainGuideTrackId!.Value, nodes[i - 1].Id, nodes[i].Id); data.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, PlannedDurationHours = 1, RoadCondition = "石阶\n防滑" }; }
        data.Manual.Nodes[nodes[1].Id] = new() { GuideNodeId = nodes[1].Id, ActualArrival = new(0, 45) };
        var snapshot = GuideExportSnapshotBuilder.Build(project); var output = Path.Combine(_folder, "三方案.xlsx");
        var hash = SHA256.HashData(File.ReadAllBytes(Template)); await new ExcelGuideExporter().ExportAsync(Template, output, snapshot);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Template))); ExcelGuideExporter.Verify(output, nodes.Count);
        using var archive = ZipFile.OpenRead(output); var sheet = Sheet(archive);
        var headers = sheet.Descendants(Ns + "row").Where(r => (int)r.Attribute("r")! is 3 or 4).Descendants(Ns + "c").Select(Text).ToArray();
        Assert.Equal(3, headers.Count(h => h.StartsWith("行进方案"))); Assert.Equal(3, headers.Count(h => h.Contains("计划抵达时间"))); Assert.Contains("摄影点", headers); Assert.DoesNotContain("参考方案", headers); Assert.DoesNotContain("缓坡爬升距离", headers);
        Assert.Equal("D5", (string?)sheet.Descendants(Ns + "pane").Single().Attribute("topLeftCell"));
        var arrival = snapshot.Columns.ToList().FindIndex(c => c.Key.StartsWith("Arrival:")) + 1;
        var cell = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == ExcelGuideExporter.ColumnName(arrival) + "6");
        Assert.Null(cell.Attribute("t")); Assert.Equal(0.5 / 24, double.Parse(cell.Element(Ns + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture), 12);
        var planCell = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "D7"); Assert.Contains("D1距离", Text(planCell)); Assert.DoesNotContain("终点", Text(planCell)); Assert.DoesNotContain("起点", Text(planCell));
        Assert.Empty(sheet.Descendants(Ns + "f")); Assert.Single(Xml(archive, "xl/workbook.xml").Descendants(Ns + "sheet"));
    }
    [Theory] [InlineData(2)] [InlineData(51)] [InlineData(65)]
    public async Task DynamicRowsNeverRetainTemplateExampleData(int count)
    {
        var project = Fixture(count); var snapshot = GuideExportSnapshotBuilder.Build(project); var path = Path.Combine(_folder, "动态行.xlsx");
        await new ExcelGuideExporter().ExportAsync(Template, path, snapshot); ExcelGuideExporter.Verify(path, count);
        using var zip = ZipFile.OpenRead(path); var rows = Sheet(zip).Descendants(Ns + "row").ToArray();
        Assert.Equal(count, rows.Count(r => (int)r.Attribute("r")! >= 5 && (int)r.Attribute("r")! < count + 5));
        Assert.DoesNotContain(Sheet(zip).Descendants(Ns + "mergeCell"), m => ((string)m.Attribute("ref")!).Contains("D5:D11"));
    }
    [Fact] public async Task RoadContinuationRoundTripsAllUnicodeWithoutSplittingPairs()
    {
        var project = Fixture(2); var a = project.GuideNodes[0].Id; var b = project.GuideNodes[1].Id; var key = new GuideSegmentKey(project.MainGuideTrackId!.Value, a, b);
        var road = new string('路', 32766) + "🏕️\n" + new string('况', 35000);
        project.GuideData.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, RoadCondition = road };
        var snapshot = GuideExportSnapshotBuilder.Build(project); var path = Path.Combine(_folder, "超长路况.xlsx"); await new ExcelGuideExporter().ExportAsync(Template, path, snapshot);
        using var zip = ZipFile.OpenRead(path); var sheet = Sheet(zip); var headers = sheet.Descendants(Ns + "row").Single(r => (int)r.Attribute("r")! == 3).Elements(Ns + "c").Where(c => Text(c).StartsWith("分段路况")).ToArray();
        var body = sheet.Descendants(Ns + "row").Single(r => (int)r.Attribute("r")! == 6).Elements(Ns + "c").ToDictionary(c => ExcelGuideTemplateInspector.ColumnIndex((string)c.Attribute("r")!));
        var parts = headers.Select(h => Text(body[ExcelGuideTemplateInspector.ColumnIndex((string)h.Attribute("r")!)])).ToArray();
        Assert.Equal(road, string.Concat(parts)); Assert.All(parts, s => Assert.True(s.Length <= 32767)); Assert.All(parts, s => Assert.False(char.IsHighSurrogate(s[^1])));
    }
    [Fact] public async Task P101NotesAreAppendedByPlanAndDayWithoutTruncationAndFirstRoadExports()
    {
        var project = Fixture(3); var nodes = GuideDataReconciler.OrderedNodes(project); var data = project.GuideData;
        data.Manual.Nodes[nodes[0].Id] = new() { GuideNodeId = nodes[0].Id, StartRoadCondition = "起点路况" };
        data.Plans[0].DailyStartNodeIds = [nodes[0].Id, nodes[1].Id];
        var longText = new string('注', 32766) + "🏕" + new string('记', 40000);
        data.Plans[0].DailyNotes[nodes[0].Id] = "第一行\n" + longText;
        data.Plans[0].DailyNotes[nodes[1].Id] = "次日备注";
        data.Plans[1].DailyNotes[nodes[0].Id] = "方案二备注";
        await Coordinator().CalculateAsync(project); var snapshot = GuideExportSnapshotBuilder.Build(project);
        Assert.Equal("起点路况", snapshot.Rows[0].Values["RoadCondition"]);
        Assert.Contains("计划总用时", snapshot.Rows[1].Values["Plan:" + data.Plans[0].PlanId]!.ToString());
        var hash = SHA256.HashData(File.ReadAllBytes(Template)); var path = Path.Combine(_folder, "P101备注.xlsx");
        await new ExcelGuideExporter().ExportAsync(Template, path, snapshot); Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Template)));
        using var zip = ZipFile.OpenRead(path); var sheet = Sheet(zip);
        var notes = sheet.Descendants(Ns + "row").Where(r => (int)r.Attribute("r")! > 8).Select(r => Text(r.Elements(Ns + "c").First())).ToArray();
        var start = Array.FindIndex(notes, n => n == "行进方案1 D1：第一行"); Assert.True(start >= 0);
        Assert.Equal(longText, string.Concat(notes.Skip(start + 1).Take(3)));
        Assert.Contains("行进方案1 D2：次日备注", notes); Assert.Contains("行进方案2 D1：方案二备注", notes);
        Assert.All(sheet.Descendants(Ns + "c").Select(Text), t => Assert.True(t.Length <= 32767));
        var roadColumn = snapshot.Columns.ToList().FindIndex(c => c.Key == "RoadCondition") + 1;
        Assert.Equal("起点路况", Text(sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == ExcelGuideExporter.ColumnName(roadColumn) + "5")));
        ExcelGuideExporter.Verify(path, 3);
    }
    [Fact] public async Task P101RevisionExcelPrecisionSummaryAlignmentAndImmediateNotesMatchUi()
    {
        var project = Fixture(3); var nodes = GuideDataReconciler.OrderedNodes(project); var plan = project.GuideData.Plans[0];
        plan.DailyStartNodeIds = [nodes[0].Id, nodes[1].Id]; plan.DepartureTimes[nodes[0].Id] = new(6, 37); plan.DepartureTimes[nodes[1].Id] = new(7, 10);
        for (var i = 1; i < nodes.Count; i++) { var key = new GuideSegmentKey(project.MainGuideTrackId!.Value, nodes[i - 1].Id, nodes[i].Id); project.GuideData.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, PlannedDurationHours = 1.25 }; }
        plan.DailyNotes[nodes[0].Id] = "直接放备注下一行"; await Coordinator().CalculateAsync(project);
        var snapshot = GuideExportSnapshotBuilder.Build(project);
        Assert.Equal(new TimeOnly(6, 37), snapshot.Rows[0].Values["Arrival:" + plan.PlanId]);
        Assert.Equal("D1抵达 07:52\nD2出发 07:10", snapshot.Rows[1].Values["Arrival:" + plan.PlanId]);
        var output = Path.Combine(_folder, "P101精度与备注.xlsx"); await new ExcelGuideExporter().ExportAsync(Template, output, snapshot);
        using var zip = ZipFile.OpenRead(output); var sheet = Sheet(zip); var styles = Xml(zip, "xl/styles.xml");
        var xfs = styles.Root!.Element(Ns + "cellXfs")!.Elements().ToArray();
        var departureCell = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "E5");
        var timeFormatId = (int)xfs[(int)departureCell.Attribute("s")!].Attribute("numFmtId")!;
        Assert.Contains(styles.Descendants(Ns + "numFmt"), f => (int)f.Attribute("numFmtId")! == timeFormatId && (string?)f.Attribute("formatCode") == "hh:mm");
        foreach (var column in snapshot.Columns.Where(c => snapshot.Rows[1].Values[c.Key] is double))
        {
            var col = snapshot.Columns.ToList().IndexOf(column) + 1;
            var cell = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == ExcelGuideExporter.ColumnName(col) + "6");
            var fmt = (int)xfs[(int)cell.Attribute("s")!].Attribute("numFmtId")!;
            var expected = GuideDisplayFormats.Decimals(column.Key);
            Assert.True(expected == 0 ? fmt == 1 : expected == 2 ? fmt == 2 : styles.Descendants(Ns + "numFmt").Any(f => (int)f.Attribute("numFmtId")! == fmt && (string?)f.Attribute("formatCode") == "0.0"));
        }
        var planCell = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "D6");
        Assert.Equal("left", (string?)xfs[(int)planCell.Attribute("s")!].Element(Ns + "alignment")?.Attribute("horizontal"));
        Assert.Contains("距离0.1KM", Text(planCell)); Assert.Contains("总用时1.3h", Text(planCell)); Assert.DoesNotContain("起点", Text(planCell));
        var notesStart = 5 + nodes.Count + 1;
        var immediate = sheet.Descendants(Ns + "row").Single(r => (int)r.Attribute("r")! == notesStart + 1);
        Assert.Equal("行进方案1 D1：直接放备注下一行", Text(immediate.Elements(Ns + "c").First()));
    }
    [Fact] public async Task FailedOrUnconfirmedExportPreservesExistingFileAndTemplate()
    {
        var project = Fixture(2); var output = Path.Combine(_folder, "已有.xlsx"); await File.WriteAllTextAsync(output, "原文件"); var hash = SHA256.HashData(File.ReadAllBytes(output));
        var exporter = new ExcelGuideExporter(); var snapshot = GuideExportSnapshotBuilder.Build(project);
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(Template, output, snapshot)); Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(output)));
        await Assert.ThrowsAnyAsync<Exception>(() => exporter.ExportAsync(Path.Combine(_folder, "不存在.xlsx"), output, snapshot, true)); Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(output)));
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(Template, Template, snapshot, true)); Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }
    [Fact] public void SnapshotBlocksMissingTrackAndTooFewNodes()
    { Assert.Throws<InvalidOperationException>(() => GuideExportSnapshotBuilder.Build(new())); Assert.Throws<InvalidOperationException>(() => GuideExportSnapshotBuilder.Build(Fixture(1))); }
    [Fact] public void DeletedBoundaryWarningExplainsBlankTimesAndExplicitConfirmationRestoresWithoutDem()
    {
        var project = Fixture(3); var nodes = GuideDataReconciler.OrderedNodes(project); var plan = project.GuideData.Plans[0];
        plan.DailyStartNodeIds = [nodes[0].Id, Guid.NewGuid()]; plan.NeedsReview = true; plan.DepartureTimes[nodes[0].Id] = new(6, 0);
        for (var i = 1; i < nodes.Count; i++) { var key = new GuideSegmentKey(project.MainGuideTrackId!.Value, nodes[i - 1].Id, nodes[i].Id); project.GuideData.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, PlannedDurationHours = .8 }; }
        var snapshot = GuideExportSnapshotBuilder.Build(project);
        Assert.Contains(snapshot.Warnings, w => w.Contains("行进方案1有1个每日起点") && w.Contains("计划抵达时间暂停计算") && w.Contains("确认每日边界"));
        Assert.Null(snapshot.Rows[1].Values["Arrival:" + plan.PlanId]);
        GuideDataReconciler.ConfirmExistingDailyStarts(plan, nodes.Select(n => n.Id).ToArray()); snapshot = GuideExportSnapshotBuilder.Build(project);
        Assert.Equal(new TimeOnly(6, 48), snapshot.Rows[1].Values["Arrival:" + plan.PlanId]); Assert.DoesNotContain(snapshot.Warnings, w => w.Contains("计划抵达时间暂停计算"));
    }
    [Fact] public void FirstRowSegmentStatisticsAreZeroAndManualRemainsBlank()
    { var row = GuideExportSnapshotBuilder.Build(Fixture()).Rows[0]; Assert.Equal(0d, row.Values["SegmentDistance"]); Assert.Equal(0d, row.Values["AverageSpeedDuration"]); Assert.Null(row.Values["PlannedDuration"]); }
    private sealed class SynchronousProgress(Action<(int Done, int Total)> callback) : IProgress<(int Done, int Total)>
    { public void Report((int Done, int Total) value) => callback(value); }
    [Fact] public async Task LateCallbackCannotPublishAfterGpsInputChanges()
    {
        var project = Fixture(); var progress = new SynchronousProgress(p => { if (p.Done == 1) lock (project) project.MainGuideTrack!.Segments[0].Points[0].SourceElevation = 888; });
        var result = await Coordinator().CalculateAsync(project, progress); Assert.True(result.IsStale); Assert.Null(project.GuideData.Derived);
    }
    [Fact] public async Task MatchingHistoricalFingerprintRestoresCacheWithoutChangingRevision()
    {
        var project = Fixture(); var coordinator = Coordinator(); var first = await coordinator.CalculateAsync(project);
        project.GuideData.Derived = first with { IsStale = true }; var restored = await coordinator.CalculateAsync(project);
        Assert.False(restored.IsStale); Assert.Equal(first.ResultRevision, restored.ResultRevision);
    }
    [Fact] public async Task TemplateFontsFillsBordersAndOriginalStylesAreUnchanged()
    {
        var snapshot = GuideExportSnapshotBuilder.Build(Fixture()); var output = Path.Combine(_folder, "保真.xlsx"); await new ExcelGuideExporter().ExportAsync(Template, output, snapshot);
        using var original = ZipFile.OpenRead(Template); using var exported = ZipFile.OpenRead(output);
        var before = Xml(original, "xl/styles.xml").Root!; var after = Xml(exported, "xl/styles.xml").Root!;
        foreach (var name in new[] { "fonts", "fills", "borders", "cellStyleXfs" }) Assert.True(XNode.DeepEquals(before.Element(Ns + name), after.Element(Ns + name)));
        var oldXfs = before.Element(Ns + "cellXfs")!.Elements().ToArray(); var newXfs = after.Element(Ns + "cellXfs")!.Elements().ToArray();
        for (var i = 0; i < oldXfs.Length; i++) Assert.True(XNode.DeepEquals(oldXfs[i], newXfs[i]));
        Assert.Equal((string?)Sheet(original).Descendants(Ns + "row").Single(r => (int)r.Attribute("r")! == 6).Attribute("ht"), (string?)Sheet(exported).Descendants(Ns + "row").Single(r => (int)r.Attribute("r")! == 6).Attribute("ht"));
    }
    [Theory] [InlineData(double.NaN)] [InlineData(91)]
    public void InvalidTrackCoordinatesBlockExport(double latitude)
    { var project = Fixture(); project.MainGuideTrack!.Segments[0].Points[0].Latitude = latitude; Assert.Throws<InvalidOperationException>(() => GuideExportSnapshotBuilder.Build(project)); }
    [Fact] public async Task AddingAndDeletingNodesMakePersistedStatisticsStale()
    { var project = Fixture(); await Coordinator().CalculateAsync(project); project.GuideNodes.RemoveAt(1); Assert.True(project.GuideData.Derived!.IsStale); }
}
