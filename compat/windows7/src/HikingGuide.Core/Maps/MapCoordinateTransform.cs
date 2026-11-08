using HikingGuide.Core.Models;

namespace HikingGuide.Core.Maps;

/// <summary>显示用正向转换：从WGS84取样目标图源。轨迹、DEM和业务坐标始终保持WGS84。
/// GCJ/BD公式参考MIT项目wandergis/coordtransform，版权见ThirdPartyNotices。</summary>
public static class MapCoordinateTransform
{
    private const double Axis = 6378245;
    private const double EccentricitySquared = .00669342162296594323;
    private const double BdPi = Math.PI * 3000 / 180;
    public const double MercatorLatitudeLimit = 85.0511287798066;
    public static (double Longitude,double Latitude) FromWgs84(double longitude,double latitude,MapSourceCoordinateSystem system)
    {
        if(!double.IsFinite(longitude)||longitude is < -180 or > 180||!double.IsFinite(latitude)||latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(longitude),"地图坐标超出有效经纬度范围。");
        if(!Enum.IsDefined(system))throw new ArgumentOutOfRangeException(nameof(system),"不支持该图源坐标系。");
        if(system==MapSourceCoordinateSystem.Wgs84)return (longitude,latitude);
        var lng=longitude;var lat=latitude;
        // 与参考实现一致的适用范围；域外GCJ不偏移。
        if(longitude>73.66&&longitude<135.05&&latitude>3.86&&latitude<53.55)
        {
            var x=longitude-105;var y=latitude-35;
            var a=-100+2*x+3*y+.2*y*y+.1*x*y+.2*Math.Sqrt(Math.Abs(x));
            var b=300+x+2*y+.1*x*x+.1*x*y+.1*Math.Sqrt(Math.Abs(x));
            var common=(20*Math.Sin(6*x*Math.PI)+20*Math.Sin(2*x*Math.PI))*2/3;
            a+=common+(20*Math.Sin(y*Math.PI)+40*Math.Sin(y/3*Math.PI))*2/3+(160*Math.Sin(y/12*Math.PI)+320*Math.Sin(y*Math.PI/30))*2/3;
            b+=common+(20*Math.Sin(x*Math.PI)+40*Math.Sin(x/3*Math.PI))*2/3+(150*Math.Sin(x/12*Math.PI)+300*Math.Sin(x/30*Math.PI))*2/3;
            var rad=latitude*Math.PI/180;var magic=1-EccentricitySquared*Math.Pow(Math.Sin(rad),2);var root=Math.Sqrt(magic);
            lat+=a*180/(Axis*(1-EccentricitySquared)/(magic*root)*Math.PI);
            lng+=b*180/(Axis/root*Math.Cos(rad)*Math.PI);
        }
        if(system==MapSourceCoordinateSystem.Gcj02)return (lng,lat);
        var radius=Math.Sqrt(lng*lng+lat*lat)+.00002*Math.Sin(lat*BdPi);
        var angle=Math.Atan2(lat,lng)+.000003*Math.Cos(lng*BdPi);
        return (radius*Math.Cos(angle)+.0065,radius*Math.Sin(angle)+.006);
    }
    public static (double X,double Y) ToTile(double longitude,double latitude,int zoom)
    {
        var n=(double)(1<<zoom);var rad=Math.Clamp(latitude,-MercatorLatitudeLimit,MercatorLatitudeLimit)*Math.PI/180;
        return ((longitude+180)/360*n,(1-Math.Asinh(Math.Tan(rad))/Math.PI)/2*n);
    }
    public static (double Longitude,double Latitude) FromTile(double x,double y,int zoom)
    {
        var n=(double)(1<<zoom);
        return (x/n*360-180,Math.Atan(Math.Sinh(Math.PI*(1-2*y/n)))*180/Math.PI);
    }
}
