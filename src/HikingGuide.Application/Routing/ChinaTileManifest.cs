using System.Security.Cryptography;
using System.Text;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Routing;

/// <summary>中国范围固定清单。保留旧1°解析兼容；当前使用经用户确认的rd5文件清单。</summary>
public sealed class ChinaTileManifest
{
    public const string ApprovedRd5SourceSha256 = "F68B78EDB31404CC92DEE1CB2F63A9B076C85F74B0C0759A2BDD5520FB2AB8D2";
    public required int SourceLineCount { get; init; }
    public required string SourceSha256 { get; init; }
    public required IReadOnlyList<BRouterTile> Tiles { get; init; }
    public bool IsRd5FileList { get; init; }
    public bool IsApprovedRd5Range => IsRd5FileList && SourceSha256 == ApprovedRd5SourceSha256;

    public static ChinaTileManifest ParseRd5(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var text = new UTF8Encoding(false, true).GetString(source).TrimStart('\uFEFF');
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var tiles = new HashSet<BRouterTile>();
        foreach (var line in lines)
            if (!tiles.Add(BRouterTile.ParseFileName(line.Trim()))) throw new FormatException("中国范围路由瓦片清单包含重复文件名。");
        if (tiles.Count == 0) throw new FormatException("中国范围路由瓦片清单为空。");
        return new ChinaTileManifest
        {
            SourceLineCount = lines.Length,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(source)),
            IsRd5FileList = true,
            Tiles = Array.AsReadOnly(tiles.OrderBy(t => t.SouthLatitude).ThenBy(t => t.WestLongitude).ToArray())
        };
    }

    public static ChinaTileManifest Parse(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var text = new UTF8Encoding(false, true).GetString(source).TrimStart('\uFEFF');
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var tiles = new HashSet<BRouterTile>();
        foreach (var line in lines)
            tiles.Add(BRouterTile.ParseOneDegreeCell(line.Trim()));
        if (lines.Length == 0) throw new FormatException("中国范围瓦片清单为空。");
        return new ChinaTileManifest
        {
            SourceLineCount = lines.Length,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(source)),
            Tiles = tiles.OrderBy(tile => tile.SouthLatitude)
                .ThenBy(tile => tile.WestLongitude).ToArray()
        };
    }
}
