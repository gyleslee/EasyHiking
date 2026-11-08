namespace HikingGuide.Application.TrackFiles;

public sealed record ImportWarning(string Code, string Message, string? Context = null);
