using System.Diagnostics;
using System.Text;

namespace HikingGuide.Infrastructure.Maps;

/// <summary>独立、隐藏的只读解码子进程；取消只终止本对象创建的进程，不触碰BRouter路由进程。</summary>
public sealed class OsmRoadReaderProcess(string java,string readerJar,string brouterJar,string lookups,string directory):IDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private Process? _process;
    public async Task<byte[]> ReadChunkAsync(int x,int y,CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if(_process is null||_process.HasExited)
            {
                Stop();
                var start=new ProcessStartInfo(java){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
                foreach(var argument in new[]{"-Xmx256m","-Dfile.encoding=UTF-8","-cp",readerJar+Path.PathSeparator+brouterJar,"OsmRoadReader",directory,lookups})start.ArgumentList.Add(argument);
                _process=Process.Start(start)??throw new IOException("无法启动路网解码程序。");
                _process.ErrorDataReceived+=(_,_)=>{};_process.BeginErrorReadLine();
            }
            var process=_process;
            using var registration=token.Register(Stop);
            await process.StandardInput.WriteLineAsync(x+"\t"+y).ConfigureAwait(false);await process.StandardInput.FlushAsync().ConfigureAwait(false);
            var line=await process.StandardOutput.ReadLineAsync().WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(line is null||line.StartsWith("ERROR\t",StringComparison.Ordinal))throw new InvalidDataException("路网数据无法解码，请检查rd5完整性与BRouter版本匹配。");
            if(line.Length>32*1024*1024)throw new InvalidDataException("当前路网过于密集，请放大地图后查看。");
            return Encoding.UTF8.GetBytes(line);
        }
        catch{Stop();token.ThrowIfCancellationRequested();throw;}
        finally{_gate.Release();}
    }
    private readonly object _stopGate=new();
    public void Stop()
    {lock(_stopGate){var process=_process;_process=null;if(process is null)return;try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}finally{process.Dispose();}}}
    public void Dispose()=>Stop();
}
