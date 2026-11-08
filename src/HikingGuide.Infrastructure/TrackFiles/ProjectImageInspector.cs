using System.Buffers.Binary;

namespace HikingGuide.Infrastructure.TrackFiles;

internal static class ProjectImageInspector
{
    public static bool TryInspect(byte[] bytes, out string mime, out string extension, out int width, out int height)
    {
        mime = extension = "";
        width = height = 0;
        if (bytes.Length >= 33 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
            bytes.AsSpan(bytes.Length - 12, 8).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68 }))
        {
            var w = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (w is > 0 and <= 50000 && h is > 0 and <= 50000)
            { mime = "image/png"; extension = ".png"; width = (int)w; height = (int)h; return true; }
        }
        if (bytes.Length > 8 && bytes[0] == 0xff && bytes[1] == 0xd8 &&
            bytes[^2] == 0xff && bytes[^1] == 0xd9)
        {
            for (var offset = 2; offset + 9 < bytes.Length;)
            {
                if (bytes[offset] != 0xff) break;
                var marker = bytes[offset + 1];
                if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) { offset += 2; continue; }
                if (marker == 0xda) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2));
                if (length < 2 || offset + 2 + length > bytes.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 7, 2));
                    if (width > 0 && height > 0)
                    { mime = "image/jpeg"; extension = ".jpg"; return true; }
                }
                offset += 2 + length;
            }
        }
        if (bytes.Length >= 30 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) + 8 == bytes.Length)
        {
            var tag = bytes.AsSpan(12, 4);
            if (tag.SequenceEqual("VP8X"u8))
            {
                width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
                height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
            }
            else if (tag.SequenceEqual("VP8 "u8) && bytes.Length >= 30)
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2)) & 0x3fff;
                height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)) & 0x3fff;
            }
            else if (tag.SequenceEqual("VP8L"u8) && bytes.Length >= 25)
            {
                width = 1 + (((bytes[22] & 0x3f) << 8) | bytes[21]);
                height = 1 + (((bytes[24] & 0xf) << 10) | (bytes[23] << 2) | ((bytes[22] & 0xc0) >> 6));
            }
            if (width is > 0 and <= 50000 && height is > 0 and <= 50000)
            { mime = "image/webp"; extension = ".webp"; return true; }
        }
        width = height = 0;
        return false;
    }
}
