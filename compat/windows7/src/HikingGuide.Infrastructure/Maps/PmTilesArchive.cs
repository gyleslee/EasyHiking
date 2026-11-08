using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HikingGuide.Core.Maps;
namespace HikingGuide.Infrastructure.Maps;

public sealed class OfflineMapPack
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Name {get;set;}="";
    public string SourceName {get;set;}="";
    public Guid SourceId {get;set;}
    public string SourceFingerprint {get;set;}="";
    public string Attribution {get;set;}="";
    public OfflineMapRegion Region {get;set;}=new([]);
    public int MinZoom {get;set;}
    public int MaxZoom {get;set;}
    public long TileCount {get;set;}
    public long PlannedTiles {get;set;}
    public bool IsPartial {get;set;}
    public DateTimeOffset Created {get;set;}=DateTimeOffset.Now;
    [JsonIgnore] public string FilePath {get;set;}="";
    [JsonIgnore] public long FileBytes {get;set;}
    [JsonIgnore] public bool Enabled {get;set;}=true;
    [JsonIgnore] public string SizeText=>OfflineMapFormatting.Bytes(FileBytes);
    [JsonIgnore] public string DateText=>Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore] public string ZoomText=>$"Z{MinZoom}—Z{MaxZoom}";
    [JsonIgnore] public string CompletenessText=>IsPartial?$"部分可用（{TileCount:N0}/{PlannedTiles:N0}）":"完整";
}
public static class OfflineMapFormatting
{
    public static string Bytes(long bytes)=>bytes>=1L<<30?$"{bytes/(double)(1L<<30):F2} GB":bytes>=1<<20?$"{bytes/(double)(1<<20):F1} MB":$"{bytes/1024d:F1} KB";
}
/// <summary>PMTiles v3 raster writer/reader, gzip directories, one leaf level; tile images remain uncompressed internally.</summary>
public sealed class PmTilesArchive
{
    private sealed record Entry(ulong Id,ulong Offset,int Length,ulong Run);
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly byte[] _header;
    private readonly Entry[] _root;
    private readonly Dictionary<ulong,Entry[]> _leaves=[];
    private readonly object _sync=new();
    public OfflineMapPack Pack {get;}
    public PmTilesArchive(string path)
    {
        _path=Path.GetFullPath(path);using var file=Open();_header=Read(file,0,127);
        if(Encoding.ASCII.GetString(_header,0,7)!="PMTiles"||_header[7]!=3||_header[98]!=1||_header[99] is <2 or >4||_header[100]>_header[101]||_header[101]>24)throw new InvalidDataException("不是受支持的PMTiles v3栅格地图。");
        if(U64(_header,8)<127||U64(_header,8)+U64(_header,16)>16384)throw new InvalidDataException("PMTiles根目录无效。");
        foreach(var pair in new[]{(8,16),(24,32),(40,48),(56,64)})ValidateSection(file.Length,U64(_header,pair.Item1),U64(_header,pair.Item2));
        _root=ReadDirectory(file,U64(_header,8),(int)U64(_header,16));
        if(U64(_header,32)>8*1024*1024)throw new InvalidDataException("离线地图元信息过大。");
        using var metadata=JsonDocument.Parse(Inflate(Read(file,(long)U64(_header,24),(int)U64(_header,32)),8*1024*1024));
        Pack=metadata.RootElement.GetProperty("easyhiking").Deserialize<OfflineMapPack>(JsonOptions)??throw new InvalidDataException("缺少离线地图元信息。");
        if(Pack.Id==Guid.Empty||Pack.SourceFingerprint is not {Length:64}||!Pack.SourceFingerprint.All(Uri.IsHexDigit)||Pack.TileCount<=0||Pack.MinZoom!=_header[100]||Pack.MaxZoom!=_header[101])throw new InvalidDataException("离线地图信息无效。");
        OfflineMapCoverage.CreateGeometry(Pack.Region);Pack.FilePath=_path;Pack.FileBytes=file.Length;
    }
    public async Task<byte[]?> ReadTileAsync(OfflineTileAddress address,CancellationToken token=default)
    {
        if(address.Zoom<Pack.MinZoom||address.Zoom>Pack.MaxZoom)return null;
        return await Task.Run(()=>{
            token.ThrowIfCancellationRequested();using var file=Open();var id=(ulong)address.TileId;var entry=Find(_root,id);if(entry is null)return null;
            if(entry.Run==0)
            {
                Entry[] entries;lock(_sync){if(!_leaves.TryGetValue(entry.Offset,out entries!)){if(_leaves.Count>=32)_leaves.Clear();if(entry.Offset>U64(_header,48)||(ulong)entry.Length>U64(_header,48)-entry.Offset)throw new InvalidDataException("离线地图叶目录越界。");entries=ReadDirectory(file,U64(_header,40)+entry.Offset,entry.Length);_leaves[entry.Offset]=entries;}}
                entry=Find(entries,id);
            }
            if(entry is null||entry.Run==0||id-entry.Id>=entry.Run)return null;
            if(entry.Offset>U64(_header,64)||(ulong)entry.Length>U64(_header,64)-entry.Offset||entry.Length>8*1024*1024)throw new InvalidDataException("离线瓦片数据越界。");
            return Read(file,checked((long)(U64(_header,56)+entry.Offset)),entry.Length);
        },token).ConfigureAwait(false);
    }
    public string ContentType=>_header[99] switch{2=>"image/png",3=>"image/jpeg",4=>"image/webp",_=>"application/octet-stream"};
    private FileStream Open()=>new(_path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
    private static Entry? Find(Entry[] entries,ulong id)
    {var low=0;var high=entries.Length-1;while(low<=high){var mid=(low+high)/2;if(entries[mid].Id<=id)low=mid+1;else high=mid-1;}return high>=0?entries[high]:null;}
    private Entry[] ReadDirectory(FileStream file,ulong offset,int length)
    {
        if(length<=0||length>4*1024*1024)throw new InvalidDataException("离线地图目录长度无效。");
        var bytes=Inflate(Read(file,checked((long)offset),length),4*1024*1024);var position=0;
        ulong Var(){ulong value=0;for(var shift=0;shift<=63;shift+=7){if(position>=bytes.Length)throw new InvalidDataException("离线目录不完整。");var b=bytes[position++];if(shift==63&&b>1)throw new InvalidDataException("离线目录数值溢出。");value|=(ulong)(b&127)<<shift;if(b<128)return value;}throw new InvalidDataException("离线目录数值无效。");}
        var count=Var();if(count==0||count>100_000)throw new InvalidDataException("离线目录条目数量无效。");var n=(int)count;var ids=new ulong[n];var runs=new ulong[n];var lengths=new int[n];var offsets=new ulong[n];ulong previous=0;
        for(var i=0;i<n;i++){ids[i]=checked(previous+Var());if(i>0&&ids[i]<=previous)throw new InvalidDataException("离线目录顺序无效。");previous=ids[i];}
        for(var i=0;i<n;i++)runs[i]=Var();for(var i=0;i<n;i++){var len=Var();if(len==0||len>8*1024*1024)throw new InvalidDataException("离线目录条目大小无效。");lengths[i]=(int)len;}
        for(var i=0;i<n;i++){var value=Var();if(value==0&&i==0)throw new InvalidDataException("离线目录偏移无效。");offsets[i]=value==0?checked(offsets[i-1]+(ulong)lengths[i-1]):value-1;}
        return Enumerable.Range(0,n).Select(i=>new Entry(ids[i],offsets[i],lengths[i],runs[i])).ToArray();
    }
    private byte[] Inflate(byte[] bytes,int limit)
    {
        if(_header[97]==1)return bytes;if(_header[97]!=2)throw new InvalidDataException("离线地图压缩方式不受支持。");
        using var stream=new GZipStream(new MemoryStream(bytes),CompressionMode.Decompress);using var output=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=stream.Read(buffer,0,buffer.Length))>0){if(output.Length+count>limit)throw new InvalidDataException("离线地图解压大小超出限制。");output.Write(buffer,0,count);}return output.ToArray();
    }
    private static byte[] Read(FileStream file,long offset,int count)
    {ValidateSection(file.Length,(ulong)offset,(ulong)count);file.Position=offset;var bytes=new byte[count];var n=0;while(n<count){var read=file.Read(bytes,n,count-n);if(read==0)throw new EndOfStreamException("离线地图不完整。");n+=read;}return bytes;}
    private static void ValidateSection(long length,ulong offset,ulong count){if(offset>(ulong)length||count>(ulong)length-offset)throw new InvalidDataException("离线地图文件边界无效。");}
    private static ulong U64(byte[] bytes,int offset)=>BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset,8));
    private static void U64(byte[] bytes,int offset,ulong value)=>BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset,8),value);
    private static byte[] Zip(byte[] raw){using var output=new MemoryStream();using(var gzip=new GZipStream(output,CompressionLevel.Optimal,true))gzip.Write(raw);return output.ToArray();}
    private static byte[] Encode(IReadOnlyList<Entry> entries)
    {
        using var output=new MemoryStream();void Var(ulong value){while(value>=128){output.WriteByte((byte)(value|128));value>>=7;}output.WriteByte((byte)value);}
        Var((ulong)entries.Count);ulong previous=0;foreach(var e in entries){Var(e.Id-previous);previous=e.Id;}foreach(var e in entries)Var(e.Run);foreach(var e in entries)Var((ulong)e.Length);
        ulong next=0;for(var i=0;i<entries.Count;i++){var e=entries[i];Var(i>0&&e.Offset==next?0:e.Offset+1);next=e.Offset+(ulong)e.Length;}return Zip(output.ToArray());
    }
    public static async Task WriteAsync(string path,OfflineMapPack pack,byte tileType,IAsyncEnumerable<(long TileId,byte[] Bytes)> tiles,CancellationToken token=default)
    {
        if(tileType is <2 or >4)throw new ArgumentException("离线图片格式无效。");
        var metadata=Zip(JsonSerializer.SerializeToUtf8Bytes(new{name=pack.Name,attribution=pack.Attribution,type="baselayer",easyhiking=pack},JsonOptions));
        await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,true);
        try
        {
            output.SetLength(16384);output.Position=16384;await output.WriteAsync(metadata,token).ConfigureAwait(false);var dataOffset=(ulong)output.Position;
            var root=new List<Entry>();var leaves=new List<byte[]>();var batch=new List<Entry>();ulong count=0,previous=0,leafOffset=0;
            await foreach(var tile in tiles.WithCancellation(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();if(tile.Bytes.Length==0||tile.Bytes.Length>8*1024*1024||tile.TileId<0||count>0&&(ulong)tile.TileId<=previous)throw new InvalidDataException("离线瓦片顺序或长度无效。");
                previous=(ulong)tile.TileId;batch.Add(new(previous,(ulong)output.Position-dataOffset,tile.Bytes.Length,1));await output.WriteAsync(tile.Bytes,token).ConfigureAwait(false);count++;
                if(batch.Count==4096)FlushBatch();
            }
            if(count==0)throw new InvalidDataException("没有可写入的离线瓦片。");
            FlushBatch();var dataLength=(ulong)output.Position-dataOffset;var leafStart=(ulong)output.Position;
            foreach(var leaf in leaves)await output.WriteAsync(leaf,token).ConfigureAwait(false);
            var rootBytes=Encode(root);if(rootBytes.Length>16257)throw new InvalidDataException("离线目录过大，请分区下载。");
            var header=new byte[127];Encoding.ASCII.GetBytes("PMTiles").CopyTo(header,0);header[7]=3;
            U64(header,8,127);U64(header,16,(ulong)rootBytes.Length);U64(header,24,16384);U64(header,32,(ulong)metadata.Length);U64(header,40,leafStart);U64(header,48,leafOffset);U64(header,56,dataOffset);U64(header,64,dataLength);U64(header,72,count);U64(header,80,count);U64(header,88,count);
            header[96]=1;header[97]=2;header[98]=1;header[99]=tileType;header[100]=(byte)pack.MinZoom;header[101]=(byte)pack.MaxZoom;
            var points=pack.Region.Points;void Position(int offset,double lon,double lat){BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset,4),(int)Math.Round(lon*1e7));BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset+4,4),(int)Math.Round(lat*1e7));}
            Position(102,points.Min(p=>p.Longitude),points.Min(p=>p.Latitude));Position(110,points.Max(p=>p.Longitude),points.Max(p=>p.Latitude));header[118]=(byte)pack.MinZoom;Position(119,points.Average(p=>p.Longitude),points.Average(p=>p.Latitude));
            output.Position=0;await output.WriteAsync(header,token).ConfigureAwait(false);await output.WriteAsync(rootBytes,token).ConfigureAwait(false);await output.FlushAsync(token).ConfigureAwait(false);output.Flush(true);
            void FlushBatch(){if(batch.Count==0)return;var bytes=Encode(batch);root.Add(new(batch[0].Id,leafOffset,bytes.Length,0));leaves.Add(bytes);leafOffset+=(ulong)bytes.Length;batch.Clear();}
        }
        catch{await output.DisposeAsync().ConfigureAwait(false);if(File.Exists(path))File.Delete(path);throw;}
    }
}
