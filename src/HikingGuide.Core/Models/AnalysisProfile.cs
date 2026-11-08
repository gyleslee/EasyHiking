namespace HikingGuide.Core.Models;

public sealed class AnalysisProfile
{
    public string Name { get; set; } = "未配置";

    /// <summary>重采样距离，单位：米。</summary>
    public double? ResampleDistance { get; set; }

    public SmoothingMethod SmoothingMethod { get; set; }

    public double? SmoothingStrength { get; set; }

    /// <summary>坡度计算窗口，单位：米。</summary>
    public double? SlopeWindow { get; set; }

    /// <summary>平路坡度阈值。采用比例值，例如 0.12 表示 12%。</summary>
    public double? FlatThreshold { get; set; }

    /// <summary>累计高程增益判定阈值，单位：米。</summary>
    public double? ElevationGainThreshold { get; set; }

    /// <summary>
    /// 仅供自动测试使用，不代表经过校准的 Locus 参数。
    /// </summary>
    public static AnalysisProfile CreateTestProfile() => new()
    {
        Name = "自动测试配置",
        ResampleDistance = 10,
        SmoothingMethod = SmoothingMethod.MovingAverage,
        SmoothingStrength = 3,
        SlopeWindow = 50,
        FlatThreshold = 0.03,
        ElevationGainThreshold = 3
    };
}
