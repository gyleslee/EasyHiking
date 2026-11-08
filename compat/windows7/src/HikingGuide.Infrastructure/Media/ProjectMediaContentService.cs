using System.IO.Compression;
using System.Security.Cryptography;
using HikingGuide.Application.Media;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Media;

public sealed record PreparedProjectMedia(MediaBlob? NewBlob, MediaAttachment Attachment);

public sealed class ProjectMediaContentService
{
    public async Task<byte[]> ReadOriginalAsync(MediaBlob blob, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        if (blob.PendingBytes is { } pending) bytes = pending;
        else
        {
            if (blob.ArchiveFilePath is null) throw new IOException("项目图片原图没有可用来源。");
            using var archive = ZipFile.OpenRead(blob.ArchiveFilePath);
            var entry = archive.GetEntry(blob.StoredPath) ?? throw new IOException("项目图片原图缺失。");
            if (entry.Length != blob.ByteLength) throw new IOException("项目图片原图长度不一致。");
            await using var input = entry.Open();
            using var output = new MemoryStream();
            await input.CopyToAsync(output, cancellationToken);
            bytes = output.ToArray();
        }
        if (bytes.LongLength != blob.ByteLength ||
            !Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(bytes)).Equals(blob.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("项目图片原图 SHA-256 校验失败。");
        return bytes;
    }

    public async Task<PreparedProjectMedia> PrepareSaveExternalAsync(HikingProject project,
        ExternalMediaReference reference, ExternalMediaDownloadResult download,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(download.CachePath, cancellationToken);
        if (bytes.LongLength != download.ByteLength ||
            !Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(bytes)).Equals(download.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("缓存图片校验失败，请重新下载。");
        var existing = project.MediaBlobs.FirstOrDefault(blob =>
            blob.Sha256.Equals(download.Sha256, StringComparison.OrdinalIgnoreCase));
        MediaBlob? newBlob = null;
        if (existing is null)
        {
            newBlob = new MediaBlob
            {
                Sha256 = download.Sha256, MimeType = download.MimeType,
                FileExtension = download.Extension, ByteLength = bytes.LongLength,
                Width = download.Width, Height = download.Height, PendingBytes = bytes
            };
            newBlob.StoredPath = $"media/originals/{newBlob.Id:D}{newBlob.FileExtension}";
        }
        var attachment = new MediaAttachment
        {
            OwnerType = reference.OwnerType, OwnerId = reference.OwnerId,
            MediaBlobId = existing?.Id ?? newBlob!.Id, SourceKind = MediaSourceKind.ExternalUrl,
            SourceReference = reference.Url, OriginalFileName = Path.GetFileName(new Uri(reference.Url).AbsolutePath),
            Caption = reference.Caption, DisplayOrder = reference.DisplayOrder
        };
        return new PreparedProjectMedia(newBlob, attachment);
    }
}
