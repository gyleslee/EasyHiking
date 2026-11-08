using HikingGuide.Core.Models;

namespace HikingGuide.Core.Guides;

public sealed record GuideReconcileResult(int ArchivedSegmentCount, IReadOnlyList<Guid> PlansNeedingReview);

public static class GuideDataReconciler
{
    /// <summary>仅在用户明确确认现存每日边界后调用；失效节点的时间和备注保留，不能猜测替代节点。</summary>
    public static int ConfirmExistingDailyStarts(GuideTravelPlan plan, IReadOnlyList<Guid> orderedNodeIds)
    {
        if (orderedNodeIds.Count < 2 || orderedNodeIds.Distinct().Count() != orderedNodeIds.Count)
            throw new ArgumentException("确认每日边界至少需要两个具有唯一标识的有效攻略节点。");
        var active = orderedNodeIds.Take(orderedNodeIds.Count - 1).ToHashSet();
        var removed = plan.DailyStartNodeIds.RemoveAll(id => !active.Contains(id));
        if (!plan.DailyStartNodeIds.Contains(orderedNodeIds[0])) plan.DailyStartNodeIds.Insert(0, orderedNodeIds[0]);
        plan.NeedsReview = false; plan.ModifiedAt = DateTimeOffset.UtcNow;
        return removed;
    }

    public static IReadOnlyList<GuideNode> OrderedNodes(HikingProject project) => project.MainGuideTrackId is null ? [] : project.GuideNodes
        .Where(node => node.TrackId == project.MainGuideTrackId && !node.IsOrphaned && node.ExactPosition is not null)
        .OrderBy(node => node.ExactPosition!.DistanceFromTrackStart).ThenBy(node => node.StableOrder).ThenBy(node => node.Id).ToArray();

    public static GuideReconcileResult Reconcile(GuideWorkbenchData data, Guid trackId, IReadOnlyList<Guid> orderedNodeIds)
    {
        if (orderedNodeIds.Distinct().Count() != orderedNodeIds.Count) throw new ArgumentException("攻略节点标识重复。");
        var active = new HashSet<string>();
        for (var i = 1; i < orderedNodeIds.Count; i++) active.Add(new GuideSegmentKey(trackId, orderedNodeIds[i - 1], orderedNodeIds[i]).StorageKey);
        var archiveKeys = data.Manual.Segments.Keys.Where(key => !active.Contains(key)).ToArray();
        foreach (var key in archiveKeys)
        {
            data.Manual.ArchivedSegments.Add(new(data.Manual.Segments[key], DateTimeOffset.UtcNow, "攻略节点相邻关系发生变化"));
            data.Manual.Segments.Remove(key);
        }
        foreach (var id in orderedNodeIds) data.Manual.Nodes.TryAdd(id, new() { GuideNodeId = id });
        foreach (var plan in data.Plans)
        {
            if (plan.DailyStartNodeIds.Any(id => !orderedNodeIds.Contains(id) || orderedNodeIds.Count > 0 && id == orderedNodeIds[^1])) plan.NeedsReview = true;
            if (orderedNodeIds.Count > 0 && !plan.DailyStartNodeIds.Contains(orderedNodeIds[0])) plan.DailyStartNodeIds.Insert(0, orderedNodeIds[0]);
        }
        if (data.Derived is not null) data.Derived = data.Derived with { IsStale = true };
        return new(archiveKeys.Length, data.Plans.Where(p => p.NeedsReview).Select(p => p.PlanId).ToArray());
    }

    public static readonly IReadOnlyList<string> LocationPresets = ["营地", "下撤", "补给", "补生水", "洗手间", "公共交通", "住宿", "商店", "餐饮", "Wi-Fi", "充电", "热水", "山屋", "动物", "直饮水", "垃圾桶", "淋浴间", "烧烤炉", "凉亭"];

    public static LocationConditionDefinition AddCondition(GuideCalculationSettings settings, string name, string? builtInKey = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("地点情况名称不能为空。");
        if (settings.LocationConditions.Any(d => d.IsActive && d.Name == name)) throw new ArgumentException("地点情况已存在。");
        var old = settings.LocationConditions.FirstOrDefault(d => !d.IsActive && (builtInKey is not null ? d.BuiltInKey == builtInKey : d.Name == name));
        var result = old is null ? new LocationConditionDefinition { Name = name, BuiltInKey = builtInKey, Order = settings.LocationConditions.Count } : old with { Name = name, IsActive = true };
        if (old is not null) settings.LocationConditions.Remove(old);
        settings.LocationConditions.Add(result); return result;
    }

    public static void RemoveCondition(GuideCalculationSettings settings, Guid id)
    {
        var old = settings.LocationConditions.FirstOrDefault(d => d.DefinitionId == id) ?? throw new ArgumentException("地点情况不存在。");
        settings.LocationConditions[settings.LocationConditions.IndexOf(old)] = old with { IsActive = false };
    }
}
