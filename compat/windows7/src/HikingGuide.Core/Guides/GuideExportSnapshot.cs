using HikingGuide.Core.Models;

namespace HikingGuide.Core.Guides;

public sealed record GuideOutputColumn(string Key, string Header, string TemplateKey, bool Time = false, bool Location = false);
public sealed record GuideOutputRow(Guid NodeId, IReadOnlyDictionary<string, object?> Values);
public sealed record GuideExportSnapshot(string TrackName, GuideCalculationSettings Settings,
    IReadOnlyList<GuideOutputColumn> Columns, IReadOnlyList<GuideOutputRow> Rows,
    IReadOnlyDictionary<Guid, GuidePlanTimingResult> PlanTimings, IReadOnlyList<string> Warnings);

/// <summary>业务层生成最终类型化数值；Excel及GUI共享，不依赖公式或窗口。</summary>
public static class GuideExportSnapshotBuilder
{
    public static GuideExportSnapshot Build(HikingProject project)
    {
        lock (project)
        {
            var track = project.MainGuideTrack ?? throw new InvalidOperationException("请先设置攻略轨迹。");
            ValidateTrack(track);
            var nodes = GuideDataReconciler.OrderedNodes(project).ToArray();
            if (nodes.Length < 2) throw new InvalidOperationException("至少需要两个有效攻略节点。");
            var data = project.GuideData; data.Validate();
            var derived = data.Derived?.Nodes.ToDictionary(n => n.GuideNodeId) ?? [];
            var statistics = derived.Values.Where(n => n.IncomingSegment is not null).Select(n => n.IncomingSegment!)
                .ToDictionary(s => s.Key.StorageKey);
            var plans = data.Plans.OrderBy(p => p.Order).ToArray();
            var timings = plans.ToDictionary(p => p.PlanId, p => GuideTimingService.CalculatePlan(track.Id, nodes.Select(n => n.Id).ToArray(), p, data.Manual, statistics));
            var columns = new List<GuideOutputColumn> { new("Index", "编号", "Index"), new("GuideNodeName", "位置（攻略节点）", "GuideNodeName"), new("Elevation", "海拔m", "Elevation") };
            for (var i = 0; i < plans.Length; i++)
            {
                columns.Add(new("Plan:" + plans[i].PlanId, $"行进方案{i + 1}", "TravelPlanPrototype"));
                columns.Add(new("Arrival:" + plans[i].PlanId, $"方案{i + 1}计划抵达时间", "PlannedArrivalPrototype", true));
            }
            columns.AddRange(new[] { new GuideOutputColumn("ActualArrival", "实际抵达时间", "ActualArrival", true),
                new("PlannedDuration", "计划用时h", "PlannedDuration"), new("AverageSpeedDuration", "均速分段用时h", "AverageSpeedDuration"),
                new("NaismithDuration", "奈斯密斯分段用时h", "NaismithDuration"), new("DistanceFromStart", "距起点km", "DistanceFromStart"),
                new("SegmentDistance", "分段距离km", "SegmentDistance"), new("CumulativeAscent", "累计爬升m", "CumulativeAscent"),
                new("SegmentAscent", "分段爬升m", "SegmentAscent"), new("UphillDistance", "分段爬升距离km", "UphillDistance"),
                new("CumulativeDescent", "累计下降m", "CumulativeDescent"), new("SegmentDescent", "分段下降m", "SegmentDescent"),
                new("DownhillDistance", "分段下降距离km", "DownhillDistance"), new("FlatDistance", "平路距离km", "FlatDistance"),
                new("RoadCondition", "分段路况", "RoadCondition") });
            var locations = data.Settings.LocationConditions.Where(d => d.IsActive).OrderBy(d => d.Order).ToArray();
            columns.AddRange(locations.Select(l => new GuideOutputColumn("Location:" + l.DefinitionId, l.Name, "LocationConditionGroup", Location: true)));
            var rows = new List<GuideOutputRow>(); var warnings = new List<string>();
            if (data.Derived is null || data.Derived.IsStale) warnings.Add("高程统计尚未完成或为历史结果，请核对后导出。");
            if (data.Settings.FlatDownSpeedKmh is null || data.Settings.GentleUpSpeedKmh is null || data.Settings.SteepUpSpeedKmh is null) warnings.Add("徒步速度未填写完整，均速分段用时留空。");
            for (var p = 0; p < plans.Length; p++)
                if (timings[plans[p].PlanId].NeedsReview)
                {
                    var activeStarts = nodes.Take(nodes.Length - 1).Select(n => n.Id).ToHashSet();
                    var invalidStarts = plans[p].DailyStartNodeIds.Count(id => !activeStarts.Contains(id));
                    warnings.Add($"行进方案{p + 1}{(invalidStarts > 0 ? $"有{invalidStarts}个每日起点已不存在或不能作为起点" : "的每日边界待确认")}，计划抵达时间暂停计算；请点击“确认每日边界”确认当前行程，或在“行进方案管理”重新设置。");
                }
            if (timings.Values.Any(t => t.Days.Any(d => d.Departure is null))) warnings.Add("部分每日出发时间未填写，相关计划抵达时间留空。");
            if (timings.Values.Any(t => t.Days.Any(d => d.PlannedDurationHours is null))) warnings.Add("部分人工计划用时未填写，相关抵达时间及每日总用时留空。");
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i]; var d = derived.GetValueOrDefault(node.Id);
                var expectedKey = i == 0 ? (GuideSegmentKey?)null : new(track.Id, nodes[i - 1].Id, node.Id);
                var segment = d?.IncomingSegment is { } s && s.Key == expectedKey ? s : null;
                var manual = expectedKey is { } key ? data.Manual.Segments.GetValueOrDefault(key.StorageKey) : null;
                var nodeManual = data.Manual.Nodes.GetValueOrDefault(node.Id);
                var values = new Dictionary<string, object?>
                {
                    ["Index"] = i + 1, ["GuideNodeName"] = string.IsNullOrWhiteSpace(node.Name) ? "未命名节点" : node.Name,
                    ["Elevation"] = d?.ElevationMeters, ["ActualArrival"] = nodeManual?.ActualArrival,
                    ["PlannedDuration"] = manual?.PlannedDurationHours,
                    ["AverageSpeedDuration"] = i == 0 ? 0d : segment is null ? null : GuideTimingService.AverageHours(segment, data.Settings),
                    ["NaismithDuration"] = i == 0 ? 0d : segment is null ? null : GuideTimingService.NaismithHours(segment),
                    ["DistanceFromStart"] = node.ExactPosition!.DistanceFromTrackStart / 1000,
                    ["SegmentDistance"] = i == 0 ? 0d : (node.ExactPosition!.DistanceFromTrackStart - nodes[i - 1].ExactPosition!.DistanceFromTrackStart) / 1000,
                    ["CumulativeAscent"] = d?.CumulativeAscentMeters, ["CumulativeDescent"] = d?.CumulativeDescentMeters,
                    ["SegmentAscent"] = i == 0 ? 0d : segment?.AscentMeters, ["SegmentDescent"] = i == 0 ? 0d : segment?.DescentMeters,
                    ["UphillDistance"] = i == 0 ? 0d : segment?.UphillDistanceMeters / 1000,
                    ["DownhillDistance"] = i == 0 ? 0d : segment?.DownhillDistanceMeters / 1000,
                    ["FlatDistance"] = i == 0 ? 0d : segment?.FlatDistanceMeters / 1000,
                    ["RoadCondition"] = i == 0 ? nodeManual?.StartRoadCondition ?? "" : manual?.RoadCondition ?? ""
                };
                foreach (var plan in plans)
                {
                    var timing = timings[plan.PlanId]; var labels = new List<string>();
                    foreach (var day in timing.Days)
                    {
                        if (day.EndNodeId == node.Id) labels.Add($"D{day.DayNumber}终点\n{Summary(day)}");
                        if (day.StartNodeId == node.Id) labels.Add($"D{day.DayNumber}起点");
                    }
                    values["Plan:" + plan.PlanId] = string.Join('\n', labels);
                    var arrival = timing.Arrivals.GetValueOrDefault(node.Id);
                    var departure = timing.Departures.GetValueOrDefault(node.Id);
                    values["Arrival:" + plan.PlanId] = departure is not null && arrival is not null
                        ? $"D{arrival.PlanDayNumber}抵达 {arrival.Time:HH:mm}\nD{departure.PlanDayNumber}出发 {departure.Time:HH:mm}"
                        : (object?)departure?.Time ?? arrival?.Time;
                }
                foreach (var location in locations) values["Location:" + location.DefinitionId] = DisplayLocation(nodeManual?.LocationConditions.GetValueOrDefault(location.DefinitionId) ?? LocationConditionValue.Blank);
                rows.Add(new(node.Id, values));
            }
            if (rows.Any(r => r.Values["Elevation"] is null)) warnings.Add("部分节点缺少所选来源高程；不会用另一来源补洞。");
            if (rows.Skip(1).Any(r => string.IsNullOrWhiteSpace((string?)r.Values["RoadCondition"]))) warnings.Add("部分分段路况未填写。");
            if (rows.Any(r => locations.Any(l => (string?)r.Values["Location:" + l.DefinitionId] == ""))) warnings.Add("部分地点情况未填写。");
            if (rows.Any(r => ((string?)r.Values["RoadCondition"])?.Length > 32767)) warnings.Add("超长分段路况将按顺序拆入续列，不会截断。");
            return new(track.Name ?? "未命名轨迹", data.Settings, columns, rows, timings, warnings);
        }
    }

    public static string DisplayLocation(LocationConditionValue value) => value switch { LocationConditionValue.Yes => "✔", LocationConditionValue.Unknown => "不确定", LocationConditionValue.No => "✘", _ => "" };
    public static void ValidateTrack(Track track)
    {
        if (track.Segments.All(s => s.Points.Count < 2) || track.Segments.SelectMany(s => s.Points).Any(p =>
            !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude) || p.Latitude is < -90 or > 90 || p.Longitude is < -180 or > 180))
            throw new InvalidOperationException("攻略轨迹缺少有效线段或包含无效坐标，不能计算和导出。");
    }
    public static string Summary(GuideDailyItinerary day) => GuideDisplayFormats.Summary(day);
}
