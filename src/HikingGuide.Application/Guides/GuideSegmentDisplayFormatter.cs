using HikingGuide.Core.Models;

namespace HikingGuide.Application.Guides;

public static class GuideSegmentDisplayFormatter
{
    public static string Format(HikingProject project, GuideSegment segment)
    {
        var name = string.IsNullOrWhiteSpace(segment.CustomName)
            ? DefaultName(project, segment) : segment.CustomName.Trim();
        return $"{name} {segment.Distance / 1000:F2} km";
    }

    public static string DefaultName(HikingProject project, GuideSegment segment)
    {
        var start = Name(project.GuideNodes.FirstOrDefault(node => node.Id == segment.StartNodeId));
        var end = Name(project.GuideNodes.FirstOrDefault(node => node.Id == segment.EndNodeId));
        return $"{start} → {end}";
    }

    private static string Name(GuideNode? node) => string.IsNullOrWhiteSpace(node?.Name)
        ? "未命名节点" : node.Name.Trim();
}
