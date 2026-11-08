namespace HikingGuide.Infrastructure.Projects;

public sealed class ApplicationDataPaths
{
    public const string InstalledModeMarker = "EasyHiking.Installation.ini";
    public static bool IsInstalledApplication(string applicationDirectory) =>
        File.Exists(Path.Combine(Path.GetFullPath(applicationDirectory), InstalledModeMarker));
    public static bool UsesInstallationDrive(string applicationDirectory) => IsInstalledApplication(applicationDirectory) &&
        File.ReadLines(Path.Combine(Path.GetFullPath(applicationDirectory), InstalledModeMarker))
            .Any(line => line.Trim().Equals("DataMode=InstallDriveCurrentUser", StringComparison.OrdinalIgnoreCase));
    public static string InstallationDriveUserRoot(string applicationDirectory, string localAppDataDirectory)
    {
        var drive=Path.GetPathRoot(Path.GetFullPath(applicationDirectory))!;
        var local=Path.GetFullPath(localAppDataDirectory);
        var localDrive=Path.GetPathRoot(local)!;
        if(drive.Length!=3||drive[1]!=':'||localDrive.Length!=3||localDrive[1]!=':')
            throw new InvalidOperationException("程序和用户配置须使用本地盘符路径。请将软件安装到本地硬盘目录。");
        return Path.GetFullPath(Path.Combine(drive,local[localDrive.Length..],"EasyHiking"));
    }
    public static string PreviousInstalledUserRoot(string applicationDirectory)
    {
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var line=File.ReadLines(Path.Combine(Path.GetFullPath(applicationDirectory),InstalledModeMarker))
            .FirstOrDefault(value=>value.TrimStart().StartsWith("PreviousUserDataDrive=",StringComparison.OrdinalIgnoreCase));
        var drive=line?[(line.IndexOf('=')+1)..].Trim();
        if(drive is {Length:2}&&char.IsLetter(drive[0])&&drive[1]==':')
            return InstallationDriveUserRoot(drive+"\\EasyHiking",local);
        return Path.Combine(local,"EasyHiking");
    }
    public static string DefaultFunctionRootDirectory(string applicationDirectory) =>
        IsInstalledApplication(applicationDirectory)
            ? UsesInstallationDrive(applicationDirectory)
                ? InstallationDriveUserRoot(applicationDirectory,Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyHiking")
            : Path.GetFullPath(applicationDirectory);
    public ApplicationDataPaths(string? rootDirectory = null)
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("HIKINGGUIDE_APPDATA_DIR");
        RootDirectory = Path.GetFullPath(rootDirectory ??
            (string.IsNullOrWhiteSpace(configuredDirectory)
                ? DefaultRootDirectory(AppContext.BaseDirectory)
                : configuredDirectory));
    }

    /// <summary>便携版随软件携带；新安装包沿用用户LocalAppData目录结构，只替换为程序所在盘符。</summary>
    public static string DefaultRootDirectory(string applicationDirectory) =>
        Path.Combine(DefaultFunctionRootDirectory(applicationDirectory), "Data");
    public static string FunctionRootDirectory(string applicationDirectory, string? configuredDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDirectory) ? DefaultFunctionRootDirectory(applicationDirectory) : configuredDirectory);

    public string RootDirectory { get; }
    public string AutoSaveDirectory => Path.Combine(RootDirectory, "AutoSave");
    public string RecentProjectsFile => Path.Combine(RootDirectory, "recent-projects.json");
    public string SessionStateFile => Path.Combine(RootDirectory, "session.json");
    public string ActiveProjectFile => Path.Combine(RootDirectory, "active-project.json");
    public string ExternalMediaCacheDirectory => Path.Combine(RootDirectory, "ExternalMediaCache");
    public string BRouterDirectory => Path.Combine(RootDirectory, "BRouter");
    public static string OfflineMapsDirectory(string applicationDirectory,string? configuredFunctionDirectory=null)=>
        Path.Combine(FunctionRootDirectory(applicationDirectory,configuredFunctionDirectory),"离线地图");
}
