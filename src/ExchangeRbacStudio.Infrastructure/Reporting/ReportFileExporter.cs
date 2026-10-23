using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Application.Reporting;
using ExchangeRbacStudio.Infrastructure.Persistence;

namespace ExchangeRbacStudio.Infrastructure.Reporting;

/// <summary>Writes change reports as structured JSON and a self-contained HTML document.</summary>
public sealed class ReportFileExporter(AppPaths paths) : IReportExporter
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string DefaultDirectory => paths.Reports;

    public async Task<ReportExportResult> ExportAsync(ChangeReport report, string directory, string baseFileName, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var safeName = string.Concat(baseFileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var jsonPath = Path.Combine(directory, safeName + ".json");
        var htmlPath = Path.Combine(directory, safeName + ".html");

        // Defense in depth: the report model never contains secrets, but every exported text passes the redactor.
        var json = SensitiveDataRedactor.Redact(JsonSerializer.Serialize(report, JsonOptions));
        await File.WriteAllTextAsync(jsonPath, json, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(htmlPath, SensitiveDataRedactor.Redact(HtmlReportRenderer.Render(report)), cancellationToken).ConfigureAwait(false);
        return new ReportExportResult(jsonPath, htmlPath);
    }
}
