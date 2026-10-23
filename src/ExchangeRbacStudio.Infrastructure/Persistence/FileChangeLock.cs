using ExchangeRbacStudio.Application.Abstractions;

namespace ExchangeRbacStudio.Infrastructure.Persistence;

/// <summary>
/// Prevents concurrent change execution against the same tenant from multiple application instances on this computer.
/// Implemented with an exclusively opened lock file, which the operating system releases if the process ends.
/// </summary>
public sealed class FileChangeLock(AppPaths paths) : IChangeLock
{
    public IDisposable? TryAcquire(Guid tenantId)
    {
        Directory.CreateDirectory(paths.Locks);
        var path = Path.Combine(paths.Locks, $"change-{tenantId:N}.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var stamp = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId} {DateTimeOffset.UtcNow:O}");
            stream.SetLength(0);
            stream.Write(stamp);
            stream.Flush();
            return stream;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
