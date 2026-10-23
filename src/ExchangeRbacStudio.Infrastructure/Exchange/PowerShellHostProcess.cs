using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Application.Errors;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.Infrastructure.Exchange;

/// <summary>
/// A persistent, isolated pwsh.exe process running ExchangeHost.ps1. Requests are written as single JSON lines to
/// stdin; responses are read from stdout and correlated by request ID. Lines without the per-session marker are
/// treated as console noise and only logged.
/// </summary>
internal sealed class PowerShellHostProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly Process _process;
    private readonly string _marker;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HostResponse>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private int _exited;

    private PowerShellHostProcess(Process process, string marker, ILogger logger)
    {
        _process = process;
        _marker = marker;
        _logger = logger;
        _stdoutPump = Task.Run(ReadStdoutAsync);
        _stderrPump = Task.Run(ReadStderrAsync);
    }

    public event EventHandler? Exited;

    public bool IsRunning => Volatile.Read(ref _exited) == 0 && !HasProcessExited();

    public int ProcessId => _process.Id;

    public static PowerShellHostProcess Start(string pwshPath, string scriptPath, string workingDirectory, ILogger logger)
    {
        if (!File.Exists(scriptPath))
            throw new StudioOperationException(FailureKind.HostUnavailable, $"The PowerShell host script was not found at '{scriptPath}'. Reinstall the application.");

        var marker = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var utf8 = new UTF8Encoding(false);
        var startInfo = new ProcessStartInfo
        {
            FileName = pwshPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = workingDirectory,
        };

        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-ProtocolMarker", marker })
            startInfo.ArgumentList.Add(argument);

        // Make module autoloading predictable and keep telemetry off for the hosted session.
        startInfo.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["POWERSHELL_UPDATECHECK"] = "Off";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is not StudioOperationException)
        {
            throw new StudioOperationException(FailureKind.HostUnavailable, $"PowerShell 7 could not be started from '{pwshPath}'.", ex.Message, inner: ex);
        }

        process.EnableRaisingEvents = true;
        var host = new PowerShellHostProcess(process, marker, logger);
        process.Exited += (_, _) => host.OnExited();
        logger.LogInformation("Started PowerShell host process {ProcessId} ({Path})", process.Id, pwshPath);
        return host;
    }

    public async Task<HostResponse> SendAsync(string command, IReadOnlyDictionary<string, object?> parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsRunning)
            throw new StudioOperationException(FailureKind.HostUnavailable, "The PowerShell host is not running.");

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var json = JsonSerializer.Serialize(new HostRequest { Id = id, Command = command, Parameters = parameters }, JsonOptions);
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _pending.TryRemove(id, out _);
            throw new StudioOperationException(FailureKind.HostUnavailable, "The PowerShell host stopped accepting requests.", ex.Message, inner: ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw new StudioOperationException(FailureKind.Timeout, $"Exchange Online did not respond to {command} within {timeout.TotalMinutes:0.#} minute(s).");
        }
        catch (OperationCanceledException)
        {
            // Soft cancellation: the host finishes the command, its late response is discarded.
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public void Kill()
    {
        try
        {
            if (!HasProcessExited())
            {
                _logger.LogWarning("Terminating PowerShell host process {ProcessId}", _process.Id);
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate PowerShell host process");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            try
            {
                await SendAsync("Host.Shutdown", new Dictionary<string, object?>(), TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Host shutdown request failed");
            }

            try
            {
                _process.StandardInput.Close();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Kill();
            }
        }

        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Pumps end when the process exits; nothing else to clean up.
        }

        _process.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (!line.StartsWith(_marker, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        _logger.LogDebug("PowerShell host output: {Line}", SensitiveDataRedactor.Redact(line));
                    continue;
                }

                HostResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<HostResponse>(line.AsSpan(_marker.Length), JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Malformed response from PowerShell host");
                    continue;
                }

                if (response?.Id is { } id && _pending.TryRemove(id, out var completion))
                    completion.TrySetResult(response);
                else
                    _logger.LogDebug("Discarded late or uncorrelated host response {Id}", response?.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PowerShell host stdout reader stopped");
        }
        finally
        {
            OnExited();
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _logger.LogWarning("PowerShell host stderr: {Line}", SensitiveDataRedactor.Redact(line));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PowerShell host stderr reader stopped");
        }
    }

    private void OnExited()
    {
        if (Interlocked.Exchange(ref _exited, 1) == 1) return;

        int? exitCode = null;
        try
        {
            if (HasProcessExited()) exitCode = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // Exit code unavailable.
        }

        _logger.LogWarning("PowerShell host process exited (exit code {ExitCode})", exitCode);
        foreach (var (id, completion) in _pending.ToArray())
        {
            if (_pending.TryRemove(id, out _))
                completion.TrySetException(new StudioOperationException(FailureKind.HostUnavailable, "The PowerShell host process exited unexpectedly."));
        }

        Exited?.Invoke(this, EventArgs.Empty);
    }

    private bool HasProcessExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
