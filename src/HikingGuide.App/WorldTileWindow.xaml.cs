using System.IO;
using System.Text.Json;
using System.Windows;
using HikingGuide.App.Services;
using HikingGuide.Application.Routing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.Routing;
using Microsoft.Web.WebView2.Core;

namespace HikingGuide.App;

public partial class WorldTileWindow : Window
{
    private readonly BRouterController _controller;
    private readonly WorldTileSelection _selection;
    private bool _ready;
    private bool _boxMode;
    private CancellationTokenSource? _downloadCancellation;
    private readonly TaskCompletionSource<(bool Ready, bool Osm, bool Grid)> _smokeResult =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorldTileWindow(BRouterController controller, IReadOnlySet<BRouterTile> available)
    {
        InitializeComponent();
        _controller = controller;
        _selection = new WorldTileSelection(available);
        _controller.ProgressChanged += OnDownloadProgress;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _controller.ProgressChanged -= OnDownloadProgress;
            _downloadCancellation?.Cancel();
        };
        RefreshSummary();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userData = Path.Combine(new ApplicationDataPaths().RootDirectory, "WebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await WorldWebView.EnsureCoreWebView2Async(environment);
            WorldWebView.CoreWebView2.SetVirtualHostNameToFolderMapping("hikingguide-world.local",
                Path.Combine(AppContext.BaseDirectory, "Map", "Web"),
                CoreWebView2HostResourceAccessKind.Allow);
            WorldWebView.CoreWebView2.WebMessageReceived += OnWebMessage;
            WorldWebView.CoreWebView2.NavigationCompleted += (_, navigation) =>
            {
                if (!navigation.IsSuccess)
                    ((App)System.Windows.Application.Current).LoggingService.Warning(
                        $"世界路由数据地图页面加载失败：{navigation.WebErrorStatus}");
            };
            WorldWebView.Source = new Uri("https://hikingguide-world.local/world-tiles.html");
        }
        catch (Exception exception)
        {
            ((App)System.Windows.Application.Current).LoggingService.Error(exception, "世界路由数据地图初始化失败");
            SummaryText.Text = $"世界路由数据地图加载失败：{exception.Message}";
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var value = document.RootElement;
            switch (value.GetProperty("type").GetString())
            {
                case "Error":
                    var message = value.GetProperty("message").GetString() ?? "未知错误";
                    ((App)System.Windows.Application.Current).LoggingService.Warning($"世界路由数据地图：{message}");
                    SummaryText.Text = $"世界路由数据地图加载失败：{message}";
                    break;
                case "Ready":
                    _ready = true;
                    SendData();
                    break;
                case "Toggle":
                    var tile = BRouterTile.ParseFileName(value.GetProperty("name").GetString()! + ".rd5");
                    _selection.Toggle(new GeoCoordinate(tile.SouthLatitude + 2.5,
                        tile.WestLongitude + 2.5));
                    SendSelection();
                    break;
                case "Box":
                    _selection.SelectIntersecting(new GeoBoundingBox(
                        Math.Clamp(value.GetProperty("minLat").GetDouble(), -90, 90),
                        Math.Clamp(value.GetProperty("maxLat").GetDouble(), -90, 90),
                        Math.Clamp(value.GetProperty("minLon").GetDouble(), -180, 180),
                        Math.Clamp(value.GetProperty("maxLon").GetDouble(), -180, 180)),
                        value.GetProperty("remove").GetBoolean());
                    SendSelection();
                    break;
                case "Smoke":
                    _smokeResult.TrySetResult((value.GetProperty("ready").GetBoolean(),
                        value.GetProperty("osm").GetBoolean(),
                        value.GetProperty("grid").GetBoolean()));
                    break;
            }
        }
        catch (Exception) { SummaryText.Text = "地图选择消息无效，请重试。"; }
    }

    private void SendData()
    {
        if (!_ready) return;
        var installed = _controller.Data.CurrentManifest?.Tiles.Select(tile =>
            Path.GetFileNameWithoutExtension(tile.FileName)).ToArray() ?? [];
        Send(new
        {
            type = "Data",
            available = _selection.Available.Select(tile => new
            {
                name = Path.GetFileNameWithoutExtension(tile.FileName),
                lat = tile.SouthLatitude, lon = tile.WestLongitude
            }).ToArray(),
            installed,
            selected = _selection.Selected.Select(tile =>
                Path.GetFileNameWithoutExtension(tile.FileName)).ToArray()
        });
    }

    private void SendSelection()
    {
        Send(new { type = "Selection", selected = _selection.Selected.Select(tile =>
            Path.GetFileNameWithoutExtension(tile.FileName)).ToArray() });
        RefreshSummary();
    }

    private void OnDownloadProgress(object? sender, RoutingDownloadProgress progress)
    {
        if (_downloadCancellation is null || progress.CurrentTile is null ||
            progress.Total != _selection.Selected.Count) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_downloadCancellation is null) return;
            var name = Path.GetFileNameWithoutExtension(progress.CurrentTile);
            Send(new { type = "DownloadState", state = "downloaded", names = new[] { name } });
            SummaryText.Text = $"正在下载：{progress.Completed}/{progress.Total}；{progress.DownloadedBytes / 1024d / 1024d:F1} MB。";
        });
    }

    private void Send(object payload) =>
        WorldWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));

    private void RefreshSummary() =>
        SummaryText.Text = $"官方可选：{_selection.Available.Count} 个；已选择：{_selection.Selected.Count} 个；预计大小：未知。";

    private void OnToggleBox(object sender, RoutedEventArgs e)
    {
        _boxMode = !_boxMode;
        BoxButton.Content = _boxMode ? "结束框选" : "框选瓦片";
        Send(new { type = "BoxMode", enabled = _boxMode });
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _selection.Clear();
        SendSelection();
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_selection.Selected.Count == 0)
        {
            MessageBox.Show(this, "请先在地图上选择至少一个BRouter路由瓦片。",
                "未选择路由数据", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_downloadCancellation is not null) return;
        _downloadCancellation = new CancellationTokenSource();
        Send(new { type = "DownloadState", state = "downloading",
            names = _selection.Selected.Select(tile => Path.GetFileNameWithoutExtension(tile.FileName)).ToArray() });
        DownloadButton.IsEnabled = false;
        try
        {
            SummaryText.Text = $"正在后台下载 {_selection.Selected.Count} 个瓦片……";
            await _controller.UpdateManualAsync(_selection.Selected.ToArray(),
                _downloadCancellation.Token);
            SendData();
            RefreshSummary();
        }
        catch (OperationCanceledException)
        {
            MarkSelectionFailed();
            SummaryText.Text = "下载已取消，旧数据未删除。";
        }
        catch (Exception exception)
        {
            MarkSelectionFailed();
            SummaryText.Text = "下载失败，旧数据未删除。";
            MessageBox.Show(this, exception.Message, "下载世界路由数据失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            DownloadButton.IsEnabled = true;
        }
    }

    private void MarkSelectionFailed() => Send(new
    {
        type = "DownloadState", state = "failed",
        names = _selection.Selected.Select(tile => Path.GetFileNameWithoutExtension(tile.FileName))
            .ToArray()
    });

    private void OnCancelDownload(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();

    internal async Task<(bool Ready, bool Osm, bool Grid, int Selected)> RunMapSmokeAsync()
    {
        for (var attempt = 0; attempt < 100 && !_ready; attempt++)
            await Task.Delay(100);
        if (!_ready) return (false, false, false, 0);
        await WorldWebView.ExecuteScriptAsync("window.p7WorldMapSmoke()");
        var completed = await Task.WhenAny(_smokeResult.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (completed != _smokeResult.Task) return (false, false, false, _selection.Selected.Count);
        var result = await _smokeResult.Task;
        for (var attempt = 0; attempt < 50 && _selection.Selected.Count < 2; attempt++)
            await Task.Delay(100);
        return (result.Ready, result.Osm, result.Grid,
            _selection.Selected.Count);
    }
}
