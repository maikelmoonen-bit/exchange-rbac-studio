using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeRbacStudio.Infrastructure.Exchange;

internal sealed class HostRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("parameters")]
    public required IReadOnlyDictionary<string, object?> Parameters { get; init; }
}

internal sealed class HostError
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("errorId")] public string? ErrorId { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("exceptionType")] public string? ExceptionType { get; init; }
    [JsonPropertyName("targetObject")] public string? TargetObject { get; init; }
    [JsonPropertyName("innerMessage")] public string? InnerMessage { get; init; }

    public override string ToString() =>
        string.Join(Environment.NewLine, new[]
        {
            $"Message: {Message}",
            ErrorId is null ? null : $"ErrorId: {ErrorId}",
            Category is null ? null : $"Category: {Category}",
            Reason is null ? null : $"Reason: {Reason}",
            ExceptionType is null ? null : $"Exception: {ExceptionType}",
            TargetObject is null ? null : $"Target: {TargetObject}",
            InnerMessage is null ? null : $"Inner: {InnerMessage}",
        }.Where(l => l is not null));
}

internal sealed class HostResponse
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("records")] public List<Dictionary<string, JsonElement>>? Records { get; init; }
    [JsonPropertyName("warnings")] public List<string>? Warnings { get; init; }
    [JsonPropertyName("meta")] public Dictionary<string, JsonElement>? Meta { get; init; }
    [JsonPropertyName("error")] public HostError? Error { get; init; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; init; }

    public IReadOnlyList<HostRecord> GetRecords() => (Records ?? []).Select(r => new HostRecord(r)).ToList();

    public bool MetaFlag(string name) =>
        Meta is not null && Meta.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>Typed, tolerant accessors over a flat record produced by the host's output projections.</summary>
internal sealed class HostRecord(IReadOnlyDictionary<string, JsonElement> values)
{
    public string? String(string name)
    {
        if (!values.TryGetValue(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())),
            _ => null,
        };
    }

    public string RequiredString(string name, string fallback = "") => String(name) is { Length: > 0 } s ? s : fallback;

    public bool? Bool(string name)
    {
        if (!values.TryGetValue(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
            _ => null,
        };
    }

    public int? Int(string name) =>
        values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;

    public Guid? Guid(string name) => System.Guid.TryParse(String(name), out var g) ? g : null;

    public DateTimeOffset? Date(string name) =>
        DateTimeOffset.TryParse(String(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    public IReadOnlyList<string> Strings(string name)
    {
        if (!values.TryGetValue(name, out var value)) return [];
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList(),
            JsonValueKind.String => [value.GetString()!],
            _ => [],
        };
    }
}
