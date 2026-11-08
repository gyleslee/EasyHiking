namespace HikingGuide.Core.Models;

/// <summary>由一个或多个连续物理 TrackSegment 组成的逻辑拆分分段；间隙不是几何。</summary>
public sealed record SplitSection(Guid Id, Guid TrackId, string Name,
    IReadOnlyList<TrackSegment> GeometryParts, int DisplayOrder)
{
    public int PointCount => GeometryParts.Sum(part => part.PointCount);
    public double Length => GeometryParts.Sum(part => part.Length);
    public TrackPoint? StartPoint => GeometryParts.FirstOrDefault(part => !part.IsEmpty)?.StartPoint;
    public TrackPoint? EndPoint => GeometryParts.LastOrDefault(part => !part.IsEmpty)?.EndPoint;
}
