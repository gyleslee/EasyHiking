using System.Text.Json.Serialization;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Models;

public sealed class Track : System.ComponentModel.INotifyPropertyChanged
{
    // 运行时缓存使用 Revision；持久化后继续递增，不能与内容指纹混用。
    [JsonInclude]
    public long GeometryRevision { get; private set; }
    public Guid Id { get; set; } = Guid.NewGuid();

    private string? _name;
    public string? Name { get => _name; set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string? DisplayColor { get; set; }

    public string? Description { get; set; }

    public List<TrackSegment> Segments { get; set; } = [];

    public string? SourceFile { get; set; }

    public TrackSourceFormat SourceFormat { get; set; }

    public Dictionary<string, string?> Metadata { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => GetPointCount() == 0;

    [JsonIgnore]
    public TrackPoint? StartPoint => Segments.FirstOrDefault(segment => !segment.IsEmpty)?.StartPoint;

    [JsonIgnore]
    public TrackPoint? EndPoint => Segments.LastOrDefault(segment => !segment.IsEmpty)?.EndPoint;

    public int GetPointCount() => Segments.Sum(segment => segment.PointCount);

    /// <summary>只按相邻且同 ID 的物理段分组；旧项目无需迁移 Schema。</summary>
    public IReadOnlyList<SplitSection> GetSplitSections()
    {
        var sections = new List<SplitSection>();
        var index = 0;
        while (index < Segments.Count)
        {
            var first = Segments[index];
            var id = first.SplitSectionId ?? first.Id;
            var parts = new List<TrackSegment> { first };
            index++;
            while (index < Segments.Count && (Segments[index].SplitSectionId ?? Segments[index].Id) == id)
                parts.Add(Segments[index++]);
            sections.Add(new SplitSection(id, Id,
                first.SplitSectionName ?? first.Name ?? $"分段 {sections.Count + 1}", parts, sections.Count));
        }
        return sections;
    }

    public double GetTotalLength(IGeoDistanceService? distanceService = null) =>
        Segments.Sum(segment => segment.GetLength(distanceService));

    public GeoBoundingBox? GetBoundingBox()
    {
        GeoBoundingBox? result = null;
        foreach (var point in Segments.SelectMany(segment => segment.Points))
        {
            if (result is null)
            {
                result = new GeoBoundingBox(
                    point.Latitude,
                    point.Latitude,
                    point.Longitude,
                    point.Longitude);
            }
            else
            {
                result.ExpandToInclude(point.Coordinate);
            }
        }

        return result;
    }

    public void RecalculateDistances(IGeoDistanceService? distanceService = null)
    {
        var cumulativeDistance = 0d;
        foreach (var segment in Segments)
        {
            // 不同 TrackSegment 之间不计算虚构连接距离。
            // 但累计里程继续沿整个 Track 累加。
            cumulativeDistance = segment.RecalculateDistances(cumulativeDistance, distanceService);
        }
    }

    public void Reverse(IGeoDistanceService? distanceService = null)
    {
        Segments.Reverse();
        foreach (var segment in Segments)
        {
            segment.Points.Reverse();
        }

        RecalculateDistances(distanceService);
        GeometryRevision++;
    }

    public void NotifyGeometryChanged() => GeometryRevision++;

    /// <summary>撤销与重做只恢复已验证的历史版本，不触发额外编辑。</summary>
    public void RestoreGeometryRevision(long revision)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        GeometryRevision = revision;
    }
}
