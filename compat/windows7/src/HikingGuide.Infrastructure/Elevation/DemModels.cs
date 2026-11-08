using HikingGuide.Core.Geometry;

namespace HikingGuide.Infrastructure.Elevation;

public enum DemDatabaseStatus { Ready, Disconnected, Empty, Invalid }
public enum DemRasterFormat { GeoTiff, Hgt }

public sealed record DemDatabase(Guid Id, string Name, string RootPath,
    string? VerticalDatumName = null, bool UnitMetersConfirmed = false,
    DemDatabaseStatus Status = DemDatabaseStatus.Empty,
    string Fingerprint = "", int ValidFileCount = 0, int InvalidFileCount = 0,
    DateTimeOffset? LastScannedAt = null);

public sealed record RasterIndexEntry(string FilePath, string RelativePath,
    DemRasterFormat Format, GeoBoundingBox Wgs84Coverage, string NativeCrs,
    int PixelWidth, int PixelHeight, double[] GeoTransform,
    double ResolutionMeters, double? NoDataValue, string UnitType,
    DateTime LastWriteTimeUtc, long FileSize);

public sealed record DemScanProgress(int Discovered, int Valid, int Invalid,
    string? CurrentFile);

public sealed record DemScanResult(DemDatabase Database,
    IReadOnlyList<RasterIndexEntry> Entries, IReadOnlyList<string> Warnings);

public enum DemSampleStatus { Valid, NoCoverage, NoData, DatabaseDisconnected, InvalidUnit }
public sealed record DemSample(DemSampleStatus Status, double? ElevationMeters,
    Guid DemDatabaseId, string? SourceFile, string InterpolationMethod = "Bilinear",
    string? QualityFlag = null);
