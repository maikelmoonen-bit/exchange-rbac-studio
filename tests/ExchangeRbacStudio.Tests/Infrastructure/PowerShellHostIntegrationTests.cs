using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Infrastructure.Exchange;
using ExchangeRbacStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeRbacStudio.Tests.Infrastructure;

/// <summary>
/// Exercises the real ExchangeHost.ps1 in a real PowerShell 7 process. No tenant is contacted: these tests cover the
/// protocol, the allowlist and parameter validation. Skipped when pwsh.exe is not installed.
/// </summary>
public sealed class PowerShellHostIntegrationTests : IAsyncLifetime
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ers-host-" + Guid.NewGuid().ToString("N"));
    private PowerShellExchangeGateway? _gateway;

    private static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "PowerShell", "ExchangeHost.ps1");

    public ValueTask InitializeAsync()
    {
        string? pwsh = null;
        try
        {
            pwsh = PowerShellLocator.Locate(null);
        }
        catch (StudioOperationException)
        {
            // Handled below.
        }

        if (pwsh is null || !File.Exists(ScriptPath)) return ValueTask.CompletedTask;
        var paths = new AppPaths(_work, _work, _work);
        _gateway = new PowerShellExchangeGateway(new ExchangeHostOptions(() => pwsh, ScriptPath, _work), NullLogger<PowerShellExchangeGateway>.Instance);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null) await _gateway.DisposeAsync();
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private PowerShellExchangeGateway Gateway()
    {
        Assert.SkipWhen(_gateway is null, "PowerShell 7 (pwsh.exe) is not installed on this machine.");
        return _gateway!;
    }

    [Fact]
    public async Task Host_starts_and_reports_powershell_7()
    {
        var info = await Gateway().StartAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("7.", info.PowerShellVersion);
        Assert.Equal("Core", info.PowerShellEdition);
        Assert.True(_gateway!.IsHostRunning);
    }

    [Fact]
    public async Task Exchange_cmdlets_are_unavailable_before_connecting()
    {
        var gateway = Gateway();
        await gateway.StartAsync(TestContext.Current.CancellationToken);
        var availability = await gateway.GetCommandAvailabilityAsync(["New-ManagementScope", "Get-Recipient"], TestContext.Current.CancellationToken);
        Assert.All(availability.Values, Assert.False);

        var ex = await Assert.ThrowsAsync<ExchangeOperationException>(() => gateway.GetServicePrincipalsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(FailureKind.CommandNotAvailable, ex.Kind);
    }

    [Fact]
    public async Task Disallowed_commands_and_parameters_are_rejected_by_the_host()
    {
        var gateway = Gateway();
        await gateway.StartAsync(TestContext.Current.CancellationToken);
        var host = typeof(PowerShellExchangeGateway).GetField("_host", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(gateway)!;
        var send = host.GetType().GetMethod("SendAsync")!;

        async Task<HostError?> Send(string command, Dictionary<string, object?> parameters)
        {
            var task = (Task<HostResponse>)send.Invoke(host, [command, parameters, TimeSpan.FromSeconds(60), CancellationToken.None])!;
            var response = await task;
            Assert.False(response.Ok);
            return response.Error;
        }

        Assert.Equal("InvalidRequest", (await Send("Invoke-Expression", new() { ["Command"] = "Get-Process" }))!.Kind);
        Assert.Equal("InvalidRequest", (await Send("New-ManagementScope", new() { ["Name"] = "x", ["ScriptBlock"] = "{ calc }" }))!.Kind);
        Assert.Equal("InvalidRequest", (await Send("New-ServicePrincipal", new() { ["AppId"] = "not-a-guid" }))!.Kind);
        Assert.Equal("InvalidRequest", (await Send("Get-ServicePrincipal", new() { ["Identity"] = 42 }))!.Kind);
    }

    [Fact]
    public async Task Injection_attempts_in_values_are_treated_as_data()
    {
        var gateway = Gateway();
        await gateway.StartAsync(TestContext.Current.CancellationToken);
        // Values are bound as parameters, never evaluated. The command is simply unavailable before connecting.
        var ex = await Assert.ThrowsAsync<ExchangeOperationException>(() =>
            gateway.GetRecipientAsync("alice'; Remove-Item C:\\ -Recurse; '", TestContext.Current.CancellationToken));
        Assert.Equal(FailureKind.CommandNotAvailable, ex.Kind);
    }

    [Fact]
    public async Task Module_import_reports_the_installed_version_or_a_clear_error()
    {
        var gateway = Gateway();
        await gateway.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var module = await gateway.ImportModuleAsync(new Version(3, 0, 0), TestContext.Current.CancellationToken);
            Assert.True(Version.TryParse(module.Version, out var version) && version.Major >= 3);
        }
        catch (ExchangeOperationException ex)
        {
            Assert.Equal(FailureKind.ModuleMissing, ex.Kind);
            Assert.Contains("Install-Module", ex.Message);
        }
    }

    [Fact]
    public async Task Unreasonably_high_minimum_version_is_reported_as_unsupported()
    {
        var gateway = Gateway();
        await gateway.StartAsync(TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<ExchangeOperationException>(() => gateway.ImportModuleAsync(new Version(99, 0, 0), TestContext.Current.CancellationToken));
        Assert.Contains(ex.Kind, new[] { FailureKind.UnsupportedModuleVersion, FailureKind.ModuleMissing });
    }
}
