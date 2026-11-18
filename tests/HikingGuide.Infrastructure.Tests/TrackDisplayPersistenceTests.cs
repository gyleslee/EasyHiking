using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Application.Maps;
namespace HikingGuide.Infrastructure.Tests;
public sealed class TrackDisplayPersistenceTests
{
    [Fact]public async Task GlobalTrackStylesSurviveRestartWithoutChangingSourcesOrProjectDefaults()
    {
        using var directory=new TemporaryDirectory();GlobalSettingsService Service()=>new(new ApplicationDataPaths(directory.Path),new TestLoggingService(),new MapSourceTemplateService());
        var service=Service();await service.LoadAsync();var sources=service.Current.MapSources.Select(s=>s.Id).ToArray();
        service.Current.TrackDisplay=new(){SelectedColor="#12abcd",Selected=new(){Width=11,Opacity=.43,LineStyle="dashdot",OutlineWidth=2,Endpoints="end",EndpointSize=10,EndpointMinZoom=14,Arrows=false,ShowEndpointText=false},MainGuide=new(){Width=6,LineStyle="dot"}};
        await service.SaveAsync();var reload=Service();await reload.LoadAsync();var s=reload.Current.TrackDisplay;
        Assert.Equal("#12ABCD",s.SelectedColor);Assert.Equal(11,s.Selected.Width);Assert.Equal(.43,s.Selected.Opacity);Assert.Equal("dashdot",s.Selected.LineStyle);Assert.False(s.Selected.Arrows);Assert.Equal("end",s.Selected.Endpoints);Assert.Equal(14,s.Selected.EndpointMinZoom);Assert.Equal("dot",s.MainGuide.LineStyle);Assert.Equal(sources,reload.Current.MapSources.Select(x=>x.Id));
        Assert.Equal("both",new TrackDisplaySettings().Ordinary.Endpoints);
    }
    private sealed class TemporaryDirectory:IDisposable
    {
        public string Path {get;}=System.IO.Path.Combine(@"F:\Codex\Temp","TrackDisplayTests",Guid.NewGuid().ToString("N"));
        public TemporaryDirectory()=>Directory.CreateDirectory(Path);
        public void Dispose()=>Directory.Delete(Path,true);
    }
    private sealed class TestLoggingService:HikingGuide.Application.Logging.ILoggingService
    {
        public void Debug(string m){} public void Information(string m){} public void Warning(string m){}
        public void Error(Exception e,string m){} public void Dispose(){}
    }
}
