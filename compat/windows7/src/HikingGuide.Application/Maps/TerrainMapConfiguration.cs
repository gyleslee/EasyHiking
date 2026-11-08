namespace HikingGuide.Application.Maps;

public sealed record TerrainMapConfiguration(bool Enabled=false,Guid? DemDatabaseId=null,string? TerrainUrlTemplate=null,
    int MaxNativeZoom=0,double Exaggeration=1.5,double DefaultPitch=55,long TerrainRevision=0,string? Error=null,
    HikingGuide.Core.Models.TerrainCameraState? Camera=null);
