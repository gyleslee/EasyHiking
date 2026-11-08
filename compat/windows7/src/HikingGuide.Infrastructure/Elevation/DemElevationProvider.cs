using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace HikingGuide.Infrastructure.Elevation;

/// <summary>按空间索引查询；所有 GDAL handle 在受控 LRU 中只读使用。</summary>
public sealed class DemElevationProvider : IElevationProvider, IDisposable
{
    private sealed class RasterHandle : IDisposable
    {
        public sealed record PixelWindow(int Left,int Top,int Width,int Height,double[] Values,byte[] Mask);
        public Dictionary<(int X,int Y),PixelWindow> Windows { get; }=[];
        public LinkedList<(int X,int Y)> WindowLru { get; }=[];
        public required Dataset Dataset { get; init; }
        public required Band Band { get; init; }
        public required Band Mask { get; init; }
        public required SpatialReference Native { get; init; }
        public required SpatialReference Wgs84 { get; init; }
        public required CoordinateTransformation Transformation { get; init; }
        public DateTime IndexedWriteTimeUtc {get;init;}
        public long IndexedFileSize {get;init;}
        public void Dispose()
        {
            Transformation.Dispose();
            Wgs84.Dispose();
            Native.Dispose();
            Mask.Dispose();
            Band.Dispose();
            Dataset.Dispose();
        }
    }

    private readonly DemDatabaseCatalog _catalog;
    private readonly object _gate = new();
    // 所有采样均持有_gate；复用GDAL调用缓冲，避免每个像元产生短命数组。
    private readonly double[] _transformed = new double[3];
    private readonly double[] _values = new double[4];
    private readonly byte[] _mask = new byte[4];
    private readonly Dictionary<string, RasterHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = [];
    private readonly int _maximumOpenHandles;
    private readonly int _rasterWindowCacheCapacity;
    private long _hits;
    private long _misses;
    private bool _disposed;

    public DemElevationProvider(DemDatabaseCatalog catalog, int maximumOpenHandles = 8,int rasterWindowCacheCapacity=0)
    {
        if (maximumOpenHandles < 1 || maximumOpenHandles > 64)
            throw new ArgumentOutOfRangeException(nameof(maximumOpenHandles));
        _catalog = catalog;
        _maximumOpenHandles = maximumOpenHandles;
        if(rasterWindowCacheCapacity is <0 or >128)throw new ArgumentOutOfRangeException(nameof(rasterWindowCacheCapacity));
        _rasterWindowCacheCapacity=rasterWindowCacheCapacity;
        DemGdalRuntime.EnsureReady();
    }

    public (int OpenHandles, int Limit, long Hits, long Misses) CacheStatus
    {
        get { lock (_gate) return (_handles.Count, _maximumOpenHandles, _hits, _misses); }
    }

    public ValueTask<double?> SampleAsync(Guid demDatabaseId, GeoCoordinate coordinate,
        CancellationToken cancellationToken = default) =>
        new(SampleDetailed(demDatabaseId, coordinate, cancellationToken).ElevationMeters);

    public DemSample SampleDetailed(Guid databaseId, GeoCoordinate coordinate,
        CancellationToken cancellationToken = default)
    {
        coordinate.Validate();
        var database = _catalog.Get(databaseId);
        if (!Directory.Exists(database.RootPath))
            return new(DemSampleStatus.DatabaseDisconnected, null, databaseId, null);
        var candidates = _catalog.FindCovering(databaseId, coordinate);
        if (candidates.Count == 0)
            return new(DemSampleStatus.NoCoverage, null, databaseId, null);
        string? rasterError = null;
        lock (_gate)
        {
            HikingGuide.Core.Compatibility.LegacyDisposed.ThrowIf(_disposed, this);
            foreach (var entry in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(entry.FilePath)) continue;
                var factor = UnitFactor(entry.UnitType, database.UnitMetersConfirmed);
                if (factor is null)
                    return new(DemSampleStatus.InvalidUnit, null, databaseId,
                        entry.FilePath, QualityFlag: "海拔单位未知，请先确认该数据库单位为米。");
                try
                {
                    var value = ReadBilinear(GetHandle(entry), entry, coordinate);
                    if (value.HasValue)
                        return new(DemSampleStatus.Valid, value.Value * factor.Value,
                            databaseId, entry.FilePath);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // 一个重叠栅格失败时尝试下一个；保留错误以便界面区分读取失败与无数据。
                    rasterError = $"DEM读取失败：{entry.RelativePath}：{error.Message}";
                }
            }
        }
        return new(DemSampleStatus.NoData, null, databaseId, null,
            QualityFlag: rasterError);
    }

    /// <summary>只供地图的密集行采样。原始双线性权重、NoData、单位及重叠优先级保持；每行检查连接与文件。</summary>
    public DemSample[] SampleDenseRow(Guid databaseId,IReadOnlyList<GeoCoordinate> coordinates,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(coordinates.Count==0)return [];
        foreach(var point in coordinates)point.Validate();
        var database=_catalog.Get(databaseId);var result=new DemSample[coordinates.Count];
        if(!Directory.Exists(database.RootPath))
        {Array.Fill(result,new(DemSampleStatus.DatabaseDisconnected,null,databaseId,null));return result;}
        var candidates=_catalog.FindIntersecting(databaseId,new GeoBoundingBox(
            coordinates.Min(p=>p.Latitude),coordinates.Max(p=>p.Latitude),coordinates.Min(p=>p.Longitude),coordinates.Max(p=>p.Longitude)));
        var available=candidates.ToDictionary(e=>e.FilePath,e=>File.Exists(e.FilePath),StringComparer.OrdinalIgnoreCase);
        lock(_gate)
        {
            if(_disposed)throw new ObjectDisposedException(nameof(DemElevationProvider));
            for(var i=0;i<coordinates.Count;i++)
            {
                token.ThrowIfCancellationRequested();var point=coordinates[i];var covered=false;string? error=null;DemSample? sample=null;
                foreach(var entry in candidates)
                {
                    if(!entry.Wgs84Coverage.Contains(point))continue;
                    covered=true;if(!available[entry.FilePath])continue;
                    var factor=UnitFactor(entry.UnitType,database.UnitMetersConfirmed);
                    if(factor is null){sample=new(DemSampleStatus.InvalidUnit,null,databaseId,entry.FilePath,QualityFlag:"海拔单位未知，请先确认该数据库单位为米。");break;}
                    try
                    {
                        var h=ReadBilinear(GetHandle(entry),entry,point);
                        if(h.HasValue){sample=new(DemSampleStatus.Valid,h.Value*factor.Value,databaseId,entry.FilePath);break;}
                    }
                    catch(Exception e) when(e is not OperationCanceledException){error=$"DEM读取失败：{entry.RelativePath}：{e.Message}";}
                }
                result[i]=sample??new(covered?DemSampleStatus.NoData:DemSampleStatus.NoCoverage,null,databaseId,null,QualityFlag:error);
            }
        }
        return result;
    }

    private RasterHandle GetHandle(RasterIndexEntry entry)
    {
        if(_rasterWindowCacheCapacity>0&&_handles.TryGetValue(entry.FilePath,out var cached)&&
            (cached.IndexedWriteTimeUtc!=entry.LastWriteTimeUtc||cached.IndexedFileSize!=entry.FileSize))
        {
            _handles.Remove(entry.FilePath);_lru.Remove(entry.FilePath);cached.Dispose();
        }
        if (_handles.TryGetValue(entry.FilePath, out var handle))
        {
            _hits++;
            Touch(_lru, entry.FilePath);
            return handle;
        }
        _misses++;
        var dataset = Gdal.Open(entry.FilePath, Access.GA_ReadOnly) ??
            throw new IOException($"无法打开DEM文件：{entry.RelativePath}");
        var band = dataset.GetRasterBand(1);
        var mask = band.GetMaskBand();
        var native = new SpatialReference(entry.NativeCrs);
        native.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        var wgs84 = new SpatialReference("");
        wgs84.SetFromUserInput("EPSG:4326");
        wgs84.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        handle = new RasterHandle
        {
            Dataset = dataset, Band = band, Mask = mask, Native = native, Wgs84 = wgs84,
            Transformation = new CoordinateTransformation(wgs84, native),
            IndexedWriteTimeUtc=entry.LastWriteTimeUtc,IndexedFileSize=entry.FileSize
        };
        _handles.Add(entry.FilePath, handle);
        _lru.AddFirst(entry.FilePath);
        while (_handles.Count > _maximumOpenHandles)
        {
            var last = _lru.Last!.Value;
            _lru.RemoveLast();
            _handles.Remove(last, out var stale);
            stale?.Dispose();
        }
        return handle;
    }

    private double? ReadBilinear(RasterHandle handle, RasterIndexEntry entry,
        GeoCoordinate coordinate)
    {
        var transformed = _transformed;
        Array.Clear(transformed);
        handle.Transformation.TransformPoint(transformed,
            coordinate.Longitude, coordinate.Latitude, 0);
        var gt = entry.GeoTransform;
        var dx = transformed[0] - gt[0];
        var dy = transformed[1] - gt[3];
        var determinant = gt[1] * gt[5] - gt[2] * gt[4];
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-15) return null;
        var x = (dx * gt[5] - dy * gt[2]) / determinant - 0.5;
        var y = (dy * gt[1] - dx * gt[4]) / determinant - 0.5;
        // GDAL 的范围表示像元外边界，而双线性坐标以像元中心为 0。
        // 在边界半个像元内固定到最外侧像元，避免相邻 HGT Tile 的接缝被误报 NoData。
        if (x < -0.5 - 1e-8 || y < -0.5 - 1e-8 ||
            x > entry.PixelWidth - 0.5 + 1e-8 ||
            y > entry.PixelHeight - 0.5 + 1e-8) return null;
        var left = Math.Clamp((int)Math.Floor(x), 0, entry.PixelWidth - 2);
        var top = Math.Clamp((int)Math.Floor(y), 0, entry.PixelHeight - 2);
        var values = _values;
        var mask = _mask;
        // 保留原来新数组的零初始化语义，包括原生读取失败时的缺测判定。
        Array.Clear(values);
        Array.Clear(mask);
        if(_rasterWindowCacheCapacity==0)
        {
            handle.Band.ReadRaster(left, top, 2, 2, values, 2, 2, 0, 0);
            handle.Mask.ReadRaster(left, top, 2, 2, mask, 2, 2, 0, 0);
        }
        else
        {
            // 仅供密集地图采样显式启用：缓存原始窗口，不重采样、不改变权重或缺测判定。
            // 加一像元halo，跨窗口边缘的2×2读取与旧路径完全相同。
            var key=(X:left/128,Y:top/128);
            if(!handle.Windows.TryGetValue(key,out var window))
            {
                var wl=key.X*128;var wt=key.Y*128;
                var ww=Math.Min(129,entry.PixelWidth-wl);var wh=Math.Min(129,entry.PixelHeight-wt);
                var raw=new double[ww*wh];var validity=new byte[ww*wh];
                handle.Band.ReadRaster(wl,wt,ww,wh,raw,ww,wh,0,0);
                handle.Mask.ReadRaster(wl,wt,ww,wh,validity,ww,wh,0,0);
                window=new(wl,wt,ww,wh,raw,validity);handle.Windows.Add(key,window);
                while(handle.Windows.Count>_rasterWindowCacheCapacity)
                {var stale=handle.WindowLru.Last!.Value;handle.WindowLru.RemoveLast();handle.Windows.Remove(stale);}
            }
            Touch(handle.WindowLru, key);
            for(var row=0;row<2;row++)for(var col=0;col<2;col++)
            {var index=(top-window.Top+row)*window.Width+left-window.Left+col;values[row*2+col]=window.Values[index];mask[row*2+col]=window.Mask[index];}
        }
        var fx = Math.Clamp(x - left, 0, 1);
        var fy = Math.Clamp(y - top, 0, 1);
        ReadOnlySpan<double> weights = stackalloc double[] { (1 - fx) * (1 - fy), fx * (1 - fy),
            (1 - fx) * fy, fx * fy };
        var elevation = 0d;
        for (var i = 0; i < values.Length; i++)
        {
            if (weights[i] <= 1e-12) continue;
            if (mask[i] == 0 || !double.IsFinite(values[i]) ||
                entry.NoDataValue.HasValue &&
                Math.Abs(values[i] - entry.NoDataValue.Value) <= 1e-9)
                return null;
            elevation += values[i] * weights[i];
        }
        return elevation;
    }

    // 移动已有节点而不是每次删除再分配；淘汰顺序保持不变。
    private static void Touch<T>(LinkedList<T> lru, T value)
    {
        var node = lru.Find(value);
        if (node is null) lru.AddFirst(value);
        else if (node != lru.First)
        {
            lru.Remove(node);
            lru.AddFirst(node);
        }
    }

    private static double? UnitFactor(string unit, bool confirmedMeters)
    {
        var normalized = unit.Trim().ToLowerInvariant();
        if (normalized is "m" or "metre" or "meter" or "metres" or "meters") return 1;
        if (normalized is "ft" or "foot" or "feet") return 0.3048;
        return confirmedMeters ? 1 : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var handle in _handles.Values) handle.Dispose();
            _handles.Clear();
            _lru.Clear();
        }
    }
}
