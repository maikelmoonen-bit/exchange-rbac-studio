using ExchangeRbacStudio.Application.Errors;

namespace ExchangeRbacStudio.Infrastructure.Exchange;

/// <summary>Locates PowerShell 7 (pwsh.exe). Windows PowerShell 5.1 is not supported.</summary>
public static class PowerShellLocator
{
    public static string Locate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath) && Path.GetFileName(configuredPath).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
                return configuredPath;
            throw new StudioOperationException(FailureKind.HostUnavailable,
                $"The configured PowerShell path '{configuredPath}' does not exist or is not pwsh.exe.",
                remediation: "Correct the PowerShell path in Settings or clear it to use automatic detection.");
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate)) return candidate;
        }

        throw new StudioOperationException(FailureKind.HostUnavailable,
            "PowerShell 7 (pwsh.exe) was not found. Exchange Online administration requires PowerShell 7.4 or later.",
            remediation: "Install PowerShell 7 (winget install Microsoft.PowerShell) or configure the path to pwsh.exe in Settings.");
    }

    private static IEnumerable<string> Candidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        yield return Path.Combine(programFiles, "PowerShell", "7-preview", "pwsh.exe");

        var localApps = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localApps, "Microsoft", "WindowsApps", "pwsh.exe");

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, "pwsh.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return candidate;
        }
    }
}
