using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Settings;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.Infrastructure.Persistence;

/// <summary>Stores non-secret preferences as JSON in the user's roaming profile. Invalid files fall back to defaults.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _path = paths.SettingsFile;
        _logger = logger;
        Current = Load();
    }

    public StudioSettings Current { get; private set; }

    public event EventHandler<StudioSettings>? Changed;

    public async Task SaveAsync(StudioSettings settings, CancellationToken cancellationToken = default)
    {
        var errors = settings.Validate();
        if (errors.Count > 0)
            throw new StudioOperationException(FailureKind.InvalidInput, string.Join(" ", errors));

        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, Options), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
            Current = settings;
        }
        finally
        {
            _saveLock.Release();
        }

        Changed?.Invoke(this, settings);
    }

    private StudioSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new StudioSettings();
            var settings = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(_path), Options) ?? new StudioSettings();
            var errors = settings.Validate();
            if (errors.Count == 0) return settings;
            _logger.LogWarning("Settings file contains invalid values ({Errors}); defaults are used", string.Join(" ", errors));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Settings could not be read; defaults are used");
        }

        return new StudioSettings();
    }
}
