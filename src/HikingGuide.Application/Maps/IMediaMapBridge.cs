namespace HikingGuide.Application.Maps;

public interface IMediaMapBridge
{
    string RegisterMediaThumbnail(byte[] pngBytes);
    void ClearMediaThumbnails();
}
