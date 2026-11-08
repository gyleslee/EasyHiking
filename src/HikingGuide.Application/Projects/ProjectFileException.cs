namespace HikingGuide.Application.Projects;

public sealed class ProjectFileException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class ProjectVersionException(string message) : Exception(message);
