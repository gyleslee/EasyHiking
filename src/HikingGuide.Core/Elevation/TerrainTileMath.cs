namespace HikingGuide.Core.Elevation;

public readonly record struct ContourPoint(double X, double Y);
public sealed record ContourLine(double Elevation, IReadOnlyList<ContourPoint> Points);

/// <summary>无GUI的地图显示算法。网格坐标采用向右、向下方向；缺测格不插值填补。</summary>
public static class TerrainTileMath
{
    public const string AlgorithmVersion = "terrain-1";
    public const int GridCells = 128;
    public const double NormalContourStrokeWidth=1.7;
    public const double MajorContourStrokeWidth=3.4;
    public static bool IsMajorContour(double elevation,int intervalMeters)=>
        intervalMeters>0&&double.IsFinite(elevation)&&Math.Abs(elevation)>1e-8&&
        Math.Abs(elevation/intervalMeters-Math.Round(elevation/intervalMeters))<1e-8;
    public static double ContourStrokeWidth(double elevation,bool majorEnabled,int intervalMeters)=>
        majorEnabled&&IsMajorContour(elevation,intervalMeters)?MajorContourStrokeWidth:NormalContourStrokeWidth;
    public const double LightAzimuth = 315;
    public const double LightAltitude = 45;
    public static int ContourInterval(int zoom) => zoom < 13 ? 0 : zoom < 15 ? 100 : 10;

    public static (double Latitude, double Longitude) Coordinate(int zoom, int tileX, int tileY, double gx, double gy)
    {
        var n = Math.Pow(2, zoom);
        return (Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (tileY + gy / GridCells) / n))) * 180 / Math.PI,
            (tileX + gx / GridCells) / n * 360 - 180);
    }

    public static double GroundStep(int zoom, double latitude) =>
        40075016.68557849 * Math.Cos(latitude * Math.PI / 180) / (Math.Pow(2, zoom) * GridCells);

    public static double Shade(double west, double east, double north, double south, double groundStep)
    {
        var dx = (east - west) / (2 * groundStep);
        var dy = (north - south) / (2 * groundStep);
        var a = LightAzimuth * Math.PI / 180; var h = LightAltitude * Math.PI / 180;
        return Math.Clamp((-dx * Math.Sin(a) * Math.Cos(h) - dy * Math.Cos(a) * Math.Cos(h) + Math.Sin(h)) /
            Math.Sqrt(1 + dx * dx + dy * dy), 0, 1);
    }

    // Marching squares with asymptotic saddle decider; identical world-grid edges stitch across tiles.
    public static IReadOnlyList<ContourLine> Contours(double?[,] grid, int interval, CancellationToken token = default)
    {
        if (interval <= 0) return [];
        var segments = new Dictionary<double, List<(ContourPoint A, ContourPoint B)>>();
        var height = grid.GetLength(0); var width = grid.GetLength(1);
        for (var y = 0; y < height - 1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width - 1; x++)
            {
                double?[] values = [grid[y,x],grid[y,x+1],grid[y+1,x+1],grid[y+1,x]];
                if (values.Any(v => !v.HasValue || !double.IsFinite(v.Value))) continue;
                var v = values.Select(a => a!.Value).ToArray();
                var min = v.Min(); var max = v.Max();
                // Guard invalid DEMs without allocating unbounded geometry.
                if (min < -12000 || max > 10000) continue;
                for (var level = Math.Ceiling(min / interval) * interval; level <= max; level += interval)
                {
                    if (level == 0) continue;
                    ContourPoint[] corners = [new(x,y),new(x+1,y),new(x+1,y+1),new(x,y+1)];
                    var hits = new Dictionary<int, ContourPoint>();
                    for (var e = 0; e < 4; e++)
                    {
                        var b = (e + 1) % 4;
                        if ((v[e] >= level) == (v[b] >= level)) continue;
                        var t = (level - v[e]) / (v[b] - v[e]);
                        hits[e] = new(corners[e].X + t * (corners[b].X - corners[e].X), corners[e].Y + t * (corners[b].Y - corners[e].Y));
                    }
                    void Add(int a, int b)
                    {
                        if (!segments.TryGetValue(level, out var list)) segments[level] = list = [];
                        if (hits[a] != hits[b]) list.Add((hits[a],hits[b]));
                    }
                    if (hits.Count == 2) {var edges = hits.Keys.ToArray(); Add(edges[0],edges[1]);}
                    else if (hits.Count == 4)
                    {
                        var q = (v[0]-level)*(v[2]-level)-(v[1]-level)*(v[3]-level);
                        if (q >= 0) {Add(0,1); Add(2,3);} else {Add(0,3); Add(1,2);}
                    }
                }
            }
        }
        var result = new List<ContourLine>();
        foreach (var (level, list) in segments.OrderBy(p => p.Key))
        {
            static (long,long) Key(ContourPoint p) => ((long)Math.Round(p.X*1e7),(long)Math.Round(p.Y*1e7));
            var links = new Dictionary<(long,long),List<int>>();
            for (var i=0;i<list.Count;i++) foreach (var p in new[]{list[i].A,list[i].B})
            {var k=Key(p);if(!links.TryGetValue(k,out var ids))links[k]=ids=[];ids.Add(i);}
            var used = new bool[list.Count];
            foreach (var start in Enumerable.Range(0,list.Count).OrderBy(i => links[Key(list[i].A)].Count==1 || links[Key(list[i].B)].Count==1 ? 0 : 1))
            {
                if (used[start]) continue;
                var first = links[Key(list[start].B)].Count==1 ? list[start].B : list[start].A;
                var points = new List<ContourPoint>{first}; var current=first; var next=start;
                while (next >= 0)
                {
                    used[next]=true;var s=list[next]; current=Key(s.A)==Key(current)?s.B:s.A;points.Add(current);
                    next=links[Key(current)].FirstOrDefault(i=>!used[i],-1);
                }
                result.Add(new(level,points));
            }
        }
        return result;
    }
}
