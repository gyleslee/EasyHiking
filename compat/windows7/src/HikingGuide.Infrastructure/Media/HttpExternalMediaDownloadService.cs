using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HikingGuide.Application.Media;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;

namespace HikingGuide.Infrastructure.Media;

public sealed class ExternalMediaException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class HttpExternalMediaDownloadService : IExternalMediaDownloadService, IDisposable
{
    private readonly ExternalMediaDownloadProfile _profile;
    private readonly HttpClient _client;
    private readonly HttpClient? _proxyClient;
    private readonly bool _ownsClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolver;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _publicResolver;
    private readonly Func<Uri, bool> _trustedProxy;
    private readonly string _cacheDirectory;
    private readonly IImageDecodeValidator? _imageValidator;

    public HttpExternalMediaDownloadService(ApplicationDataPaths paths,
        ExternalMediaDownloadProfile? profile = null, HttpMessageHandler? testHandler = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        IImageDecodeValidator? imageValidator = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? publicResolver = null,
        Func<Uri, bool>? trustedProxy = null)
    {
        _profile = profile ?? new ExternalMediaDownloadProfile();
        _cacheDirectory = paths.ExternalMediaCacheDirectory;
        _imageValidator = imageValidator;
        _resolver = resolver ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));
        _publicResolver = publicResolver ?? ResolvePublicDnsAsync;
        _trustedProxy = trustedProxy ?? HasTrustedLoopbackProxy;
        if (testHandler is not null) _client = new HttpClient(testHandler, disposeHandler: false);
        else
        {
            _client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = async (context, token) =>
                {
                    var addresses = await _resolver(context.DnsEndPoint.Host, token);
                    var address = addresses.FirstOrDefault(IsPublicAddress)
                        ?? throw new ExternalMediaException("网络图片地址指向本机或私有网络，已拒绝连接。");
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            });
            _proxyClient = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = true,
                Proxy = HttpClient.DefaultProxy
            });
            _proxyClient.Timeout = Timeout.InfiniteTimeSpan;
            _proxyClient.DefaultRequestHeaders.UserAgent.ParseAdd(_profile.UserAgent);
            _ownsClient = true;
        }
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(_profile.UserAgent);
    }

    public async Task<ExternalMediaDownloadResult> DownloadAsync(string url, bool forceRefresh = false,
        IProgress<(long Received, long? Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var current = ValidateUrlSyntax(url);
        Directory.CreateDirectory(_cacheDirectory);
        var urlHash = Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(Encoding.UTF8.GetBytes(current.AbsoluteUri)));
        if (!forceRefresh)
        {
            var cached = await FindValidCacheAsync(urlHash, cancellationToken);
            if (cached is not null) return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_profile.Timeout);
        try
        {
            var useProxy = await ValidateHostAsync(current, timeout.Token);
            for (var redirect = 0; redirect <= _profile.MaximumRedirects; redirect++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await (useProxy && _proxyClient is not null ? _proxyClient : _client)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (redirect == _profile.MaximumRedirects)
                        throw new ExternalMediaException("图片下载失败：重定向次数超过限制。");
                    var location = response.Headers.Location ?? throw new ExternalMediaException("图片下载失败：服务器重定向地址无效。");
                    current = ValidateUrlSyntax(new Uri(current, location).AbsoluteUri);
                    useProxy = await ValidateHostAsync(current, timeout.Token);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new ExternalMediaException($"图片下载失败：服务器返回 HTTP {(int)response.StatusCode}。");
                var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (contentType is not null && contentType is not
                    ("image/jpeg" or "image/jpg" or "image/pjpeg" or "image/png" or "image/webp" or
                     "application/octet-stream" or "binary/octet-stream"))
                    throw new ExternalMediaException("该链接返回的内容不是支持的图片格式。");
                var total = response.Content.Headers.ContentLength;
                if (total > _profile.MaximumDownloadBytes)
                    throw new ExternalMediaException("图片超过允许的下载大小。");
                var tempPath = Path.Combine(_cacheDirectory, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    long received = 0;
                    await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                     FileShare.None, 65536, useAsync: true))
                    await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                    {
                        var buffer = new byte[65536];
                        int count;
                        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                        {
                            received += count;
                            if (received > _profile.MaximumDownloadBytes)
                                throw new ExternalMediaException("图片超过允许的下载大小。");
                            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                            progress?.Report((received, total));
                        }
                    }
                    var bytes = await File.ReadAllBytesAsync(tempPath, timeout.Token);
                    if (!ProjectImageInspector.TryInspect(bytes, out var mime, out var extension, out var width, out var height) ||
                        contentType is not null && contentType is not ("application/octet-stream" or "binary/octet-stream") &&
                        !(contentType == mime || mime == "image/jpeg" && contentType is "image/jpg" or "image/pjpeg"))
                        throw new ExternalMediaException("图片内容或文件签名无效。");
                    if (_imageValidator is not null &&
                        !await Task.Run(() => _imageValidator.CanDecode(bytes), timeout.Token))
                        throw new ExternalMediaException("图片解码失败，可能已损坏。");
                    var contentHash = Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(bytes));
                    var cachePath = Path.Combine(_cacheDirectory, $"{urlHash}_{contentHash}{extension}");
                    if (File.Exists(cachePath)) File.Delete(tempPath);
                    else File.Move(tempPath, cachePath);
                    return new ExternalMediaDownloadResult(cachePath, mime, extension, bytes.LongLength,
                        contentHash, width, height, false);
                }
                finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
            }
            throw new ExternalMediaException("图片下载失败：重定向次数超过限制。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ExternalMediaException("图片下载超时，请稍后重试。"); }
        catch (HttpRequestException exception)
        { throw new ExternalMediaException("图片下载失败：网络连接、DNS 或 TLS 出错。", exception); }
    }

    public Task<ExternalMediaDownloadResult?> TryGetCachedAsync(string url,
        CancellationToken cancellationToken = default)
    {
        var uri = ValidateUrlSyntax(url);
        if (!Directory.Exists(_cacheDirectory)) return Task.FromResult<ExternalMediaDownloadResult?>(null);
        var hash = Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        return FindValidCacheAsync(hash, cancellationToken);
    }

    private async Task<ExternalMediaDownloadResult?> FindValidCacheAsync(string urlHash, CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, urlHash + "_*"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var contentHash = fileName[(urlHash.Length + 1)..];
            if (contentHash.Length != 64 || !contentHash.All(Uri.IsHexDigit)) continue;
            var info = new FileInfo(path);
            if (info.Length > _profile.MaximumDownloadBytes) continue;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(bytes)).Equals(contentHash, StringComparison.OrdinalIgnoreCase) ||
                !ProjectImageInspector.TryInspect(bytes, out var mime, out var extension, out var width, out var height)) continue;
            return new ExternalMediaDownloadResult(path, mime, extension, bytes.LongLength,
                contentHash, width, height, true);
        }
        return null;
    }

    private Uri ValidateUrlSyntax(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !_profile.AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Host.Length == 0 ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new ExternalMediaException("该图片地址使用不允许的协议或本机地址。");
        return uri;
    }

    private async Task<bool> ValidateHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(uri.Host, out var literal) ? [literal] : await _resolver(uri.Host, cancellationToken); }
        catch (SocketException exception) { throw new ExternalMediaException("图片下载失败：无法解析服务器地址。", exception); }
        if (addresses.Length == 0) throw new ExternalMediaException("图片下载失败：无法解析服务器地址。");
        var useProxy = _trustedProxy(uri);
        if (addresses.All(IsFakeIp) && useProxy && !IPAddress.TryParse(uri.Host, out _))
        {
            try { addresses = await _publicResolver(uri.IdnHost, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { throw new ExternalMediaException("代理环境无法核验图片服务器的公网地址，已拒绝下载。", exception); }
        }
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new ExternalMediaException("网络图片地址指向本机或私有网络，已拒绝连接。");
        return useProxy;
    }

    private static bool IsFakeIp(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    private static bool HasTrustedLoopbackProxy(Uri destination)
    {
        var proxy = HttpClient.DefaultProxy;
        if (proxy.IsBypassed(destination)) return false;
        var endpoint = proxy.GetProxy(destination);
        return endpoint is not null && endpoint.IsLoopback && endpoint.Scheme is ("http" or "https");
    }

    private static async Task<IPAddress[]> ResolvePublicDnsAsync(string host, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var addresses = new List<IPAddress>();
        foreach (var queryType in new[] { "A", "AAAA" })
        {
            using var response = await client.GetAsync(
                $"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type={queryType}", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("Status", out var status) || status.GetInt32() != 0 ||
                !document.RootElement.TryGetProperty("Answer", out var answer)) continue;
            foreach (var item in answer.EnumerateArray())
                if (item.TryGetProperty("type", out var type) && type.GetInt32() is 1 or 28 &&
                    item.TryGetProperty("data", out var data) && IPAddress.TryParse(data.GetString(), out var address))
                    addresses.Add(address);
        }
        return addresses.ToArray();
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0]; var b = bytes[1];
            return a is >= 1 and <= 223 && a != 10 && a != 127 &&
                   !(a == 100 && b is >= 64 and <= 127) &&
                   !(a == 169 && b == 254) && !(a == 172 && b is >= 16 and <= 31) &&
                   !(a == 192 && b is 0 or 168) && !(a == 198 && b is 18 or 19);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
               bytes[0] is >= 0x20 and <= 0x3f && !IPAddress.IsLoopback(address);
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); _proxyClient?.Dispose(); }
}
