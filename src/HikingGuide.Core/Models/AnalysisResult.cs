namespace HikingGuide.Core.Models;

public sealed class AnalysisResult
{
    public double TotalDistance { get; set; }

    public double TotalAscent { get; set; }

    public double TotalDescent { get; set; }

    public double UphillDistance { get; set; }

    public double DownhillDistance { get; set; }

    public double FlatDistance { get; set; }

    public double? MinElevation { get; set; }

    public double? MaxElevation { get; set; }

    public List<SegmentAnalysisResult> SegmentResults { get; set; } = [];
}
