using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HikingGuide.Core.Favorites;
using HikingGuide.Infrastructure.Favorites;

namespace HikingGuide.App.Services;

public static class FavoriteImageService
{
    public static Task<FavoriteMediaInput> LoadAsync(string path) => Task.Run(() =>
        FromBytes(File.ReadAllBytes(path), Path.GetFileName(path)));
    public static FavoriteMediaInput FromBytes(byte[] bytes, string name)
    {
        if (bytes.Length == 0 || bytes.Length > 50 * 1024 * 1024) throw new InvalidDataException("图片为空或超过50MB。");
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 40000000) throw new InvalidDataException("图片尺寸无效或过大。");
        var stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8); frame.CopyPixels(new byte[checked(stride * frame.PixelHeight)], stride, 0);
        var extension = decoder switch { PngBitmapDecoder => ".png", JpegBitmapDecoder => ".jpg", GifBitmapDecoder => ".gif", BmpBitmapDecoder => ".bmp", _ => throw new InvalidDataException("暂不支持此图片解码格式。") };
        var mime = extension switch { ".jpg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", _ => "image/png" };
        return new(bytes, name, mime, extension, frame.PixelWidth, frame.PixelHeight);
    }
    public static async Task<BitmapSource> ThumbnailAsync(FavoritesRepository repository, FavoriteMediaBlob blob)
    {
        var path = Path.Combine(repository.Paths.ThumbnailPath, blob.Sha256 + ".png");
        var bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path) : await repository.ReadOriginalAsync(blob);
        return await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes, false); var image = new BitmapImage(); image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 240; image.StreamSource = stream; image.EndInit(); image.Freeze();
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(repository.Paths.ThumbnailPath); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                try { using (var output = File.Create(temporary)) encoder.Save(output); File.Move(temporary, path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return (BitmapSource)image;
        });
    }
    public static BitmapSource Decode(byte[] bytes)
    { using var stream = new MemoryStream(bytes, false); var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); bitmap.Freeze(); return bitmap; }
}
