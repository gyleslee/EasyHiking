using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HikingGuide.App.Services;

internal static class ImageOrientationSupport
{
    public static BitmapSource Apply(BitmapFrame frame)
    {
        var orientation = ReadOrientation(frame.Metadata as BitmapMetadata);
        BitmapSource source = frame;
        if (orientation is 2 or 5 or 7)
            source = new TransformedBitmap(source, new ScaleTransform(-1, 1));
        else if (orientation == 4)
            source = new TransformedBitmap(source, new ScaleTransform(1, -1));
        var rotation = orientation switch
        {
            3 => 180,
            5 or 8 => 270,
            6 or 7 => 90,
            _ => 0
        };
        if (rotation != 0) source = new TransformedBitmap(source, new RotateTransform(rotation));
        source.Freeze();
        return source;
    }

    private static int ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null) return 1;
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (metadata.ContainsQuery(query)) return Convert.ToInt32(metadata.GetQuery(query));
            }
            catch { /* 非标准EXIF按原方向显示，不修改原图。 */ }
        }
        return 1;
    }
}
