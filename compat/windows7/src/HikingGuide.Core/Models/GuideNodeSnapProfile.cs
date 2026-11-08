namespace HikingGuide.Core.Models;

/// <summary>攻略节点吸附告警配置；仅告警，不设置硬性最大吸附距离。</summary>
public sealed class GuideNodeSnapProfile
{
    public const double DefaultAutoSnapWarningDistanceMeters = 50;
    public double AutoSnapWarningDistanceMeters { get; set; } = DefaultAutoSnapWarningDistanceMeters;

    public void Validate()
    {
        if (!double.IsFinite(AutoSnapWarningDistanceMeters) || AutoSnapWarningDistanceMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(AutoSnapWarningDistanceMeters), "吸附告警距离必须是非负有限数值。");
    }
}
