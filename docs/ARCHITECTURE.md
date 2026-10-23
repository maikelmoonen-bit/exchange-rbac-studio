# Architecture

## Overview

```
┌──────────────────────── ExchangeRbacStudio.App (WPF, net10.0-windows) ────────────────────────┐
│ Views (XAML, WPF-UI Fluent)  ◄─ bindings ─►  View models (CommunityToolkit.Mvvm)               │
│ Shell · Navigation · Dialogs · Theme · Composition root (Microsoft.Extensions.Hosting / DI)    │
└───────────────────────────────────────────┬────────────────────────────────────────────────────┘
                                            │ calls
┌──────────────────────── ExchangeRbacStudio.Application (net10.0) ─────────────────────────────┐
│ ConnectionManager · RbacInventoryService · ApplicationIdentity/Profile · MailboxDirectory       │
│ RoleCatalog · ScopeDesigner · PreflightService · ConfigurationPlanner · ManagementPlanner       │
│ ChangeExecutor · AuthorizationTestService · ReportBuilder · AuditRecorder · SensitiveDataRedactor│
│ Ports: IEntraDirectory · IExchangeRbacGateway · IAuditLog · ISettingsStore · IReportExporter   │
└───────────────┬──────────────────────────────────────────────────────────────┬────────────────┘
                │ pure rules                                                   │ implemented by
┌───────────────▼────────── ExchangeRbacStudio.Domain ──────────┐  ┌───────────▼──── ExchangeRbacStudio.Infrastructure ────┐
│ Role & Entra permission catalogs, capability lattice           │  │ GraphEntraDirectory (Graph SDK + Azure.Identity)      │
│ RecipientFilterBuilder / Analyzer, ScopeNamingPolicy           │  │ PowerShellExchangeGateway ─► pwsh.exe ExchangeHost.ps1│
│ EntraGrantAnalyzer, RbacOverlapAnalyzer, AuthorizationEvaluator│  │ JsonlAuditLog · JsonSettingsStore · FileChangeLock    │
│ ChangePlan / ExchangeCommand / PreflightReport / AuditEntry    │  │ ReportFileExporter + HtmlReportRenderer · Serilog     │
└────────────────────────────────────────────────────────────────┘  └───────────────────────────────────────────────────────┘
```

Dependencies point inwards: Domain has no dependencies; Application depends on Domain and defines ports; Infrastructure
implements the ports; the App composes everything. View models never call Microsoft 365 directly.

## Key design decisions

| Decision | Rationale |
|---|---|
| **Exchange runs in a separate, persistent `pwsh.exe` process** | The ExchangeOnlineManagement module and the Microsoft Graph SDK both ship MSAL; loading both into one .NET process causes assembly-version conflicts. A child process isolates them, uses the administrator's installed PowerShell 7 and module, keeps the EXO session alive across operations, and can be terminated to cancel an interactive sign-in. |
| **Fixed, allowlisted dispatcher instead of script text** | `ExchangeHost.ps1` accepts one JSON request per line, validates the command against an allowlist and every parameter against a declared type (String, Guid, Switch, Int, StringArray), then **splats** it. No `Invoke-Expression`, no script blocks from input. Requests are parsed with `System.Text.Json` to avoid `ConvertFrom-Json` type coercion. Output is projected to a fixed property set (data minimisation). |
| **One `ExchangeCommand` object for preview and execution** | The review page's PowerShell preview is rendered from the same typed command objects the executor sends, so the preview cannot diverge from what runs. |
| **Plans are fingerprinted and re-planned before execution** | `ChangePlan.Fingerprint` (SHA-256 over tenant and command sequence) must equal the validated fingerprint, and the plan is rebuilt from live state right before the first write. Any difference (another admin changed something) aborts with no changes. |
| **Writes are never retried; state is queried instead** | After each write the executor reads Exchange to confirm the expected object. A write that errored but whose result exists is reported as succeeded-with-note; nothing is ever re-sent automatically. |
| **Exact-identity scopes only** | Filters are disjunctions of immutable GUID equality; values are formatted from parsed GUIDs (no user text reaches the filter). Membership is verified by Exchange before use. |
| **Tenant isolation by session ID** | Every connect/disconnect/tenant change yields a new session ID; inventory caches, wizard state and pages are discarded when it changes. Graph and Exchange tenants must match; the executor re-checks the live connection's tenant against the plan. |
| **Security analysis is pure and capability-based** | Exchange roles and Entra permissions map onto one `MailboxCapability` lattice (ReadWrite ⇒ Read, EWS ⇒ everything, item APIs ⇒ mail/calendar/contacts). Overlap between tenant-wide Entra capabilities and requested RBAC capabilities defeats the scope. |
| **Fail closed** | Unverifiable security-critical checks block; an unwritable audit log prevents execution; an assignment with no recognisable scope is treated as organization-wide. |

## Execution engine (`ChangeExecutor`)

```
TryAcquire tenant lock ─► Connecting (session + live Get-ConnectionInformation tenant == plan tenant)
                       ─► Validating (no conflicts, fingerprint == validated, re-plan from live state == plan, audit "started")
                       ─► for each operation: Reuse | Dry run | Execute once ─► query resulting state ─► Succeeded / Failed (stop)
                       ─► Verifying (re-read every changed object)
                       ─► Completed | Partially completed | Failed | Dry run completed  (+ audit, inventory invalidated)
```

Stopping is honoured only between operations. After a partial failure, validating again plans only what is missing
(idempotency); *Remove objects created by this run* builds a compensation plan limited to objects created by that run
and not referenced by anything else.

## PowerShell host protocol

* Start: `pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ExchangeHost.ps1 -ProtocolMarker <random>`
  (arguments passed via `ArgumentList`, never concatenated).
* Request: `{"id":"…","command":"New-ManagementScope","parameters":{"Name":"…","RecipientRestrictionFilter":"…"}}`
* Response line: `<marker>{"id":"…","ok":true,"records":[…],"warnings":[…],"meta":{…},"error":null,"durationMs":…}`.
  Lines without the marker (banners, host output) are logged after redaction and ignored.
* Host operations: `Host.Hello`, `Host.ImportModule`, `Host.Connect` (adds `-ShowBanner:$false`, `-CommandName <needed
  cmdlets>`, `-DisableWAM` unless enabled), `Host.CommandAvailability`, `Host.GetApplicationRoles`,
  `Host.GetApplicationRoleAssignments`, `Host.GetScopeReferences`, `Host.PreviewRecipientFilter`, `Host.Shutdown`.
* Timeouts: reads 4 min, writes 5 min, interactive connect 10 min (cancel kills the host). Reads retry on throttling and
  network errors with backoff (honouring "retry after N seconds"); writes never.

## Microsoft Graph

`InteractiveBrowserCredential` (system browser, `http://localhost` redirect, `DisableAutomaticAuthentication` so no
browser appears mid-operation) with in-memory token cache; `GraphServiceClient` with the SDK's retry handler for 429/503.
Calls: `/me`, `/organization`, service principals (search with `$search` + `ConsistencyLevel: eventual`, by id, by
appId), `/applications/{id}` (only to detect App Registration IDs), `/servicePrincipals/{id}/appRoleAssignments` resolved
against each resource service principal's `appRoles` (cached per session).

## UI

WPF with WPF-UI (Fluent): Mica window, custom sidebar with keyboard-accessible list navigation, session bar, card-based
pages, status badges/glyphs with automation peers, light/dark/system themes, compact sidebar below 1180 px. Pages are
view-model-first (`DataTemplate` per view model); transient pages (application detail, wizard) are disposed on
navigation. All long-running work is asynchronous; errors become an inline panel with expandable sanitized diagnostics.
