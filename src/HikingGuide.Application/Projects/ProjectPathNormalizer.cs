namespace HikingGuide.Application.Projects;

public static class ProjectPathNormalizer
{
    public static string Normalize(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var file = new FileInfo(full);
        try
        {
            if (file.Exists && file.ResolveLinkTarget(true) is { } directTarget)
                return Path.GetFullPath(directTarget.FullName);
            for (var directory = file.Directory; directory is not null; directory = directory.Parent)
            {
                if (directory.ResolveLinkTarget(true) is not { } target) continue;
                var suffix = Path.GetRelativePath(directory.FullName, full);
                return Path.GetFullPath(Path.Combine(target.FullName, suffix));
            }
        }
        catch (IOException) { /* 损坏的链接仍按规范化字面路径交给加载器报告。 */ }
        catch (UnauthorizedAccessException) { /* 无权查询链接目标时仍能尝试普通打开。 */ }
        return full;
    }
}
