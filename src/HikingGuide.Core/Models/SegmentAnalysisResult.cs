namespace HikingGuide.Core.Models;

public sealed class SegmentAnalysisResult
{
    public Guid GuideSegmentId { get; set; }

    public double SegmentDistance { get; set; }

    public double CumulativeDistance { get; set; }

    public double SegmentAscent { get; set; }

    public double CumulativeAscent { get; set; }

    public double SegmentDescent { get; set; }

    public double CumulativeDescent { get; set; }

    public double UphillDistance { get; set; }

    public double DownhillDistance { get; set; }

    public double FlatDistance { get; set; }

    public double? MinElevation { get; set; }

    public double? MaxElevation { get; set; }
}
