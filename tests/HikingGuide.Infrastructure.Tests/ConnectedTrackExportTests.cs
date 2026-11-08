using HikingGuide.Core.Models;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Infrastructure.TrackFiles;
using System.Xml.Linq;

namespace HikingGuide.Infrastructure.Tests;

public sealed class ConnectedTrackExportTests
{
    [Theory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task ConnectedTrack_SingleGeometry_AllPointsAndSourceUnchanged(bool merged,bool kml)
    {
        var section=Guid.NewGuid(); var track=new Track { Name="连接成果", Metadata=new() { ["hg:editorOperation"]="直线连接轨迹" } };
        foreach(var lon in new[]{113d,113.01,113.02}) track.Segments.Add(new() { SplitSectionId=section,Points=[new() {Latitude=23,Longitude=lon,SourceElevation=100},new() {Latitude=23,Longitude=lon+.01,SourceElevation=200}] });
        track.RecalculateDistances(); var length=track.GetTotalLength(); var references=track.Segments.ToArray();
        var root=Path.Combine(Path.GetTempPath(),"HikingGuide-ConnectedExport-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var output=Path.Combine(root,kml?"track.kml":"track.gpx");
            if(merged){var p=new HikingProject(); p.AddTrack(track); var selection=new MergedExportSelection(); selection.TrackIds.Add(track.Id); await new ProjectMergedExportService().ExportAsync(p,selection,kml?TrackFileFormat.Kml:TrackFileFormat.Gpx,output,false);}
            else { ITrackExporter exporter=kml ? new KmlTrackExporter() : new GpxTrackExporter(); await exporter.ExportAsync(new() {Tracks=[track]},output); }
            var xml=XDocument.Load(output); Assert.Single(xml.Descendants(),e=>e.Name.LocalName==(kml?"LineString":"trkseg")); Assert.DoesNotContain(xml.Descendants(),e=>e.Name.LocalName=="MultiGeometry");
            var result=kml? await new KmlTrackImporter().ImportAsync(output):await new GpxTrackImporter().ImportAsync(output);
            var imported=Assert.Single(result.Tracks); Assert.Single(imported.Segments); Assert.Equal(6,imported.GetPointCount());
            Assert.Equal(track.Segments.SelectMany(s=>s.Points).Select(p=>(p.Latitude,p.Longitude,p.SourceElevation)),imported.Segments[0].Points.Select(p=>(p.Latitude,p.Longitude,p.SourceElevation)));
            Assert.Equal(references,track.Segments); Assert.Equal(length,track.GetTotalLength());
        }
        finally { Directory.Delete(root,true); }
    }
}
