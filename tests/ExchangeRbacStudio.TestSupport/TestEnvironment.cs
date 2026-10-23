using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Applications;
using ExchangeRbacStudio.Application.Auditing;
using ExchangeRbacStudio.Application.Authorization;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Mailboxes;
using ExchangeRbacStudio.Application.Permissions;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Scoping;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeRbacStudio.TestSupport;

/// <summary>Wires the real application services to fake Graph and Exchange adapters.</summary>
public sealed class TestEnvironment
{
    public TestEnvironment()
    {
        Exchange = new FakeExchangeGateway();
        Entra = new FakeEntraDirectory();
        Settings = new FakeSettingsStore();
        Audit = new InMemoryAuditLog();
        Clock = new TestClock();
        ChangeLock = new FakeChangeLock();
        Runtime = new StudioRuntimeInfo("1.0.0-test", "TEST-WORKSTATION");
        Recorder = new AuditRecorder(Audit, Runtime, Clock, NullLogger<AuditRecorder>.Instance);
        Connections = new ConnectionManager(Entra, Exchange, Settings, Recorder, Clock, NullLogger<ConnectionManager>.Instance);
        Inventory = new RbacInventoryService(Connections, Exchange, Entra, Clock, NullLogger<RbacInventoryService>.Instance);
        Mailboxes = new MailboxDirectoryService(Connections, Exchange);
        Identity = new ApplicationIdentityService(Connections, Entra, Exchange, Inventory);
        Profiles = new ApplicationProfileService(Connections, Inventory, Exchange, Entra);
        Roles = new RoleCatalogService(Inventory);
        ScopeDesigner = new ScopeDesigner(Exchange, Clock);
        StateReader = new ConfigurationStateReader(Exchange);
        Preflight = new PreflightService(Connections, Entra, Exchange, StateReader, Mailboxes, ScopeDesigner, Settings, Clock, NullLogger<PreflightService>.Instance);
        Executor = new ChangeExecutor(Connections, Exchange, Recorder, ChangeLock, Inventory, Clock, NullLogger<ChangeExecutor>.Instance);
        Plans = new ManagementPlanService(Connections, Exchange, Clock);
        Tests = new AuthorizationTestService(Connections, Exchange, Recorder, Clock);
    }

    public FakeExchangeGateway Exchange { get; }
    public FakeEntraDirectory Entra { get; }
    public FakeSettingsStore Settings { get; }
    public InMemoryAuditLog Audit { get; }
    public TestClock Clock { get; }
    public FakeChangeLock ChangeLock { get; }
    public StudioRuntimeInfo Runtime { get; }
    public AuditRecorder Recorder { get; }
    public ConnectionManager Connections { get; }
    public RbacInventoryService Inventory { get; }
    public MailboxDirectoryService Mailboxes { get; }
    public ApplicationIdentityService Identity { get; }
    public ApplicationProfileService Profiles { get; }
    public RoleCatalogService Roles { get; }
    public ScopeDesigner ScopeDesigner { get; }
    public ConfigurationStateReader StateReader { get; }
    public PreflightService Preflight { get; }
    public ChangeExecutor Executor { get; }
    public ManagementPlanService Plans { get; }
    public AuthorizationTestService Tests { get; }

    public async Task<TestEnvironment> ConnectAsync()
    {
        await Connections.ConnectAsync(CancellationToken.None);
        return this;
    }

    /// <summary>A configuration request for Polo Test Automation scoped to the given mailboxes and roles.</summary>
    public ConfigurationRequest Request(IReadOnlyList<ExchangeRecipient>? mailboxes = null, IReadOnlyList<string>? roles = null, string scopeName = "SCOPE_APP_POLO_TEST_AUTOMATION_MAIL",
        RecipientFilterStrategy strategy = RecipientFilterStrategy.ExternalDirectoryObjectId, ConfigurationMode mode = ConfigurationMode.New, string? existingScopeName = null)
    {
        mailboxes ??= [Fixtures.Alice, Fixtures.FinanceShared];
        roles ??= ["Application Mail.Read"];
        var filter = RecipientFilterBuilder.Build(mailboxes, strategy).Filter!;
        return new ConfigurationRequest(Fixtures.Polo, mailboxes, roles, scopeName, filter, strategy, mode, existingScopeName);
    }

    public async Task<(PreflightOutcome Outcome, Domain.Planning.ChangePlan Plan)> PrepareAsync(ConfigurationRequest request)
    {
        var outcome = await Preflight.RunAsync(request, null, CancellationToken.None);
        var plan = ConfigurationPlanner.Plan(request, outcome.Context.State!, Fixtures.TenantId, Fixtures.TenantName, Settings.Current.AssignmentNameTemplate, Clock.GetUtcNow());
        return (outcome, plan);
    }

    public Func<CancellationToken, Task<Domain.Planning.ChangePlan>> Replan(ConfigurationRequest request, Domain.Planning.ChangePlan plan) => async ct =>
    {
        var state = await StateReader.ReadAsync(request, ct);
        return ConfigurationPlanner.Plan(request, state, plan.TenantId, plan.TenantDisplayName, Settings.Current.AssignmentNameTemplate, plan.CreatedUtc);
    };

    public Task<ExecutionResult> ExecuteAsync(Domain.Planning.ChangePlan plan, ConfigurationRequest? request = null, bool dryRun = false, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(plan, new ExecutionOptions(dryRun, plan.Fingerprint, request is null ? null : Replan(request, plan)), null, cancellationToken);
}
