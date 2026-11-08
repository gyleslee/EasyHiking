using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Routing;

public sealed class WorldTileSelection(IReadOnlySet<BRouterTile> available)
{
    private readonly HashSet<BRouterTile> _selected = [];
    public IReadOnlySet<BRouterTile> Selected => _selected;
    public IReadOnlySet<BRouterTile> Available => available;

    public bool Toggle(GeoCoordinate point)
    {
        var tile = BRouterTile.At(point);
        if (!available.Contains(tile)) return false;
        if (!_selected.Add(tile)) _selected.Remove(tile);
        return true;
    }

    public int SelectIntersecting(GeoBoundingBox bounds, bool remove = false)
    {
        var changed = 0;
        foreach (var tile in available)
        {
            var cell = tile.Bounds;
            if (cell.MaxLatitude <= bounds.MinLatitude ||
                cell.MinLatitude >= bounds.MaxLatitude ||
                cell.MaxLongitude <= bounds.MinLongitude ||
                cell.MinLongitude >= bounds.MaxLongitude) continue;
            if (remove ? _selected.Remove(tile) : _selected.Add(tile)) changed++;
        }
        return changed;
    }

    public void Clear() => _selected.Clear();
}
