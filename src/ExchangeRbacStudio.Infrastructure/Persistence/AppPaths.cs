namespace ExchangeRbacStudio.Infrastructure.Persistence;

/// <summary>
/// Per-user storage locations. All locations are inside the user profile, which is protected by the user's ACLs.
/// No secrets are stored in any of them.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null, string? settingsDirectory = null, string? reportsDirectory = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExchangeRbacStudio");
        SettingsDirectory = settingsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExchangeRbacStudio");
        Reports = reportsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Exchange RBAC Studio", "Reports");
    }

    public string Root { get; }

    public string SettingsDirectory { get; }

    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    public string Logs => Path.Combine(Root, "Logs");

    public string Audit => Path.Combine(Root, "Audit");

    public string Locks => Path.Combine(Root, "Locks");

    public string Reports { get; }

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Root, SettingsDirectory, Logs, Audit, Locks })
            Directory.CreateDirectory(directory);
    }
}
