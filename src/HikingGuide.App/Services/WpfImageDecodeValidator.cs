using System.IO;
using System.Windows.Media.Imaging;
using HikingGuide.Application.Media;

namespace HikingGuide.App.Services;

public sealed class WpfImageDecodeValidator : IImageDecodeValidator
{
    public bool CanDecode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return false;
            // CopyPixels asks WIC to decode pixels rather than trusting only a header.
            var stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
            var pixels = new byte[checked(stride * frame.PixelHeight)];
            frame.CopyPixels(pixels, stride, 0);
            return true;
        }
        catch { return false; }
    }
}
