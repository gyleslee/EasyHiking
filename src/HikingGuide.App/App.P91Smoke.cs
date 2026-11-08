using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Favorites;
using HikingGuide.Infrastructure.TrackFiles;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunP91SmokeAsync(MainWindow window, MainViewModel main, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var checks = new Dictionary<string, object>(); var kmzReports = new List<object>(); string? error = null;
        try
        {
            if (!Favorites.Paths.FavoritesRoot.StartsWith("F:\\Codex\\Temp\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("P9-1验收必须指定隔离的功能数据目录。");
            var now = DateTimeOffset.UtcNow; var photo = await Favorites.SaveCollectionAsync(new(Guid.NewGuid(), "摄影点", 1, now, now)); var camp = await Favorites.SaveCollectionAsync(new(Guid.NewGuid(), "营地", 2, now, now));
            using var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/AppLogo.png"))!.Stream; using var memory = new MemoryStream(); await resource.CopyToAsync(memory);
            var image = await Task.Run(() => FavoriteImageService.FromBytes(memory.ToArray(), "山脊.png"));
            var frame = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 140, 0, 255, 0, 140, 0, 255, 0, 140, 0, 255, 0, 140, 0, 255 }, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); using var small = new MemoryStream(); encoder.Save(small);
            var second = await Task.Run(() => FavoriteImageService.FromBytes(small.ToArray(), "营地.png"));
            var names = new[] { "A点", "B点", "C点", new string('十', 10), new string('三', 30), new string('百', 100), "另一长名" + new string('长', 96), new string('超', 500), "阿尔山", "白云山" };
            var points = names.Select((name, i) => new FavoritePoint { Name = name, Description = "中文说明 & < > \"\n下一行", CollectionId = i < 5 ? photo.Id : camp.Id, Latitude = 23.13 + i / 1000d, Longitude = 113.3, ElevationMeters = i == 0 ? 123.45 : null, SourceReference = i == 0 ? "https://example.com/guide" : null }).ToArray();
            for (var i = 0; i < points.Length; i++) await Favorites.SaveAsync(points[i], i == 0 ? [image, second] : i == 1 ? [image] : i == 2 ? [second] : []);
            var detail = (await Favorites.GetAsync(points[0].Id))!; await Favorites.SaveAsync(points[0], retained: detail.Attachments.Concat([new FavoriteMediaAttachment(Guid.NewGuid(), points[0].Id, null, "网络图片", null, 2, now, "https://example.com/photo.jpg", true)]).ToArray());
            window.Width = 1000; window.ShowFavoritesForAutomation(); await window.FavoritesContent.RefreshAsync(); window.UpdateLayout(); await Task.Delay(100);
            foreach (var pair in await window.FavoritesContent.RunP91PanelSmokeAsync()) checks[pair.Key] = pair.Value;
            CaptureWindowPng(window, Path.ChangeExtension(output, ".panel.png"));
            var ranges = new[] { ("all", (IReadOnlyList<Guid>?)null, (Guid?)null, 10), ("collection", (IReadOnlyList<Guid>?)null, (Guid?)photo.Id, 5), ("multi", (IReadOnlyList<Guid>?)new[] { points[0].Id, points[1].Id, points[6].Id }, (Guid?)null, 3), ("single", (IReadOnlyList<Guid>?)new[] { points[0].Id }, (Guid?)null, 1) };
            foreach (var (name, ids, collection, expected) in ranges)
            {
                var file = Path.Combine(Path.GetDirectoryName(output)!, name + ".kmz"); var result = await Favorites.ExportKmzAsync(file, ids, collection);
                var imported = await new KmzTrackImporter(imageValidator: new WpfImageDecodeValidator()).ImportAsync(file);
                var first = imported.Waypoints.FirstOrDefault(p => p.Name == points[0].Name);
                checks["KmzRoundTrip:" + name] = imported.IsSuccess && imported.Waypoints.Count == expected && first is not null && first.Description == points[0].Description && first.SourceElevation == points[0].ElevationMeters && imported.MediaBlobs.Count == 2 && imported.MediaAttachments.Any(a => a.OwnerId == first.Id);
                kmzReports.Add(new { Scope = name, result.PointCount, result.CollectionCount, result.ImageCount, result.FileBytes, ImportedAttachments = imported.MediaAttachments.Count, ImportedUrls = imported.ExternalMediaReferences.Count });
            }
            foreach (var pair in main.RunP91SelectionSmoke()) checks[pair.Key] = pair.Value;
            checks["TreeMouseTenAlternationsAndStatusBinding"] = await window.RunP91TreeMouseSmokeAsync(main);
            await Task.Delay(50); CaptureWindowPng(window, Path.ChangeExtension(output, ".selection.png"));
        }
        catch (Exception exception) { error = exception.ToString(); }
        var passed = error is null && checks.Values.OfType<bool>().All(value => value) && checks.Values.OfType<bool>().Count() >= 20;
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Passed = passed, Error = error, Checks = checks, Kmz = kmzReports }, new JsonSerializerOptions { WriteIndented = true })); return passed;
    }
}
