using System.Text;
using System.Text.Json;
using NetTopologySuite.Geometries;

namespace HikingGuide.Infrastructure.Maps;

/// <summary>只为显示生成MVT线瓦片；原始rd5及中间完整几何均不修改。</summary>
public static class OsmRoadVectorTile
{
    public const int Extent=4096;
    public static byte[] Encode(IEnumerable<JsonElement> chunks,int z,int tileX,int tileY)
    {
        if(z is <0 or >22||tileX<0||tileY<0||tileX>=(1<<z)||tileY>=(1<<z))throw new ArgumentOutOfRangeException(nameof(z));
        var factory=new GeometryFactory();var clip=factory.ToGeometry(new Envelope(-64,Extent+64,-64,Extent+64));
        var keys=new List<string>();var values=new List<string>();var keyMap=new Dictionary<string,uint>();var valueMap=new Dictionary<string,uint>();
        var features=new List<byte[]>();var seen=new HashSet<string>(StringComparer.Ordinal);var scale=Math.Pow(2,z);
        foreach(var chunk in chunks)
        foreach(var feature in chunk.GetProperty("features").EnumerateArray())
        {
            var props=feature.GetProperty("properties");var edge=props.GetProperty("edge").GetString()!;
            if(!seen.Add(edge))continue;
            var raw=feature.GetProperty("geometry").GetProperty("coordinates");
            var coordinates=raw.EnumerateArray().Select(p=>{
                var px=(p[0].GetDouble()+180)/360*scale-tileX;
                if(px>scale/2)px-=scale;else if(px< -scale/2)px+=scale;
                var lat=Math.Clamp(p[1].GetDouble(),-85.05112878,85.05112878)*Math.PI/180;
                var py=(1-Math.Asinh(Math.Tan(lat))/Math.PI)/2*scale-tileY;
                return new Coordinate(px*Extent,py*Extent);
            }).ToArray();
            if(coordinates.Length<2)continue;
            var line=factory.CreateLineString(coordinates);if(!line.EnvelopeInternal.Intersects(clip.EnvelopeInternal))continue;
            var intersection=line.Intersection(clip);var paths=new List<Coordinate[]>();
            for(var i=0;i<intersection.NumGeometries;i++)if(intersection.GetGeometryN(i) is LineString segment&&segment.NumPoints>=2)paths.Add(segment.Coordinates);
            if(paths.Count==0)continue;
            var geometry=new List<uint>();var cursorX=0;var cursorY=0;
            foreach(var path in paths)
            {
                var points=path.Select(p=>((int)Math.Round(p.X),(int)Math.Round(p.Y))).DistinctAdjacent().ToArray();
                if(points.Length<2)continue;
                geometry.Add(9);AddPoint(points[0]);geometry.Add((uint)((points.Length-1)*8+2));for(var i=1;i<points.Length;i++)AddPoint(points[i]);
            }
            void AddPoint((int X,int Y) p){geometry.Add(Zig(p.X-cursorX));geometry.Add(Zig(p.Y-cursorY));cursorX=p.X;cursorY=p.Y;}
            if(geometry.Count==0)continue;
            var tags=new List<uint>();
            foreach(var property in props.EnumerateObject().Where(p=>p.Name!="edge"))
            {
                if(!keyMap.TryGetValue(property.Name,out var k)){k=(uint)keys.Count;keyMap.Add(property.Name,k);keys.Add(property.Name);}
                var text=property.Value.GetString()??"";if(!valueMap.TryGetValue(text,out var v)){v=(uint)values.Count;valueMap.Add(text,v);values.Add(text);}
                tags.Add(k);tags.Add(v);
            }
            var f=new Proto();f.UInt(1,(ulong)features.Count+1);f.Packed(2,tags);f.UInt(3,2);f.Packed(4,geometry);features.Add(f.Bytes());
        }
        var layer=new Proto();layer.Text(1,"roads");foreach(var f in features)layer.Message(2,f);foreach(var k in keys)layer.Text(3,k);
        foreach(var v in values){var value=new Proto();value.Text(1,v);layer.Message(4,value.Bytes());}
        layer.UInt(5,Extent);layer.UInt(15,2);var tile=new Proto();tile.Message(3,layer.Bytes());return tile.Bytes();
    }
    private static uint Zig(int value)=>unchecked((uint)((value<<1)^(value>>31)));
    private static IEnumerable<T> DistinctAdjacent<T>(this IEnumerable<T> input)
    {var first=true;T? previous=default;foreach(var value in input){if(first||!EqualityComparer<T>.Default.Equals(previous!,value)){yield return value;previous=value;first=false;}}}
    private sealed class Proto
    {
        private readonly MemoryStream _stream=new();
        private void Var(ulong value){while(value>=128){_stream.WriteByte((byte)(value|128));value>>=7;}_stream.WriteByte((byte)value);}
        public void UInt(int field,ulong value){Var((ulong)(field*8));Var(value);}
        public void Message(int field,byte[] bytes){Var((ulong)(field*8+2));Var((ulong)bytes.Length);_stream.Write(bytes);}
        public void Text(int field,string value)=>Message(field,Encoding.UTF8.GetBytes(value));
        public void Packed(int field,IEnumerable<uint> values){var buffer=new Proto();foreach(var value in values)buffer.Var(value);Message(field,buffer.Bytes());}
        public byte[] Bytes()=>_stream.ToArray();
    }
}
