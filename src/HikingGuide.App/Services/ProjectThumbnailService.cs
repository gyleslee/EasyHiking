using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Media;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.App.Services;

public sealed class ThumbnailProfile
{
    public int MaximumSidePixels { get; init; } = 320;
}

/// <summary>只在用户请求时解码一张图，缓存中仅保存派生 PNG，原图字节保持不变。</summary>
public sealed class ProjectThumbnailService(ApplicationDataPaths paths, ThumbnailProfile? profile = null)
{
    private readonly ThumbnailProfile _profile = profile ?? new ThumbnailProfile();
    private readonly ProjectMediaContentService _content = new();

    public async Task<byte[]> GetInternalThumbnailAsync(HikingProject project, MediaBlob blob,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(paths.RootDirectory, "MediaCache", project.Id.ToString("D"));
        var path = Path.Combine(directory, blob.Id.ToString("D") + ".png");
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, cancellationToken);
        var original = await Task.Run(() => _content.ReadOriginalAsync(blob, cancellationToken), cancellationToken);
        var bytes = await Task.Run(() => Generate(original), cancellationToken);
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            if (!File.Exists(path)) File.Move(temp, path);
            return bytes;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task<byte[]> GetExternalThumbnailAsync(string cachePath, CancellationToken cancellationToken = default) =>
        Task.Run(async () => Generate(await File.ReadAllBytesAsync(cachePath, cancellationToken)), cancellationToken);

    private byte[] Generate(byte[] original)
    {
        using var input = new MemoryStream(original, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource image = ImageOrientationSupport.Apply(decoder.Frames[0]);
        var ratio = Math.Min(1d, (double)_profile.MaximumSidePixels / Math.Max(image.PixelWidth, image.PixelHeight));
        if (ratio < 1) image = new TransformedBitmap(image, new ScaleTransform(ratio, ratio));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

}
