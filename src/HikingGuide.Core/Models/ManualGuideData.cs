namespace HikingGuide.Core.Models;

public sealed class ManualGuideData
{
    public string? CpSp { get; set; }

    public string? Plan { get; set; }

    public DateTimeOffset? PlannedArrivalTime { get; set; }

    public DateTimeOffset? ActualArrivalTime { get; set; }

    public TimeSpan? PlannedDuration { get; set; }

    public TimeSpan? ActualSegmentDuration { get; set; }

    public string? RoadCondition { get; set; }

    public string? Camp { get; set; }

    public string? Toilet { get; set; }

    public string? Evacuation { get; set; }

    public string? PublicTransport { get; set; }

    public string? Water { get; set; }

    public string? Resupply { get; set; }

    public string? Notes { get; set; }

    public ManualGuideData DeepCopy() => (ManualGuideData)MemberwiseClone();
}
