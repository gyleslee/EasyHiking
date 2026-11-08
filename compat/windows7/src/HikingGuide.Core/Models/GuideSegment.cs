namespace HikingGuide.Core.Models;

public sealed class GuideSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid StartNodeId { get; set; }

    public Guid EndNodeId { get; set; }

    public double StartDistance { get; set; }

    public double EndDistance { get; set; }

    public Guid? TrackId { get; set; }

    /// <summary>用户自定义显示名称；为空时仍按两端攻略节点自动命名。</summary>
    public string? CustomName { get; set; }

    public SegmentManualData? ManualData { get; set; }

    public double Distance => EndDistance - StartDistance;
}
