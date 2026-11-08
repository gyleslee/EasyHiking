namespace HikingGuide.Application.Maps;

public sealed class MapDisplayProfile
{
    public static MapDisplayProfile Default { get; } = new();

    public IReadOnlyList<MapZoomTolerance> ZoomTolerances { get; init; } =
    [
        new(6, 500),
        new(10, 120),
        new(14, 25),
        new(17, 5),
        new(19, 1.5),
        new(24, 0.4)
    ];

    public double GetToleranceMeters(double zoom)
    {
        var tier = ZoomTolerances.FirstOrDefault(item => zoom <= item.MaximumZoom);
        return tier?.ToleranceMeters ?? ZoomTolerances[^1].ToleranceMeters;
    }

    public int GetCacheZoomBucket(double zoom)
    {
        for (var index = 0; index < ZoomTolerances.Count; index++)
            if (zoom <= ZoomTolerances[index].MaximumZoom) return index;
        return ZoomTolerances.Count - 1;
    }
}

public sealed record MapZoomTolerance(double MaximumZoom, double ToleranceMeters);
