namespace HikingGuide.Core.Models;

public enum PathReferenceKind
{
    Absolute,
    ProjectRelative
}

public sealed class PathReference
{
    public string? Path { get; set; }

    public PathReferenceKind Kind { get; set; } = PathReferenceKind.Absolute;

    public string? Resolve(string? projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            return null;
        }

        if (Kind == PathReferenceKind.Absolute)
        {
            return System.IO.Path.GetFullPath(Path);
        }

        if (string.IsNullOrWhiteSpace(projectFilePath))
        {
            throw new InvalidOperationException("解析项目相对路径需要项目文件位置。");
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectFilePath))!;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, Path));
    }
}
