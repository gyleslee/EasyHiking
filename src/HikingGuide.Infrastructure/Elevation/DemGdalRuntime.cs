using MaxRev.Gdal.Core;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace HikingGuide.Infrastructure.Elevation;

/// <summary>使用 NuGet 随软件发布的固定 GDAL/PROJ，不搜索 QGIS 或系统 PATH。</summary>
public static class DemGdalRuntime
{
    private static readonly object Gate = new();
    private static bool _configured;

    public static (string GdalVersion, string ProjVersion) EnsureReady()
    {
        lock (Gate)
        {
            if (!_configured)
            {
                GdalBase.ConfigureAll();
                _configured = true;
            }
            if (Gdal.GetDriverByName("GTiff") is null ||
                Gdal.GetDriverByName("SRTMHGT") is null)
                throw new InvalidOperationException("DEM模块缺少GTiff或SRTMHGT驱动，无法读取高程数据。");
            return (Gdal.VersionInfo("RELEASE_NAME"),
                $"{Osr.GetPROJVersionMajor()}.{Osr.GetPROJVersionMinor()}.{Osr.GetPROJVersionMicro()}");
        }
    }
}
