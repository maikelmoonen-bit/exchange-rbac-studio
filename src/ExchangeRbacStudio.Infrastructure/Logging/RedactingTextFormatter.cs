using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Infrastructure.Persistence;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace ExchangeRbacStudio.Infrastructure.Logging;

/// <summary>Formats a log event with an inner formatter and removes tokens and credentials before writing.</summary>
public sealed class RedactingTextFormatter(ITextFormatter inner) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter();
        inner.Format(logEvent, buffer);
        output.Write(SensitiveDataRedactor.Redact(buffer.ToString()));
    }
}

public static class LoggingConfiguration
{
    public static Logger CreateLogger(AppPaths paths, LogEventLevel minimumLevel, string version)
    {
        Directory.CreateDirectory(paths.Logs);
        return new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.WithProperty("AppVersion", version)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.File(
                new RedactingTextFormatter(new CompactJsonFormatter()),
                Path.Combine(paths.Logs, "studio-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
    }
}
