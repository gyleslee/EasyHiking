using System.IO.Compression;
using System.Xml.Linq;
using HikingGuide.Core.Models;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Infrastructure.TrackFiles;

namespace HikingGuide.Infrastructure.Tests;

public sealed class TrackExportNameCompatibilityTests
{
    [Theory]
    [InlineData(TrackFileFormat.Gpx,false)] [InlineData(TrackFileFormat.Kml,false)] [InlineData(TrackFileFormat.Kmz,false)]
    [InlineData(TrackFileFormat.Gpx,true)] [InlineData(TrackFileFormat.Kml,true)] [InlineData(TrackFileFormat.Kmz,true)]
    public async Task DocumentNamesUseTracks_NotProject_ForSingleAndMultiple(TrackFileFormat format,bool multiple)
    {
        var p=new HikingProject {Name="船底顶"};
        Track Make(string name)=>new() {Name=name,Segments=[new() {Points=[new() {Latitude=23,Longitude=113},new() {Latitude=23,Longitude=113.01}]}]};
        var a=Make("船底顶-联A西A"); p.AddTrack(a); var b=Make("另一条独立轨迹"); if(multiple)p.AddTrack(b,false);
        var selection=new MergedExportSelection(); foreach(var track in p.Tracks)selection.TrackIds.Add(track.Id);
        var root=Path.Combine(Path.GetTempPath(),"HikingGuide-ExportNames-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var output=Path.Combine(root,"export."+format.ToString().ToLowerInvariant()); await new ProjectMergedExportService().ExportAsync(p,selection,format,output,false);
            XDocument doc; if(format==TrackFileFormat.Kmz){using var zip=ZipFile.OpenRead(output); using var input=zip.GetEntry("doc.kml")!.Open(); doc=XDocument.Load(input);} else doc=XDocument.Load(output);
            var expected=multiple?a.Name+"、"+b.Name:a.Name;
            if(format==TrackFileFormat.Gpx){XNamespace ns="http://www.topografix.com/GPX/1/1"; Assert.Equal(expected,doc.Root!.Element(ns+"metadata")!.Element(ns+"name")!.Value); Assert.Equal(p.Tracks.Select(t=>t.Name),doc.Root.Elements(ns+"trk").Select(t=>t.Element(ns+"name")!.Value));}
            else {XNamespace ns="http://www.opengis.net/kml/2.2"; var document=doc.Root!.Element(ns+"Document")!; Assert.Equal(expected,document.Element(ns+"name")!.Value); Assert.Equal(p.Tracks.Select(t=>t.Name),document.Descendants(ns+"Placemark").Where(e=>e.Elements(ns+"LineString").Any()).Select(e=>e.Element(ns+"name")!.Value));}
            Assert.Equal("船底顶",p.Name);
        }
        finally {Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DirectExportOverridesStaleDocumentProjectName(bool kml)
    {
        var track=new Track {Name="独立轨迹名",Segments=[new() {Points=[new() {Latitude=23,Longitude=113},new() {Latitude=23,Longitude=113.01}]}]};
        var root=Path.Combine(Path.GetTempPath(),"HikingGuide-DirectNames-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var output=Path.Combine(root,"test.xml"); ITrackExporter exporter=kml?new KmlTrackExporter():new GpxTrackExporter();
            await exporter.ExportAsync(new() {DocumentName="项目名",Tracks=[track]},output);
            var doc=XDocument.Load(output); var names=doc.Descendants().Where(e=>e.Name.LocalName=="name").Select(e=>e.Value).ToArray();
            Assert.DoesNotContain("项目名",names); Assert.All(names,n=>Assert.Equal("独立轨迹名",n));
        } finally {Directory.Delete(root,true);}
    }
}
