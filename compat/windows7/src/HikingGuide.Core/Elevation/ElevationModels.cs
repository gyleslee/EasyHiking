using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Elevation;

public enum ElevationSourceType { Gps, Dem }
public enum ElevationAvailability { Complete, Partial, Unavailable }
public enum ElevationTargetType { Track, SplitSection, GuideSegment }

public sealed record ElevationSourceDescriptor(ElevationSourceType SourceType,
    Guid? DemDatabaseId, string DisplayName, string SourceFingerprint,
    string? VerticalDatumName = null);

public sealed record ElevationAnalysisProfile(
    double SampleSpacingMeters = 10,
    double SmoothingWindowMeters = 20,
    double SlopeWindowMeters = 10,
    string Version = "StandardElevationAnalysisV2")
{
    public void Validate()
    {
        if (!double.IsFinite(SampleSpacingMeters) || SampleSpacingMeters <= 0 || SampleSpacingMeters > 1000 ||
            !double.IsFinite(SmoothingWindowMeters) || SmoothingWindowMeters < 0 || SmoothingWindowMeters > 1000 ||
            !double.IsFinite(SlopeWindowMeters) || SlopeWindowMeters <= 0 || SlopeWindowMeters > 1000)
            throw new ArgumentOutOfRangeException(nameof(ElevationAnalysisProfile), "高程分析参数超出允许范围。");
    }
}

public sealed record ElevationTargetDescriptor(Guid ProjectId, ElevationTargetType TargetType,
    Guid TargetId, Guid TrackId, string DisplayName, IReadOnlyList<TrackSegment> GeometryParts);

public sealed record ElevationGridPoint(int PartIndex, double DistanceMeters,
    GeoCoordinate Coordinate, double? GpsElevationMeters);

public sealed record ElevationSeriesPoint(int PartIndex, double DistanceMeters,
    GeoCoordinate Coordinate, double? RawElevationMeters, double? ProcessedElevationMeters,
    double? SlopePercent);

public sealed record ElevationSummary(double DistanceMeters, double? StartElevationMeters,
    double? EndElevationMeters, double? MaximumElevationMeters, double? MinimumElevationMeters,
    double? MeanElevationMeters, double? AscentMeters, double? DescentMeters,
    double? NetElevationChangeMeters, double? MeanAscentSlopePercent,
    double? MeanDescentSlopePercent, double? MaximumAscentSlopePercent,
    double? MaximumDescentSlopePercent, double? HighestPointDistanceMeters,
    double? LowestPointDistanceMeters, double CoverageRatio, int SampleCount,
    ElevationAvailability Availability);

public sealed record ElevationAnalysisResult(Guid Id, Guid ProjectId,
    ElevationTargetType TargetType, Guid TargetId, Guid TrackId,
    string GeometryFingerprint, ElevationSourceDescriptor Source,
    string AnalysisProfileVersion, ElevationAnalysisProfile Profile,
    IReadOnlyList<ElevationSeriesPoint> Series, ElevationSummary Summary,
    DateTimeOffset CreatedAt, bool IsStale = false)
{
    public TerrainDistanceClassificationResult? TerrainClassification { get; init; }
    public string CacheKey => string.Join('|', ProjectId, TargetType, TargetId,
        GeometryFingerprint, Source.SourceType, Source.DemDatabaseId,
        Source.SourceFingerprint, AnalysisProfileVersion,
        Profile.SampleSpacingMeters, Profile.SmoothingWindowMeters, Profile.SlopeWindowMeters);
}

public sealed record ElevationComparisonSummary(int CommonValidSamples,
    double? MeanDifferenceMeters, double? MeanAbsoluteDifferenceMeters,
    double? MaximumAbsoluteDifferenceMeters, double? RmseMeters);

public interface IElevationProvider
{
    ValueTask<double?> SampleAsync(Guid demDatabaseId, GeoCoordinate coordinate,
        CancellationToken cancellationToken = default);
}
