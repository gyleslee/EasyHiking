namespace HikingGuide.Application.Media;

public interface IImageDecodeValidator
{
    bool CanDecode(byte[] bytes);
}
