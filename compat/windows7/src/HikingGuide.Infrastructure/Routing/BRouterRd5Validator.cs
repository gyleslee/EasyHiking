using System.Diagnostics;
using System.Text;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Routing;

public interface IRd5IntegrityValidator
{
    Task ValidateAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>调用当前随附 BRouter 的 PhysicalFile 完整性检查，不依赖文件扩展名猜测格式。</summary>
public sealed class BRouterRd5Validator(string javaPath, string brouterJar) : IRd5IntegrityValidator
{
    private const long MaxTileBytes = 512L * 1024 * 1024;

    public async Task ValidateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到BRouter路由数据文件。", fullPath);
        if (!Path.GetExtension(fullPath).Equals(".rd5", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只能导入.rd5格式的BRouter路由数据。");
        BRouterTile.ParseFileName(Path.GetFileName(fullPath));
        var length = new FileInfo(fullPath).Length;
        if (length < 200 || length > MaxTileBytes)
            throw new InvalidDataException($"BRouter路由瓦片大小无效：{Path.GetFileName(fullPath)}。");
        if (!File.Exists(javaPath) || !File.Exists(brouterJar))
            throw new FileNotFoundException("缺少用于校验rd5的BRouter运行文件。");

        var start = new ProcessStartInfo(javaPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-Xmx512M", "-cp", brouterJar,
                     "btools.mapaccess.PhysicalFile", fullPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("BRouter瓦片校验进程启动失败。");
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line && errors.Length < 16384) errors.AppendLine(line);
        };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0 || errors.Length > 0)
            throw new InvalidDataException($"BRouter无法识别或校验失败：{Path.GetFileName(fullPath)}。{errors.ToString().Trim()}");
    }
}
