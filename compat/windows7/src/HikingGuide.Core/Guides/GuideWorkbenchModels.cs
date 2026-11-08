using HikingGuide.Core.Elevation;

namespace HikingGuide.Core.Guides;

public enum GuideElevationSourceKind { Dem, Gps }
public enum LocationConditionValue { Blank, Yes, Unknown, No }

public sealed record GuideCalculationSettings
{
    public int SchemaVersion { get; init; } = 1;
    public GuideElevationSourceKind ElevationSourceKind { get; init; } = GuideElevationSourceKind.Dem;
    public Guid? DemDatabaseId { get; init; }
    public double? FlatDownSpeedKmh { get; init; }
    public double? GentleUpSpeedKmh { get; init; }
    public double? SteepUpSpeedKmh { get; init; }
    public List<LocationConditionDefinition> LocationConditions { get; init; } = CreateDefaults();
    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("攻略数据版本不受支持。");
        foreach (var speed in new[] { FlatDownSpeedKmh, GentleUpSpeedKmh, SteepUpSpeedKmh })
            if (speed.HasValue && (!double.IsFinite(speed.Value) || speed.Value <= 0)) throw new ArgumentException("速度必须大于0。");
        if (LocationConditions.Any(d => d.DefinitionId == Guid.Empty || string.IsNullOrWhiteSpace(d.Name)) ||
            LocationConditions.Select(d => d.DefinitionId).Distinct().Count() != LocationConditions.Count ||
            LocationConditions.Where(d => d.IsActive).Select(d => d.Name).Distinct(StringComparer.Ordinal).Count() != LocationConditions.Count(d => d.IsActive))
            throw new ArgumentException("地点情况存在无效标识、空名称或重复有效名称。");
    }
    private static List<LocationConditionDefinition> CreateDefaults() => new[] { "营地", "下撤", "补给", "补生水" }
        .Select((name, order) => new LocationConditionDefinition { Name = name, Order = order, BuiltInKey = name }).ToList();
}

public sealed record LocationConditionDefinition
{
    public Guid DefinitionId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public int Order { get; init; }
    public bool IsActive { get; init; } = true;
    public string? BuiltInKey { get; init; }
}

public readonly record struct GuideSegmentKey(Guid TrackId, Guid FromGuideNodeId, Guid ToGuideNodeId)
{
    public string StorageKey => $"{TrackId:D}/{FromGuideNodeId:D}/{ToGuideNodeId:D}";
}
public sealed record GuideNodeManualData
{
    public Guid GuideNodeId { get; init; }
    // 首行没有前一分段，路况按节点保存；节点关系变化时保留人工原文。
    public string StartRoadCondition { get; init; } = "";
    public TimeOnly? ActualArrival { get; init; }
    public Dictionary<Guid, LocationConditionValue> LocationConditions { get; init; } = [];
}
public sealed record GuideSegmentManualData
{
    public GuideSegmentKey SegmentKey { get; init; }
    public double? PlannedDurationHours { get; init; }
    public string RoadCondition { get; init; } = "";
    public void Validate()
    {
        if (PlannedDurationHours is double hours && (!double.IsFinite(hours) || hours < 0))
            throw new ArgumentException("计划用时不能为负数或无效数值。");
    }
}
public sealed record ArchivedGuideSegmentManualData(GuideSegmentManualData Data, DateTimeOffset ArchivedAt, string Reason);
public sealed class GuideManualDataStore
{
    public Dictionary<Guid, GuideNodeManualData> Nodes { get; set; } = [];
    public Dictionary<string, GuideSegmentManualData> Segments { get; set; } = [];
    public List<ArchivedGuideSegmentManualData> ArchivedSegments { get; set; } = [];
}
public sealed class GuideTravelPlan
{
    public Guid PlanId { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Guid> DailyStartNodeIds { get; set; } = [];
    public Dictionary<Guid, TimeOnly?> DepartureTimes { get; set; } = [];
    public Dictionary<Guid, string> DailyNotes { get; set; } = [];
    public bool NeedsReview { get; set; }
}
public sealed class GuideWorkbenchData
{
    public GuideCalculationSettings Settings { get; set; } = new();
    public GuideManualDataStore Manual { get; set; } = new();
    public List<GuideTravelPlan> Plans { get; set; } = [new() { Order = 0 }, new() { Order = 1 }];
    public GuideDataCalculationResult? Derived { get; set; }
    public void Validate()
    {
        Settings.Validate();
        if (Plans.Count == 0 || Plans.Any(p => p.PlanId == Guid.Empty) || Plans.Select(p => p.PlanId).Distinct().Count() != Plans.Count)
            throw new ArgumentException("至少保留一个具有唯一标识的行进方案。");
        foreach (var (key, data) in Manual.Segments)
        {
            data.Validate();
            if (key != data.SegmentKey.StorageKey) throw new ArgumentException("人工分段身份与存储标识不一致。");
        }
        foreach (var (key, data) in Manual.Nodes)
            if (key != data.GuideNodeId) throw new ArgumentException("人工节点身份与存储标识不一致。");
        foreach (var data in Manual.ArchivedSegments) data.Data.Validate();
    }
}

public sealed record GuideSegmentStatistics(GuideSegmentKey Key, double DistanceMeters,
    double? AscentMeters, double? DescentMeters, double? UphillDistanceMeters,
    double? DownhillDistanceMeters, double? FlatDistanceMeters, double? GentleUphillDistanceMeters,
    double? SteepUphillDistanceMeters, double UnclassifiedDistanceMeters, bool IsComplete);
public sealed record GuideNodeDerivedData(Guid GuideNodeId, double ChainageMeters, double? ElevationMeters,
    double? CumulativeAscentMeters, double? CumulativeDescentMeters, GuideSegmentStatistics? IncomingSegment);
public sealed record GuideDataCalculationResult(string InputFingerprint, string GeometryFingerprint,
    string ElevationSourceFingerprint, string ProfileVersion, long ResultRevision,
    DateTimeOffset CalculatedAt, IReadOnlyList<GuideNodeDerivedData> Nodes, bool IsStale = false, double? CoverageRatio = null);

public sealed record GuideTerrainBreakdown(double UphillMeters, double DownhillMeters, double FlatMeters,
    double GentleUphillMeters, double SteepUphillMeters, double UnclassifiedMeters);
public sealed record GuidePlannedArrival(int PlanDayNumber, TimeOnly Time, int DayOffset, double ElapsedHours);
public sealed record GuideDailyItinerary(int DayNumber, Guid StartNodeId, Guid EndNodeId,
    IReadOnlyList<GuideSegmentKey> Segments, TimeOnly? Departure,
    double? DistanceMeters, double? AscentMeters, double? DescentMeters, double? PlannedDurationHours)
{
    public string Notes { get; init; } = "";
}
public sealed record GuidePlanTimingResult(Guid PlanId, IReadOnlyList<GuideDailyItinerary> Days,
    IReadOnlyDictionary<Guid, GuidePlannedArrival?> Arrivals, bool NeedsReview)
{
    public IReadOnlyDictionary<Guid, GuidePlannedArrival> Departures { get; init; } = new Dictionary<Guid, GuidePlannedArrival>();
}
