using System.IO;
using System.Text.Json;
using System.Windows;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunChinaRangeSmokeAsync(MainWindow window,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;
        var folder=Path.GetDirectoryName(Path.GetFullPath(output))!;
        if(!folder.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("中国范围验收必须隔离");
        try
        {
            var china=BRouterController!.China;
            var files=Directory.GetFiles(@"F:\Hiking Guide\易行徒步规划制作器\BRouter\RoutingData\current","*.rd5")
                .Select(Path.GetFileName).OrderBy(n=>n,StringComparer.Ordinal).ToArray();
            checks["实际控制器采用已确认的70个rd5清单"]=china.IsApprovedRd5Range&&china.Tiles.Count==70;
            checks["范围与用户指定目录全部70个文件完全一致"]=files.Length==70&&files.SequenceEqual(china.Tiles.Select(t=>t.FileName).OrderBy(n=>n,StringComparer.Ordinal));
            var resources=typeof(App).Assembly.GetManifestResourceNames();
            checks["正式程序集只嵌入当前rd5清单而非旧1度清单"]=resources.Count(n=>n.EndsWith("ChinaRd5Tiles.txt",StringComparison.Ordinal))==1&&!resources.Any(n=>n.EndsWith("China1DegreeTiles.txt",StringComparison.Ordinal));
            checks["路由操作目录与正式数据隔离"]=BRouterController.Paths.RoutingDataDirectory.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase);
            var dialog=new BRouterSettingsWindow(BRouterController){Owner=window};dialog.Show();await T10ManualLayoutAsync(dialog);
            checks["路由管理显示70个瓦片且无旧67个提示"]=dialog.ChinaManifest.Text.Contains("70 个",StringComparison.Ordinal)&&!dialog.ChinaManifest.Text.Contains("67",StringComparison.Ordinal);
            checks["中国范围下载入口通过清单校验并可用"]=dialog.UpdateChinaButton.IsEnabled;
            await CaptureT10ClientAsync(dialog,Path.Combine(folder,"中国范围70个路由瓦片.png"));dialog.Close();
        }
        catch(Exception ex){error=ex.ToString();}
        var passed=error is null&&checks.Count>0&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,SourceSha256=BRouterController!.China.SourceSha256,
            Tiles=BRouterController.China.Tiles.Select(t=>new{t.FileName,t.Bounds}),Downloaded=false,Error=error},new JsonSerializerOptions{WriteIndented=true}));
        return passed;
    }
}
