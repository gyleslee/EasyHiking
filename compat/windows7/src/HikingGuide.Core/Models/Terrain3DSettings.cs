namespace HikingGuide.Core.Models;

/// <summary>只影响全局地图显示，不进入项目容器。</summary>
public sealed record Terrain3DSettings
{
    public static double RoundExaggeration(double value)=>Math.Round(value,1,MidpointRounding.AwayFromZero);
    public bool Enabled { get; init; }
    public Guid? TerrainDemDatabaseId { get; init; }
    public double Exaggeration { get; init; } = 1.5;
    public double DefaultPitch { get; init; } = 55;
    public bool ShowContours { get; init; } = true;
    public bool ShowHillshade { get; init; } = true;
    public int CacheLimitMb { get; init; } = 4096;
    public bool IsValid => double.IsFinite(Exaggeration) && Exaggeration is >= .5 and <= 3 &&
        double.IsFinite(DefaultPitch) && DefaultPitch is >= 0 and <= 85 && CacheLimitMb is >= 1024 and <= 51200;
}

public sealed record TerrainCameraState(double Latitude,double Longitude,double Zoom,double Bearing,double Pitch)
{
    public bool IsValid=>new[]{Latitude,Longitude,Zoom,Bearing,Pitch}.All(double.IsFinite)&&Latitude is >=-85 and <=85&&Longitude is >=-180 and <=180&&Zoom is >=0 and <=24&&Pitch is >=0 and <=85;
}
