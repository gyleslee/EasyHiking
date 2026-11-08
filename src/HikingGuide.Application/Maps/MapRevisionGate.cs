namespace HikingGuide.Application.Maps;

/// <summary>统一判断后台生成结果是否仍属于当前项目，防止快速切换后的旧数据覆盖新地图。</summary>
public sealed class MapRevisionGate
{
    private readonly object _sync = new();
    private Guid? _projectId;
    private long _revision;

    public long Begin(Guid? projectId)
    {
        lock (_sync)
        {
            _projectId = projectId;
            return ++_revision;
        }
    }

    public bool IsCurrent(Guid projectId, long revision)
    {
        lock (_sync) return _projectId == projectId && _revision == revision;
    }
}
