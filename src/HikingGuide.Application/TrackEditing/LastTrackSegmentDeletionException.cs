namespace HikingGuide.Application.TrackEditing;

/// <summary>删除最后一个分段需要用户改用明确的整轨删除操作。</summary>
public sealed class LastTrackSegmentDeletionException : InvalidOperationException
{
    public LastTrackSegmentDeletionException(bool isMainGuideTrack = false) : base(
        "当前选中的是该轨迹的最后一个轨迹段，删除它就等于删除整条轨迹。\n\n" +
        "如只想删除部分内容，请先拆分轨迹，或使用区间裁剪功能。\n" +
        (isMainGuideTrack ? "主攻略轨迹如需拆分，请先转为普通轨迹，所属攻略节点会同步转为普通标注。\n" : "") +
        "如要删除整条轨迹，请在左侧“项目与节点”面板中右击该轨迹，选择“删除轨迹”。\n\n" +
        "本次未删除任何轨迹或节点。") { }
}
