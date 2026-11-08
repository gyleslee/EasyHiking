using System.Text.Json;
using System.Text;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Application.Maps;
namespace HikingGuide.Infrastructure.Tests;
public sealed class OsmRoadVectorTileTests
{
    [Fact] public async Task TerrainModeUsesFreshEngineRequestsButPreservesDiskCacheIdentity()
    {
        var root=Path.Combine(Path.GetTempPath(),"HikingGuide-RoadMode-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root,"E110_N20.rd5"),[1]);
            using var routeGate=new SemaphoreSlim(0,1);
            using var source=new OsmRoadTileSource(root,root,"missing.jar",Path.Combine(root,"cache"),routeGate);
            async Task<string> Signature(bool? terrain=null)=>JsonSerializer.SerializeToElement(await source.ConfigureAsync(new(){Enabled=true},terrainEnabled:terrain)).GetProperty("signature").GetString()!;
            var flat=await Signature(false);source.SetViewport(0,0,1,1);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>source.GetAsync(new Uri($"https://hikingguide-roads.local/{flat}/15/26692/14090.pbf")));
            var terrain=await Signature(true);var retained=await Signature();
            Assert.EndsWith("-2d",flat);Assert.EndsWith("-3d",terrain);Assert.Equal(terrain,retained);
            Assert.Equal(flat[..^3],terrain[..^3]);
            var request=source.GetAsync(new Uri($"https://hikingguide-roads.local/{terrain}/15/26692/14090.pbf"));await Task.Delay(50);
            Assert.False(request.IsCompleted);Assert.Equal(flat,await Signature(false));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed record Field(int Number,ulong Value,byte[] Bytes);
    private static List<Field> Read(byte[] bytes)
    {
        var i=0;ulong Var(){ulong value=0;var shift=0;do{var b=bytes[i++];value|=(ulong)(b&127)<<shift;if(b<128)return value;shift+=7;}while(shift<64);throw new InvalidDataException();}
        var result=new List<Field>();while(i<bytes.Length){var key=Var();var number=(int)(key>>3);if((key&7)==0)result.Add(new(number,Var(),[]));else if((key&7)==2){var count=(int)Var();result.Add(new(number,0,bytes[i..(i+count)]));i+=count;}else throw new InvalidDataException();}return result;
    }
    [Fact] public void EncodedMvtHasValidLayerGeometryAndDeduplicatesChunkEdges()
    {
        using var doc=JsonDocument.Parse("""{"features":[{"properties":{"edge":"a","highway":"path"},"geometry":{"coordinates":[[0,0],[1,0]]}},{"properties":{"edge":"a","highway":"path"},"geometry":{"coordinates":[[0,0],[1,0]]}},{"properties":{"edge":"b","highway":"track"},"geometry":{"coordinates":[[120,80],[121,80]]}}]}""");
        var bytes=OsmRoadVectorTile.Encode([doc.RootElement],10,512,512);var layer=Read(Read(bytes).Single().Bytes);
        Assert.Equal("roads",Encoding.UTF8.GetString(layer.Single(f=>f.Number==1).Bytes));Assert.Equal(4096UL,layer.Single(f=>f.Number==5).Value);Assert.Equal(2UL,layer.Single(f=>f.Number==15).Value);
        var feature=Read(Assert.Single(layer,f=>f.Number==2).Bytes);Assert.Equal(2UL,feature.Single(f=>f.Number==3).Value);Assert.NotEmpty(feature.Single(f=>f.Number==4).Bytes);
        Assert.Contains(layer,f=>f.Number==3&&Encoding.UTF8.GetString(f.Bytes)=="highway");Assert.DoesNotContain(layer,f=>f.Number==3&&Encoding.UTF8.GetString(f.Bytes)=="edge");
    }
    [Fact] public async Task DisabledOrMissingDataCannotGenerateAndClearKeepsOtherSources()
    {
        var root=Path.Combine(Path.GetTempPath(),"HikingGuide-RoadTest-"+Guid.NewGuid().ToString("N"));
        try
        {
            var cache=MapTileDiskCache.Shared(root);var other=Guid.NewGuid();await cache.GetAsync(other,"keep",10000,()=>Task.FromResult(new CachedMapTile([1,2,3],"image/png",DateTimeOffset.MaxValue)));
            using var source=new OsmRoadTileSource(Path.Combine(root,"missing"),root,"missing.jar",root);var config=JsonSerializer.SerializeToElement(await source.ConfigureAsync(new OsmRoadSettings{Enabled=true}));Assert.False(config.GetProperty("enabled").GetBoolean());
            await Assert.ThrowsAsync<OperationCanceledException>(()=>source.GetAsync(new Uri("https://hikingguide-roads.local/a/15/1/1.pbf")));await source.ClearAsync();Assert.NotEmpty(Directory.GetFiles(Path.Combine(root,other.ToString("N"))));Assert.Equal(0,source.ChunksDecoded);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public async Task TerrainPriorityDefersNewDecodeAndDisableCancelsWaitingWork()
    {
        var root=Path.Combine(Path.GetTempPath(),"HikingGuide-RoadPriority-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root,"E110_N20.rd5"),[1]);
            using var routeGate=new SemaphoreSlim(1,1);using var source=new OsmRoadTileSource(root,root,"absent.jar",Path.Combine(root,"cache"),routeGate);
            var settings=new OsmRoadSettings{Enabled=true};var config=JsonSerializer.SerializeToElement(await source.ConfigureAsync(settings));var sig=config.GetProperty("signature").GetString();
            TerrainGenerationPriority.Shared.SetTerrainReady(false);
            var request=source.GetAsync(new Uri($"https://hikingguide-roads.local/{sig}/15/1/1.pbf"));await Task.Delay(50);
            Assert.False(request.IsCompleted);Assert.Equal(0,source.ChunksDecoded);Assert.True(await routeGate.WaitAsync(100));routeGate.Release();
            settings.Enabled=false;await source.ConfigureAsync(settings);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(0,source.ChunksDecoded);await source.StopAsync();
        }
        finally{TerrainGenerationPriority.Shared.SetTerrainReady(true);Directory.Delete(root,true);}
    }
}

