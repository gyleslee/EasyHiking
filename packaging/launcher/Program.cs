using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

[assembly: AssemblyTitle("易行徒步规划制作器")]
[assembly: AssemblyProduct("易行徒步规划制作器")]
[assembly: AssemblyDescription("易行徒步规划制作器启动入口")]

internal static class Program
{
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string title, uint type);

    // Quote according to the Windows CommandLineToArgvW rules, not shell syntax.
    internal static string Quote(string argument)
    {
        var result=new StringBuilder("\"");
        int slashes=0;
        foreach(char c in argument)
        {
            if(c=='\\'){slashes++;continue;}
            if(c=='\"'){result.Append('\\',slashes*2+1);result.Append(c);slashes=0;continue;}
            result.Append('\\',slashes);slashes=0;result.Append(c);
        }
        result.Append('\\',slashes*2);result.Append('"');return result.ToString();
    }

    internal static bool IsX64Host(string file)
    {
        try { using(var stream=File.OpenRead(file)) using(var reader=new BinaryReader(stream))
        {
            if(stream.Length<64 || reader.ReadUInt16()!=0x5A4D)return false;
            stream.Position=0x3C;uint offset=reader.ReadUInt32();
            if(offset>stream.Length-6)return false;
            stream.Position=offset;return reader.ReadUInt32()==0x4550 && reader.ReadUInt16()==0x8664;
        }} catch(IOException){return false;} catch(UnauthorizedAccessException){return false;}
    }

    private static string FindHost()
    {
        // Use the installed x64 host directly; do not depend on PATH or an SDK.
        using(var hive=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
        using(var key=hive.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
        {
            var location=key==null?null:key.GetValue("InstallLocation") as string;
            if(!string.IsNullOrEmpty(location))
            {
                var candidate=Path.Combine(location,"dotnet.exe");
                if(IsX64Host(candidate))return candidate;
            }
        }
        var programFiles=Environment.GetEnvironmentVariable("ProgramW6432");
        if(string.IsNullOrEmpty(programFiles))programFiles=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        // On Arm64, the native host is in dotnet; the x64 host is in dotnet\x64.
        var emulated=Path.Combine(programFiles,"dotnet","x64","dotnet.exe");
        if(IsX64Host(emulated))return emulated;
        var fallback=Path.Combine(programFiles,"dotnet","dotnet.exe");
        if(IsX64Host(fallback))return fallback;
        throw new FileNotFoundException("未找到64位 .NET 桌面运行环境。请安装 .NET 10 Desktop Runtime（x64）后重试。\r\nhttps://dotnet.microsoft.com/download/dotnet/10.0");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string root=AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            string dll=Path.Combine(root,"易行徒步规划制作器.dll");
            if(!File.Exists(dll))throw new FileNotFoundException("程序文件不完整，请重新安装。缺少："+dll);
            var command=new StringBuilder("exec "+Quote(dll));
            foreach(string argument in args)command.Append(' ').Append(Quote(argument));
            var start=new ProcessStartInfo(FindHost(),command.ToString());
            start.WorkingDirectory=root;start.UseShellExecute=false;start.CreateNoWindow=true;
            // Intentionally no GPU flags, startup hooks, runtime overrides or global settings.
            using(var process=Process.Start(start))
            {
                if(process==null)throw new InvalidOperationException("未能创建软件进程。");
            }
            return 0;
        }
        catch(Exception error)
        {
            MessageBox(IntPtr.Zero,"软件未能启动：\r\n\r\n"+error.Message,"易行徒步规划制作器",0x10);
            return 1;
        }
    }
}
