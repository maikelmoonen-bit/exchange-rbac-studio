using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Domain.Auditing;

namespace ExchangeRbacStudio.Infrastructure.Persistence;

/// <summary>
/// Append-only audit log stored as JSON Lines, one file per month. Each entry carries the SHA-256 hash of the previous
/// entry, so deletion or modification of earlier entries is detectable with <see cref="VerifyIntegrityAsync"/>.
/// </summary>
public sealed class JsonlAuditLog(AppPaths paths, TimeProvider time) : IAuditLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string StorageLocation => paths.Audit;

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(paths.Audit);
            var previous = ReadLastHash();
            var chained = entry with { PreviousHash = previous, Hash = null };
            chained = chained with { Hash = ComputeHash(chained) };
            var line = JsonSerializer.Serialize(chained, Options) + "\n";

            var file = FileFor(time.GetUtcNow());
            await using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
            var bytes = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        var entries = new List<AuditEntry>();
        foreach (var file in Files().Reverse())
        {
            var lines = await ReadLinesSharedAsync(file, cancellationToken).ConfigureAwait(false);
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var entry = TryParse(lines[i]);
                if (entry is null) continue;
                if (query.TenantId is not null && entry.TenantId != query.TenantId) continue;
                if (query.SinceUtc is not null && entry.TimestampUtc < query.SinceUtc) return entries;
                entries.Add(entry);
                if (entries.Count >= query.MaxEntries) return entries;
            }
        }

        return entries;
    }

    public async Task<AuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        string? previous = null;
        var count = 0;
        foreach (var file in Files())
        {
            var lines = await ReadLinesSharedAsync(file, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var entry = TryParse(lines[i]);
                var location = $"{Path.GetFileName(file)} line {i + 1}";
                if (entry is null)
                {
                    problems.Add($"{location}: entry is not valid JSON.");
                    continue;
                }

                count++;
                if (!string.Equals(entry.PreviousHash, previous, StringComparison.Ordinal))
                    problems.Add($"{location}: chain broken (previous hash does not match the preceding entry).");
                if (!string.Equals(entry.Hash, ComputeHash(entry with { Hash = null }), StringComparison.Ordinal))
                    problems.Add($"{location}: entry content does not match its hash.");
                previous = entry.Hash;
            }
        }

        return new AuditIntegrityResult(problems.Count == 0, count, problems);
    }

    internal static string ComputeHash(AuditEntry entry)
    {
        var canonical = JsonSerializer.Serialize(entry with { Hash = null }, Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private string? ReadLastHash()
    {
        foreach (var file in Files().Reverse())
        {
            var last = ReadLinesShared(file).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (last is null) continue;
            return TryParse(last)?.Hash;
        }

        return null;
    }

    private IEnumerable<string> Files() =>
        Directory.Exists(paths.Audit)
            ? Directory.EnumerateFiles(paths.Audit, "audit-*.jsonl").OrderBy(f => f, StringComparer.Ordinal)
            : [];

    private string FileFor(DateTimeOffset timestamp) =>
        Path.Combine(paths.Audit, $"audit-{timestamp.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.jsonl");

    private static AuditEntry? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditEntry>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> ReadLinesShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static async Task<List<string>> ReadLinesSharedAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line) lines.Add(line);
        return lines;
    }
}
