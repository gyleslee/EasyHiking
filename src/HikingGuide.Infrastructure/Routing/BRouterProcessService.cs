using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HikingGuide.Infrastructure.Routing;

/// <summary>只管理本进程启动的 JVM；端口冲突不会结束其它应用。</summary>
public sealed class BRouterProcessService : IAsyncDisposable
{
    private readonly string _javaPath;
    private readonly string _jarPath;
    private readonly string _segmentsDirectory;
    private readonly string _profilesDirectory;
    private Process? _ownedProcess;

    public BRouterProcessService(string javaPath, string jarPath, string segmentsDirectory,
        string profilesDirectory)
    {
        _javaPath = Path.GetFullPath(javaPath);
        _jarPath = Path.GetFullPath(jarPath);
        _segmentsDirectory = Path.GetFullPath(segmentsDirectory);
        _profilesDirectory = Path.GetFullPath(profilesDirectory);
    }

    public int? Port { get; private set; }
    public bool IsRunning => _ownedProcess is { HasExited: false } && Port is not null;
    public Uri? BaseAddress => Port is int port ? new Uri($"http://127.0.0.1:{port}/") : null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        if (!File.Exists(_javaPath)) throw new FileNotFoundException("找不到 BRouter 所需的 Java 运行环境。", _javaPath);
        if (!File.Exists(_jarPath)) throw new FileNotFoundException("找不到 BRouter 本地引擎。", _jarPath);
        var profile = Path.Combine(_profilesDirectory, "shortest.brf");
        if (!File.Exists(profile) ||
            !System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(profile),
                @"(?m)^\s*assign\s+validForFoot\s+1\s*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidOperationException("缺少已验证的徒步最短路由配置 shortest.brf。");
        if (!Directory.Exists(_segmentsDirectory) ||
            !Directory.EnumerateFiles(_segmentsDirectory, "*.rd5").Any())
            throw new InvalidOperationException("尚未下载 BRouter 路由数据。");

        var port = ChooseLoopbackPort();
        var info = new ProcessStartInfo(_javaPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = _segmentsDirectory
        };
        foreach (var argument in new[] { "-Xmx512M", "-DmaxRunningTime=120", "-cp", _jarPath,
                     "btools.server.RouteServer", _segmentsDirectory, _profilesDirectory,
                     _profilesDirectory, port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "2", "127.0.0.1" })
            info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("BRouter 进程启动失败。");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ownedProcess = process;
        Port = port;
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { Timeout = TimeSpan.FromSeconds(2) };
            for (var attempt = 0; attempt < 60; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException(
                    $"BRouter 已退出，退出码 {process.ExitCode}。");
                try
                {
                    using var response = await client.GetAsync(BaseAddress, cancellationToken);
                    // 根路径返回 404 是官方 RouteServer 的正常响应，说明本机 API 可访问。
                    if (response.StatusCode == HttpStatusCode.NotFound) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                await Task.Delay(250, cancellationToken);
            }
            throw new TimeoutException("BRouter 本地服务健康检查超时。");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync();
        await StartAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        var process = _ownedProcess;
        _ownedProcess = null;
        Port = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally { process.Dispose(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static int ChooseLoopbackPort()
    {
        foreach (var preferred in new[] { 17777, 0 })
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, preferred);
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            catch (SocketException) when (preferred != 0) { }
        }
        throw new InvalidOperationException("无法分配 BRouter 本机监听端口。");
    }
}
