using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;
using Xunit;
using System.Xml.Linq;

namespace HikingGuide.Infrastructure.Tests;

public sealed class TrackEditPersistenceExportTests
{
    [Fact]
    public async Task P62EditableNamesAndManualProperties_SurviveProjectReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HikingGuide-P62-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = CreateProject();
            var track = Assert.Single(project.Tracks);
            track.Name = "人工轨迹名";
            track.Description = "轨迹说明";
            var part = Assert.Single(track.Segments);
            part.SplitSectionName = "人工拆分分段";
            part.SplitSectionDescription = "分段说明";
            var start = new GuideNode { Name = "起点", TrackId = track.Id };
            var end = new GuideNode { Name = "终点", TrackId = track.Id };
            project.GuideNodes.Add(start);
            project.GuideNodes.Add(end);
            project.GuideSegments.Add(new GuideSegment
            {
                TrackId = track.Id, StartNodeId = start.Id, EndNodeId = end.Id,
                CustomName = "自定义攻略段", StartDistance = 0, EndDistance = track.GetTotalLength(),
                ManualData = new SegmentManualData { RoadCondition = "石阶", Description = "用户攻略内容",
                    PlannedDuration = TimeSpan.FromMinutes(45) }
            });
            var path = Path.Combine(directory, "properties.hikeproj");
            var store = new ProjectFileService();
            await store.SaveAsync(project, path, false);
            var reopened = await store.LoadAsync(path);
            Assert.Equal("人工轨迹名", reopened.Tracks[0].Name);
            Assert.Equal("轨迹说明", reopened.Tracks[0].Description);
            Assert.Equal("人工拆分分段", reopened.Tracks[0].GetSplitSections()[0].Name);
            Assert.Equal("分段说明", reopened.Tracks[0].Segments[0].SplitSectionDescription);
            var guide = Assert.Single(reopened.GuideSegments);
            Assert.Equal("自定义攻略段", guide.CustomName);
            Assert.Equal("石阶", guide.ManualData?.RoadCondition);
            Assert.Equal("用户攻略内容", guide.ManualData?.Description);
            Assert.Equal(TimeSpan.FromMinutes(45), guide.ManualData?.PlannedDuration);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(TrackFileFormat.Gpx)]
    [InlineData(TrackFileFormat.Kml)]
    [InlineData(TrackFileFormat.Kmz)]
    public async Task LogicalMultiPartMerge_PersistsAndExportsWithoutFakeGap(TrackFileFormat format)
    {
        var directory = Path.Combine(Path.GetTempPath(), "HikingGuide-P61-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = new HikingProject();
            var track = new Track { Name = "有间隙合并" };
            foreach (var lon in new[] { 113d, 113.02, 113.05 })
                track.Segments.Add(new TrackSegment { Points =
                [new TrackPoint { Latitude = 23, Longitude = lon },
                 new TrackPoint { Latitude = 23, Longitude = lon + .005 }] });
            track.RecalculateDistances();
            project.AddTrack(track);
            project.MainGuideTrackId = null;
            var before = track.GetTotalLength();
            var sources = track.GetSplitSections().Select(section => new SplitSectionSource(section.Id, false)).ToArray();
            new TrackEditService().PrepareCombineSplitSections(project, track.Id, sources, false).Apply();
            var saved = Path.Combine(directory, "merged.hikeproj");
            var store = new ProjectFileService();
            await store.SaveAsync(project, saved, false);
            var reopened = await store.LoadAsync(saved);
            var restored = Assert.Single(reopened.Tracks);
            Assert.Equal(3, Assert.Single(restored.GetSplitSections()).GeometryParts.Count);
            Assert.Equal(before, restored.GetTotalLength(), 5);
            var output = Path.Combine(directory, "merged." + format.ToString().ToLowerInvariant());
            var selection = new MergedExportSelection();
            selection.TrackIds.Add(restored.Id);
            await new ProjectMergedExportService().ExportAsync(reopened, selection, format, output, false);
            var imported = format switch
            {
                TrackFileFormat.Gpx => await new GpxTrackImporter().ImportAsync(output),
                TrackFileFormat.Kml => await new KmlTrackImporter().ImportAsync(output),
                _ => await new KmzTrackImporter().ImportAsync(output)
            };
            var exported = Assert.Single(imported.Tracks);
            Assert.Equal(3, exported.Segments.Count);
            Assert.Equal(before, exported.GetTotalLength(), 4);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(TrackFileFormat.Gpx)]
    [InlineData(TrackFileFormat.Kml)]
    [InlineData(TrackFileFormat.Kmz)]
    public async Task LogicalConnect_ExportsSingleGeometryWhileProjectRetainsConnectorParts(TrackFileFormat format)
    {
        var directory = Path.Combine(Path.GetTempPath(), "HikingGuide-P61-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = new HikingProject();
            var track = new Track { Name = "人工连接导出" };
            foreach (var lon in new[] { 113d, 113.02 })
                track.Segments.Add(new TrackSegment { Points =
                [new TrackPoint { Latitude = 23, Longitude = lon },
                 new TrackPoint { Latitude = 23, Longitude = lon + .005 }] });
            track.RecalculateDistances();
            project.AddTrack(track);
            project.MainGuideTrackId = null;
            var before = track.GetTotalLength();
            var sources = track.GetSplitSections().Select(section => new SplitSectionSource(section.Id, false)).ToArray();
            new TrackEditService().PrepareCombineSplitSections(project, track.Id, sources, true).Apply();
            var connector = Assert.Single(track.Segments, part =>
                part.OriginalAttributes.GetValueOrDefault("hg:editorProvenance") == "人工直线连接");
            Assert.True(connector.Length > 0);
            var saved = Path.Combine(directory, "connected.hikeproj");
            var store = new ProjectFileService();
            await store.SaveAsync(project, saved, false);
            var reopened = await store.LoadAsync(saved);
            var restored = Assert.Single(reopened.Tracks);
            Assert.Equal(3, Assert.Single(restored.GetSplitSections()).GeometryParts.Count);
            Assert.Single(restored.Segments, part =>
                part.OriginalAttributes.GetValueOrDefault("hg:editorProvenance") == "人工直线连接");
            var output = Path.Combine(directory, "connected." + format.ToString().ToLowerInvariant());
            var selection = new MergedExportSelection();
            selection.TrackIds.Add(restored.Id);
            await new ProjectMergedExportService().ExportAsync(reopened, selection, format, output, false);
            var imported = format switch
            {
                TrackFileFormat.Gpx => await new GpxTrackImporter().ImportAsync(output),
                TrackFileFormat.Kml => await new KmlTrackImporter().ImportAsync(output),
                _ => await new KmzTrackImporter().ImportAsync(output)
            };
            var exported = Assert.Single(imported.Tracks);
            Assert.Single(exported.Segments);
            Assert.Equal(before + connector.Length, exported.GetTotalLength(), 4);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task GpxExports_UseFormalProductNameAsCreator()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HikingGuide-Brand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = CreateProject();
            var track = project.Tracks.Single();
            var single = Path.Combine(directory, "single.gpx");
            await new GpxTrackExporter().ExportAsync(new TrackExportRequest { Tracks = [track] }, single);
            Assert.Equal(AppProduct.DisplayName, XDocument.Load(single).Root?.Attribute("creator")?.Value);
            var merged = Path.Combine(directory, "merged.gpx");
            var selection = new MergedExportSelection();
            selection.TrackIds.Add(track.Id);
            await new ProjectMergedExportService().ExportAsync(project, selection,
                TrackFileFormat.Gpx, merged, false);
            Assert.Equal(AppProduct.DisplayName, XDocument.Load(merged).Root?.Attribute("creator")?.Value);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SaveReopen_PreservesRevisionAndPointProvenance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HikingGuide-P6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = CreateProject();
            var track = project.Tracks.Single();
            track.RestoreGeometryRevision(7);
            var path = Path.Combine(directory, "edited.hikeproj");
            var store = new ProjectFileService();
            await store.SaveAsync(project, path, false);
            var reopened = await store.LoadAsync(path);
            var restored = reopened.Tracks.Single();
            Assert.Equal(7, restored.GeometryRevision);
            Assert.True(restored.Segments[0].Points[0].IsCoordinateEdited);
            Assert.True(restored.Segments[0].Points[1].IsInsertedByEditor);
            Assert.Equal(track.Segments[0].Points.Select(point => point.Id),
                restored.Segments[0].Points.Select(point => point.Id));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(TrackFileFormat.Gpx)]
    [InlineData(TrackFileFormat.Kml)]
    [InlineData(TrackFileFormat.Kmz)]
    public async Task ExportEditedPoint_UsesCurrentGeometryWithoutStaleSourceAltitude(TrackFileFormat format)
    {
        var directory = Path.Combine(Path.GetTempPath(), "HikingGuide-P6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = CreateProject();
            var track = project.Tracks.Single();
            var path = Path.Combine(directory, "edited." + format.ToString().ToLowerInvariant());
            var selection = new MergedExportSelection();
            selection.TrackIds.Add(track.Id);
            await new ProjectMergedExportService().ExportAsync(project, selection, format, path, false);
            var imported = format switch
            {
                TrackFileFormat.Gpx => await new GpxTrackImporter().ImportAsync(path),
                TrackFileFormat.Kml => await new KmlTrackImporter().ImportAsync(path),
                _ => await new KmzTrackImporter().ImportAsync(path)
            };
            var points = Assert.Single(imported.Tracks).Segments.Single().Points;
            Assert.Equal(23.001, points[0].Latitude);
            Assert.Null(points[0].SourceElevation);
            Assert.Null(points[1].SourceElevation);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task KmlGxTrack_ExportDoesNotReintroducePreservedOldGeometry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HikingGuide-P6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.kml");
            await File.WriteAllTextAsync(source,
                "<kml xmlns='http://www.opengis.net/kml/2.2' xmlns:gx='http://www.google.com/kml/ext/2.2'>" +
                "<Document><Placemark><name>轨迹</name><gx:Track><gx:coord>113 23 100</gx:coord>" +
                "<gx:coord>113.01 23 101</gx:coord></gx:Track></Placemark></Document></kml>");
            var parsed = await new KmlTrackImporter().ImportAsync(source);
            var track = Assert.Single(parsed.Tracks);
            track.Metadata["kml:xml:Track"] =
                "<gx:Track xmlns:gx='http://www.google.com/kml/ext/2.2'><gx:coord>113 23 100</gx:coord>" +
                "<gx:coord>113.01 23 101</gx:coord></gx:Track>";
            track.Segments[0].Points[0].Latitude = 24;
            track.Segments[0].Points[0].IsCoordinateEdited = true;
            var output = Path.Combine(directory, "edited.kml");
            await new KmlTrackExporter().ExportAsync(new TrackExportRequest { Tracks = [track] }, output);
            var xml = await File.ReadAllTextAsync(output);
            Assert.DoesNotContain("gx:Track", xml);
            Assert.Contains("113,24", xml);
            Assert.DoesNotContain("113,23,100", xml);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static HikingProject CreateProject()
    {
        var project = new HikingProject();
        var track = new Track { Name = "编辑后的轨迹", Segments =
        [
            new TrackSegment { Points =
            [
                new TrackPoint { Latitude = 23.001, Longitude = 113, SourceElevation = 100,
                    IsCoordinateEdited = true },
                new TrackPoint { Latitude = 23.002, Longitude = 113.002,
                    IsCoordinateEdited = true, IsInsertedByEditor = true }
            ] }
        ] };
        track.RecalculateDistances();
        project.AddTrack(track);
        return project;
    }
}
