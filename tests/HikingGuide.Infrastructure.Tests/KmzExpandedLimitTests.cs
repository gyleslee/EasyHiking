using System.IO.Compression;
using System.Text;
using HikingGuide.Infrastructure.TrackFiles;
namespace HikingGuide.Infrastructure.Tests;
public class KmzExpandedLimitTests
{
    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public async Task ExpandedLimitIs500MiB(int sizeMiB, bool accepted)
    {
        using var context = new TrackFileTestContext();
        var file = context.OutputPath("limit.kmz");
        var document = Encoding.UTF8.GetBytes("<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><Placemark><LineString><coordinates>113,23,10 113.01,23.01,20</coordinates></LineString></Placemark></Document></kml>");
        using (var archive = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var stream = archive.CreateEntry("doc.kml").Open()) stream.Write(document);
            var remaining = sizeMiB * 1024L * 1024 - document.Length;
            var buffer = new byte[64 * 1024];
            for (var index = 0; remaining > 0; index++)
            {
                using var stream = archive.CreateEntry($"unused/{index}.bin", CompressionLevel.Fastest).Open();
                var entryBytes = Math.Min(50L * 1024 * 1024, remaining);
                remaining -= entryBytes;
                while (entryBytes > 0)
                {
                    var count = (int)Math.Min(buffer.Length, entryBytes);
                    stream.Write(buffer, 0, count);
                    entryBytes -= count;
                }
            }
        }
        if (accepted) Assert.Single((await new KmzTrackImporter().ImportAsync(file)).Tracks);
        else Assert.Contains("500 MB", (await Assert.ThrowsAsync<InvalidDataException>(() => new KmzTrackImporter().ImportAsync(file))).Message);
    }
}
