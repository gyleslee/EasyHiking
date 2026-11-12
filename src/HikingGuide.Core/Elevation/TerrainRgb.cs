namespace HikingGuide.Core.Elevation;

/// <summary>512像素Terrain-RGB显示网格；与攻略统计完全分离。</summary>
public static class TerrainRgb
{
    public const int TileSize = 512;
    public const int SurfaceBorder=2;
    public const int SurfaceTileSize=TileSize+2*SurfaceBorder;
    public const string EncodingVersion = "mapbox-rgb-512-v1";
    public static (byte R,byte G,byte B) Encode(double meters)
    {
        if(!double.IsFinite(meters)||meters < -10000 || meters > 1667721.5)throw new ArgumentOutOfRangeException(nameof(meters),"地形高程超出Terrain-RGB编码范围。");
        var encoded=(int)Math.Round((meters+10000)*10,MidpointRounding.AwayFromZero);
        return ((byte)(encoded>>16),(byte)(encoded>>8),(byte)encoded);
    }
    public static double Decode(byte r,byte g,byte b)=>((r<<16)|(g<<8)|b)*.1-10000;
    public static (double Latitude,double Longitude) Coordinate(int z,int x,int y,double px,double py)
    {
        var n=Math.Pow(2,z);
        return (Math.Atan(Math.Sinh(Math.PI*(1-2*(y+py/TileSize)/n)))*180/Math.PI,(x+px/TileSize)/n*360-180);
    }
    // 保守使用数据库最粗分辨率和最大绝对纬度，不把过采样当作新精度。
    public static int MaxNativeZoom(double resolutionMeters,double maximumAbsoluteLatitude)
    {
        if(!double.IsFinite(resolutionMeters)||resolutionMeters<=0)throw new ArgumentOutOfRangeException(nameof(resolutionMeters));
        var size=40075016.68557849*Math.Cos(Math.Clamp(Math.Abs(maximumAbsoluteLatitude),0,85)*Math.PI/180);
        return Math.Clamp((int)Math.Floor(Math.Log2(size/(TileSize*resolutionMeters))),0,18);
    }
}
