namespace ContextFlow.Infrastructure;

/// <summary>
/// All mutable application state lives under the current user's local app data.
/// No data is written to Program Files and nothing leaves the machine.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ContextFlow");

        SettingsFile = Path.Combine(Root, "settings.json");
        SettingsBackupFile = Path.Combine(Root, "settings.backup.json");
        SessionGuardFile = Path.Combine(Root, "session-guard.json");
        LogFile = Path.Combine(Root, "logs", "contextflow.log");
    }

    public string Root { get; }
    public string SettingsFile { get; }
    public string SettingsBackupFile { get; }
    public string SessionGuardFile { get; }
    public string LogFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
    }
}
