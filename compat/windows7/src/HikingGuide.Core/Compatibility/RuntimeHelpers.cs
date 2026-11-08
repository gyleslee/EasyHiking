using System.Runtime.CompilerServices;

namespace HikingGuide.Core.Compatibility
{
    public static class LegacyArgument
    {
        public static void ThrowIfNullOrWhiteSpace(string? value,[CallerArgumentExpression("value")]string? name=null)
        {
            if(value is null)throw new ArgumentNullException(name);
            if(string.IsNullOrWhiteSpace(value))throw new ArgumentException("参数不能为空或空白字符。",name);
        }
    }
    public static class LegacyDisposed
    {
        public static void ThrowIf(bool disposed,object instance)
        { if(disposed)throw new ObjectDisposedException(instance.GetType().FullName); }
    }
}
namespace System.Security.Cryptography
{
    public static class LegacySha256
    {
        public static byte[] HashData(ReadOnlySpan<byte> bytes)=>SHA256.HashData(bytes);
        public static byte[] HashData(Stream stream){using var hash=SHA256.Create();return hash.ComputeHash(stream);}
        public static async Task<byte[]> HashDataAsync(Stream stream,CancellationToken token=default)
        {using var hash=SHA256.Create();return await hash.ComputeHashAsync(stream,token).ConfigureAwait(false);}
    }
}
namespace System.IO
{
    public static class LegacyStreamExtensions
    {
        public static Task<string?> ReadLineAsync(this TextReader reader,CancellationToken token)=>reader.ReadLineAsync().WaitAsync(token);
        public static async ValueTask ReadExactlyAsync(this Stream stream,Memory<byte> buffer,CancellationToken cancellationToken=default)
        {
            while(!buffer.IsEmpty)
            {
                var read=await stream.ReadAsync(buffer,cancellationToken).ConfigureAwait(false);
                if(read==0)throw new EndOfStreamException("项目数据提前结束。");
                buffer=buffer[read..];
            }
        }
    }
}
namespace System.Net.Sockets
{
    public sealed class LegacyTcpListener(IPAddress address,int port):TcpListener(address,port),IDisposable
    {public void Dispose()=>Stop();}
}
