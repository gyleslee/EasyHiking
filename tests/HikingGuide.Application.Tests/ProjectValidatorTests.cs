using System.Reflection;
using HikingGuide.Application.Projects;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class ProjectValidatorTests
{
    private readonly ProjectValidator _validator = new();

    [Fact]
    public void DuplicateStableIds_AreRejected()
    {
        var project = new HikingProject();
        var id = Guid.NewGuid();
        project.Waypoints.Add(new Waypoint { Id = id, Latitude = 30, Longitude = 110 });
        project.Waypoints.Add(new Waypoint { Id = id, Latitude = 31, Longitude = 111 });

        var result = _validator.Validate(project);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("标注点 ID 必须唯一"));
    }

    [Fact]
    public void InvalidActiveGuideNodeTrackReference_IsRejected()
    {
        var project = new HikingProject();
        project.GuideNodes.Add(new GuideNode { TrackId = Guid.NewGuid(), OriginalCoordinate = new GeoCoordinate(30, 110) });

        var result = _validator.Validate(project);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("不属于主攻略轨迹"));
        Assert.Contains(result.Warnings, warning => warning.Contains("轨迹不存在"));
    }

    [Fact]
    public void InvalidActiveTrackReference_IsRejectedEvenForDamagedLoadedData()
    {
        var project = new HikingProject();
        typeof(HikingProject).GetField("_activeTrackId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(project, Guid.NewGuid());

        var result = _validator.Validate(project);

        Assert.Contains(result.Errors, error => error.Contains("当前工作轨迹"));
    }

    [Fact]
    public void RouteControlPointCoordinate_IsValidated()
    {
        var project = new HikingProject();
        project.RouteControlPoints.Add(new RouteControlPoint { Coordinate = new GeoCoordinate(95, 110) });

        Assert.Contains(_validator.Validate(project).Errors, error => error.Contains("路线控制点"));
    }
}
