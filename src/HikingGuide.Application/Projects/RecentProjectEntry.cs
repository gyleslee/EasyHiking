namespace HikingGuide.Application.Projects;

public sealed record RecentProjectEntry(string FilePath, string ProjectName, DateTimeOffset LastOpenedAt);
