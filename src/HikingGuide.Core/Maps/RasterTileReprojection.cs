using HikingGuide.Core.Models;

namespace HikingGuide.Core.Maps;

public readonly record struct RasterTileAddress(int Zoom,int X,int Y);
/// <summary>RGBA字节，预乘alpha；只用于显示瓦片。</summary>
public sealed record RasterTileImage(int Width,int Height,byte[] Rgba);

/// <summary>逐像素反向取样，自动跨相邻瓦片拼接。算法不依赖GUI或图像库。</summary>
public sealed class RasterTileReprojection
{
    public const string AlgorithmVersion="raster-crs-1";
    public const int TileSize=256;
    private readonly (double X,double Y)[] _pixels;
    private readonly int _zoom;
    public IReadOnlyCollection<RasterTileAddress> RequiredTiles {get;}
    public RasterTileReprojection(int zoom,int x,int y,MapSourceCoordinateSystem system,CancellationToken token=default)
    {
        if(zoom is <0 or >24||x<0||y<0||x>=(1<<zoom)||y>=(1<<zoom))throw new ArgumentOutOfRangeException(nameof(zoom),"瓦片编号无效。");
        if(!Enum.IsDefined(system))throw new ArgumentOutOfRangeException(nameof(system),"不支持该图源坐标系。");
        _zoom=zoom;_pixels=new (double,double)[TileSize*TileSize];var needed=new HashSet<RasterTileAddress>();
        for(var row=0;row<TileSize;row++)
        {
            token.ThrowIfCancellationRequested();
            for(var col=0;col<TileSize;col++)
            {
                var wgs=MapCoordinateTransform.FromTile(x+(col+.5)/TileSize,y+(row+.5)/TileSize,zoom);
                var coordinate=MapCoordinateTransform.FromWgs84(wgs.Longitude,wgs.Latitude,system);
                var pixel=MapCoordinateTransform.ToTile(coordinate.Longitude,coordinate.Latitude,zoom);
                _pixels[row*TileSize+col]=pixel;
                // 双线性插值可能读取跨瓦片边界的像素，边界两侧都纳入下载计划。
                for(var dy=-1;dy<=1;dy+=2)for(var dx=-1;dx<=1;dx+=2)
                    needed.Add(Address(pixel.X+dx*.5/TileSize,pixel.Y+dy*.5/TileSize));
            }
        }
        if(needed.Count>32)throw new InvalidOperationException("此区域坐标系边界过于复杂，无法生成纠偏瓦片。");
        RequiredTiles=needed.ToArray();
    }
    private RasterTileAddress Address(double x,double y)
    {
        var n=1<<_zoom;var tx=(int)Math.Floor(x);tx=(tx%n+n)%n;
        return new(_zoom,tx,Math.Clamp((int)Math.Floor(y),0,n-1));
    }
    public byte[] Render(IReadOnlyDictionary<RasterTileAddress,RasterTileImage> tiles,CancellationToken token=default)
    {
        foreach(var key in RequiredTiles)
            if(!tiles.TryGetValue(key,out var image)||image.Width is <1 or >2048||image.Height is <1 or >2048||image.Rgba.Length!=image.Width*image.Height*4)
                throw new ArgumentException("纠偏需要的地图瓦片缺失或图片格式无效。",nameof(tiles));
        var result=new byte[TileSize*TileSize*4];
        // 以标准256像素格采样；512等高分辨率瓦片先在相同地理范围内采样。
        void Sample(long px,long py,Span<double> color)
        {
            var extent=(long)(1<<_zoom)*TileSize;
            px=(px%extent+extent)%extent;py=Math.Clamp(py,0,extent-1);
            var key=new RasterTileAddress(_zoom,(int)(px/TileSize),(int)(py/TileSize));var image=tiles[key];
            var ix=(int)((px%TileSize+.5)*image.Width/TileSize);var iy=(int)((py%TileSize+.5)*image.Height/TileSize);
            var offset=(Math.Min(iy,image.Height-1)*image.Width+Math.Min(ix,image.Width-1))*4;
            for(var c=0;c<4;c++)color[c]=image.Rgba[offset+c];
        }
        Span<double> a=stackalloc double[4];Span<double> b=stackalloc double[4];Span<double> c1=stackalloc double[4];Span<double> d=stackalloc double[4];
        for(var i=0;i<_pixels.Length;i++)
        {
            if(i%TileSize==0)token.ThrowIfCancellationRequested();
            var p=_pixels[i];var sx=p.X*TileSize-.5;var sy=p.Y*TileSize-.5;var left=(long)Math.Floor(sx);var top=(long)Math.Floor(sy);
            var fx=sx-left;var fy=sy-top;
            Sample(left,top,a);Sample(left+1,top,b);Sample(left,top+1,c1);Sample(left+1,top+1,d);
            for(var channel=0;channel<4;channel++)result[i*4+channel]=(byte)Math.Clamp(Math.Round((a[channel]*(1-fx)+b[channel]*fx)*(1-fy)+(c1[channel]*(1-fx)+d[channel]*fx)*fy),0,255);
        }
        return result;
    }
}
