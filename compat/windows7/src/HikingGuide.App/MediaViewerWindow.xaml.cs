using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HikingGuide.Application.Media;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Media;
using HikingGuide.App.Services;

namespace HikingGuide.App;

public partial class MediaViewerWindow : Window
{
    private readonly HikingProject _project;
    private readonly List<MediaItem> _items;
    private readonly IExternalMediaDownloadService _downloader;
    private readonly Func<ExternalMediaReference, ExternalMediaDownloadResult, Task> _save;
    private readonly bool _allowSaveToProject;
    private readonly string _automaticSaveMessage;
    private readonly ProjectMediaContentService _media = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translation = new();
    private CancellationTokenSource? _operation;
    private ExternalMediaDownloadResult? _downloaded;
    private int _index;
    private Point? _dragStart;

    public MediaViewerWindow(HikingProject project, Guid markerId, IExternalMediaDownloadService downloader,
        Func<ExternalMediaReference, ExternalMediaDownloadResult, Task> save, bool allowSaveToProject = true,
        string automaticSaveMessage = "已自动加入项目，请保存项目文件。")
    {
        InitializeComponent();
        _project = project;
        _downloader = downloader;
        _save = save;
        _allowSaveToProject = allowSaveToProject;
        _automaticSaveMessage = automaticSaveMessage;
        _items = project.MediaAttachments.Where(item => item.OwnerId == markerId)
            .Select(item => new MediaItem(item.DisplayOrder, item, null))
            .Concat(project.ExternalMediaReferences.Where(item => item.OwnerId == markerId &&
                !project.MediaAttachments.Any(attachment => attachment.OwnerId == item.OwnerId &&
                    attachment.SourceKind == MediaSourceKind.ExternalUrl && attachment.SourceReference == item.Url))
                .Select(item => new MediaItem(item.DisplayOrder, null, item)))
            .OrderBy(item => item.Order).ThenBy(item => item.Attachment is null ? 1 : 0).ToList();
        var transforms = new TransformGroup();
        transforms.Children.Add(_scale);
        transforms.Children.Add(_translation);
        Photo.RenderTransform = transforms;
        Loaded += async (_, _) => await ShowCurrentAsync();
        Closed += (_, _) => { _operation?.Cancel(); _operation?.Dispose(); };
    }

    private MediaItem Current => _items[_index];
    private async Task ShowCurrentAsync()
    {
        if (_items.Count == 0) { StatusLabel.Text = "该标注没有图片。"; return; }
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        var token = _operation.Token;
        var markerName = _project.Waypoints.Cast<MapMarkerBase>()
            .Concat(_project.GuideNodes).FirstOrDefault(item => item.Id == (Current.Attachment?.OwnerId ?? Current.Reference!.OwnerId))?.Name ?? "标注";
        Title = $"{markerName}｜{_index + 1} / {_items.Count}";
        Photo.Source = null;
        _downloaded = null;
        DownloadButton.Visibility = RefreshButton.Visibility = SaveButton.Visibility = Visibility.Collapsed;
        try
        {
            if (Current.Attachment is { } attachment)
            {
                StatusLabel.Text = "正在读取项目图片……";
                var blob = _project.MediaBlobs.First(item => item.Id == attachment.MediaBlobId);
                var bytes = await Task.Run(() => _media.ReadOriginalAsync(blob, token), token);
                if (token.IsCancellationRequested) return;
                await DisplayAsync(bytes, token);
                StatusLabel.Text = $"项目图片 · {blob.Width} × {blob.Height} · {blob.ByteLength / 1024d / 1024d:F1} MB";
            }
            else if (Current.Reference is { } reference)
            {
                var host = new Uri(reference.Url).Host;
                StatusLabel.Text = $"网络图片：{host} · 尚未下载。点击“下载并查看”后才会联网。";
                DownloadButton.Visibility = Visibility.Visible;
                var cached = await Task.Run(() => _downloader.TryGetCachedAsync(reference.Url, token), token);
                if (token.IsCancellationRequested) return;
                if (cached is not null) await DisplayExternalAsync(cached, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { StatusLabel.Text = $"图片打开失败：{exception.Message}"; }
    }

    private async Task DisplayExternalAsync(ExternalMediaDownloadResult result, CancellationToken token)
    {
        var reference = Current.Reference ?? throw new InvalidOperationException("当前图片不是网络图片。");
        var bytes = await File.ReadAllBytesAsync(result.CachePath, token);
        if (token.IsCancellationRequested) return;
        await DisplayAsync(bytes, token);
        if (token.IsCancellationRequested || Current.Reference?.Id != reference.Id) return;
        _downloaded = result;
        DownloadButton.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Visible;
        SaveButton.Visibility = Visibility.Collapsed;
        StatusLabel.Text = $"网络图片：{new Uri(reference.Url).Host} · {result.Width} × {result.Height}" +
                           (result.FromCache ? " · 已使用缓存" : " · 下载完成");
        await SaveDownloadedImageAsync(reference, result, token);
    }

    private async Task SaveDownloadedImageAsync(ExternalMediaReference reference, ExternalMediaDownloadResult result,
        CancellationToken token)
    {
        if (!_allowSaveToProject || token.IsCancellationRequested) return;
        if (_project.MediaAttachments.Any(item => item.OwnerId == reference.OwnerId &&
            item.SourceKind == MediaSourceKind.ExternalUrl && item.SourceReference == reference.Url))
        {
            StatusLabel.Text += " · 已在项目中";
            return;
        }
        StatusLabel.Text = "图片已显示，正在自动加入项目……";
        try
        {
            await _save(reference, result);
            if (token.IsCancellationRequested || Current.Reference?.Id != reference.Id) return;
            SaveButton.Visibility = Visibility.Collapsed;
            StatusLabel.Text = _automaticSaveMessage;
        }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested || Current.Reference?.Id != reference.Id) return;
            SaveButton.Content = "重试加入项目";
            SaveButton.IsEnabled = true;
            SaveButton.Visibility = Visibility.Visible;
            StatusLabel.Text = $"图片已显示，但自动加入项目失败：{exception.Message}";
        }
    }

    private async Task DisplayAsync(byte[] bytes, CancellationToken token)
    {
        var bitmap = await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return ImageOrientationSupport.Apply(decoder.Frames[0]);
        }, token);
        if (token.IsCancellationRequested) return;
        Photo.Source = bitmap;
        Photo.Width = bitmap.PixelWidth;
        Photo.Height = bitmap.PixelHeight;
        Fit();
    }

    private async Task DownloadAsync(bool force)
    {
        if (Current.Reference is not { } reference) return;
        _operation?.Cancel(); _operation?.Dispose();
        _operation = new CancellationTokenSource();
        var token = _operation.Token;
        DownloadButton.IsEnabled = RefreshButton.IsEnabled = false;
        CancelDownloadButton.Visibility = Visibility.Visible;
        StatusLabel.Text = "正在下载图片……";
        var progress = new Progress<(long Received, long? Total)>(value =>
            StatusLabel.Text = value.Total is long total
                ? $"正在下载图片……{value.Received / 1024d / 1024d:F1} MB / {total / 1024d / 1024d:F1} MB"
                : $"正在下载图片……{value.Received / 1024d / 1024d:F1} MB");
        try
        {
            var result = await Task.Run(() => _downloader.DownloadAsync(reference.Url, force, progress, token), token);
            if (!token.IsCancellationRequested) await DisplayExternalAsync(result, token);
        }
        catch (OperationCanceledException) { StatusLabel.Text = "图片下载已取消。"; }
        catch (Exception exception) { StatusLabel.Text = $"图片下载失败：{exception.Message}"; }
        finally { DownloadButton.IsEnabled = RefreshButton.IsEnabled = true; CancelDownloadButton.Visibility = Visibility.Collapsed; }
    }

    private async void PreviousClicked(object sender, RoutedEventArgs e) { _index = (_index - 1 + _items.Count) % _items.Count; await ShowCurrentAsync(); }
    private async void NextClicked(object sender, RoutedEventArgs e) { _index = (_index + 1) % _items.Count; await ShowCurrentAsync(); }
    private void ZoomInClicked(object sender, RoutedEventArgs e) => SetScale(_scale.ScaleX * 1.25);
    private void ZoomOutClicked(object sender, RoutedEventArgs e) => SetScale(_scale.ScaleX / 1.25);
    private void FitClicked(object sender, RoutedEventArgs e) => Fit();
    private void ActualClicked(object sender, RoutedEventArgs e) => SetScale(1);
    private async void DownloadClicked(object sender, RoutedEventArgs e) => await DownloadAsync(false);
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await DownloadAsync(true);
    private void CancelDownloadClicked(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (Current.Reference is null || _downloaded is null) return;
        await SaveDownloadedImageAsync(Current.Reference, _downloaded,
            _operation?.Token ?? CancellationToken.None);
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private void ViewportMouseWheel(object sender, MouseWheelEventArgs e) => SetScale(_scale.ScaleX * (e.Delta > 0 ? 1.2 : 1 / 1.2));
    private void ViewportMouseDown(object sender, MouseButtonEventArgs e)
    { _dragStart = e.GetPosition(Viewport); Viewport.CaptureMouse(); }
    private void ViewportMouseUp(object sender, MouseButtonEventArgs e)
    { _dragStart = null; Viewport.ReleaseMouseCapture(); }
    private void ViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point previous) return;
        var current = e.GetPosition(Viewport);
        _translation.X += current.X - previous.X;
        _translation.Y += current.Y - previous.Y;
        _dragStart = current;
    }
    private void ViewportSizeChanged(object sender, SizeChangedEventArgs e) { if (Photo.Source is not null) Fit(); }
    private void SetScale(double value) { _scale.ScaleX = _scale.ScaleY = Math.Clamp(value, 0.05, 16); }
    private void Fit()
    {
        if (Photo.Source is not BitmapSource bitmap || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        var value = Math.Min(Viewport.ActualWidth / bitmap.PixelWidth, Viewport.ActualHeight / bitmap.PixelHeight);
        SetScale(Math.Min(1, value));
        _translation.X = _translation.Y = 0;
    }
    private sealed record MediaItem(int Order, MediaAttachment? Attachment, ExternalMediaReference? Reference);

    public async Task<(bool InternalOpened, bool ExternalWaitedForClick, bool ExternalOpened)> RunAutomationAsync()
    {
        Show();
        for (var attempt = 0; attempt < 150 && Photo.Source is null; attempt++) await Task.Delay(100);
        var internalOpened = Photo.Source is not null;
        var externalIndex = _items.FindIndex(item => item.Reference is not null);
        if (externalIndex < 0) { Close(); return (internalOpened, false, false); }
        _index = externalIndex;
        await ShowCurrentAsync();
        var waited = Photo.Source is null && DownloadButton.Visibility == Visibility.Visible;
        await DownloadAsync(false);
        var externalOpened = Photo.Source is not null && _downloaded is not null;
        Close();
        return (internalOpened, waited, externalOpened);
    }
}
