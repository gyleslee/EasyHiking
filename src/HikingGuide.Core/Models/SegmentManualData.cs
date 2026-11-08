namespace HikingGuide.Core.Models;

public sealed class SegmentManualData
{
    public string? RoadCondition { get; set; }
    public string? Description { get; set; }
    public TimeSpan? PlannedDuration { get; set; }

    public SegmentManualData DeepCopy() => (SegmentManualData)MemberwiseClone();
}
