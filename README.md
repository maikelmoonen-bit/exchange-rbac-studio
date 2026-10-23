# Exchange RBAC Studio

[![CI](https://github.com/maikelmoonen-bit/exchange-rbac-studio/actions/workflows/ci.yml/badge.svg)](https://github.com/maikelmoonen-bit/exchange-rbac-studio/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> [!WARNING]
> This release has not yet been verified end to end against a live Microsoft 365 tenant. It changes who can read and
> send mail, so validate it in a dedicated test tenant first — see [Known limitations](#known-limitations-and-unverified-integrations)
> and the [integration test procedure](docs/TESTING.md#integration-testing-in-an-authorized-test-tenant).

A Windows desktop application for administering **Exchange Online RBAC for Applications**: granting an Entra ID
application access to *specific* mailboxes through Exchange management scopes and application role assignments, instead
of tenant-wide Microsoft Graph permissions.

It replaces the manual sequence of `New-ServicePrincipal`, `New-ManagementScope`, `New-ManagementRoleAssignment` and
`Test-ServicePrincipalAuthorization` with a guided, validated and audited workflow. The PowerShell layer stays
transparent: every change is previewed as the exact command that will run, and recorded in a hash-chained audit log.

| Area | What it does |
|---|---|
| **Configure scoped access** (wizard) | Select app → select mailboxes → select roles → security preflight → exact-identity scope → review & validate → confirmed execution → authorization tests and report |
| **Applications** | Every app with an Exchange service principal pointer, its roles, scopes, Entra permissions, warnings and an effective-access matrix |
| **Management Scopes** | Filters, risk analysis, matching recipients, linked assignments, dependency-checked removal |
| **Role Assignments** | All application role assignments; rescoping and removal with before/after review |
| **Authorization Tester** | Positive and negative `Test-ServicePrincipalAuthorization` tests, evaluated per role and combined with Entra grants |
| **Change History** | Local tamper-evident audit log, integrity verification and generated reports |

---

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 (19041+) or Windows 11, x64 | The packaged builds are self-contained; no .NET runtime is needed. |
| **PowerShell 7.4+** (`pwsh.exe`) | `winget install Microsoft.PowerShell`. Windows PowerShell 5.1 is not supported. |
| **ExchangeOnlineManagement 3.7.0+** for PowerShell 7 | `Install-Module ExchangeOnlineManagement -Scope CurrentUser` |
| Exchange permissions | Membership of **Organization Management** (holds the delegating assignment for application roles), or the **Exchange Administrator** Entra role. Missing cmdlets are detected and reported per session. |
| Entra ID permissions | A role that can read application role assignments, e.g. **Directory Readers**, **Global Reader** or **Cloud Application Administrator**. Without it the security check that detects tenant-wide grants cannot complete and execution is blocked. |
| Graph sign-in client | Delegated **User.Read** and **Application.Read.All** with admin consent — see [Sign-in client](#sign-in-client). |

Privileged Identity Management is supported: activate the role, then choose **Reconnect** in the session bar (or
**Reconnect & re-run** in the wizard). Exchange only exposes cmdlets your *current* roles allow, so a reconnect is needed
after activation.

## Install

Download the MSI or the portable zip from the [Releases](https://github.com/maikelmoonen-bit/exchange-rbac-studio/releases)
page, or [build from source](#build-from-source).

* **MSI** — `ExchangeRbacStudio-<version>-win-x64.msi` installs per-machine into `Program Files\Exchange RBAC Studio`
  (administrator-protected, which also protects the PowerShell host script) and adds a Start menu shortcut.
* **Portable** — extract `ExchangeRbacStudio-<version>-win-x64-portable.zip` and run `ExchangeRbacStudio.exe`.

Neither package installs PowerShell 7 or the ExchangeOnlineManagement module.

## Build from source

```powershell
# .NET 10 SDK required (a user-local install via dotnet-install.ps1 works; build.ps1 finds it)
./build.ps1                       # restore, build, test, publish, zip, MSI  → .\artifacts
./build.ps1 -SkipTests -SkipInstaller
./build.ps1 -UiTests              # additionally drives the real app with UI Automation (takes over the desktop)
```

Or individually: `dotnet build ExchangeRbacStudio.slnx`, `dotnet test --project tests\ExchangeRbacStudio.Tests\ExchangeRbacStudio.Tests.csproj`.
The repository pins the SDK and the Microsoft.Testing.Platform runner in `global.json`.

### Continuous integration and releases

Every push to `main` and every pull request runs the full `build.ps1` pipeline on a GitHub-hosted Windows runner
([ci.yml](.github/workflows/ci.yml)). Pushing a version tag builds, tests and packages that version and creates a
**draft** GitHub release with the MSI and the portable zip ([release.yml](.github/workflows/release.yml)); review the
draft before publishing it.

```powershell
git tag v1.0.1
git push origin v1.0.1
```

## Configuration

Settings are stored in `%APPDATA%\ExchangeRbacStudio\settings.json`. No credentials or tokens are ever stored.

| Setting | Default | Purpose |
|---|---|---|
| Environment | Commercial / GCC | Also GCC High, DoD and 21Vianet; selects Graph, authority and `-ExchangeEnvironmentName` |
| Tenant | (home tenant) | Tenant ID or verified domain used as sign-in authority (needed for B2B/partner admins) |
| Graph sign-in client | *chosen at first connect* | See below |
| Exchange sign-in uses WAM | Off | WAM enables device-bound Conditional Access; off by default because WAM needs a window handle the background host lacks |
| PowerShell path | auto-detect | Explicit `pwsh.exe` |
| Minimum module version | 3.7.0 | Older ExchangeOnlineManagement versions are refused with an update instruction |
| Scope / assignment name templates | `SCOPE_APP_{APP}_{CATEGORY}` / `RA_{APP}_{ROLE}` | Tokens `{APP} {APPID} {APPID8} {CATEGORY} {ROLE} {COUNT} {DATE}`; sanitized, ≤ 64 chars, made unique |
| Filter strategy | Entra object ID | `ExternalDirectoryObjectId`, `Guid` or `ExchangeGuid` equality (see [Why not PrimarySmtpAddress](#differences-from-the-usual-manual-workflow)) |
| Max mailboxes per scope | 50 | Larger sets should use administrative units (not created by this release) |
| Typed confirmation for removals | On | Removals additionally require typing the object name |

### Sign-in client

Microsoft Graph sign-in uses a public client (interactive browser, MFA and Conditional Access capable). On first
connect you choose:

1. **Microsoft Graph Command Line Tools** (`14d82eec-204b-4c2f-b7e8-296a70dab67e`) — the Microsoft-published client used
   by `Connect-MgGraph`. Works where it already has consent for `User.Read` and `Application.Read.All`.
2. **Your own app registration (recommended for production)** — register a single-tenant app with platform
   *Mobile and desktop applications*, redirect URI `http://localhost`, *Allow public client flows* = Yes, delegated
   Microsoft Graph permissions `User.Read` and `Application.Read.All`, then grant admin consent. Enter its Application
   (client) ID.

Exchange Online sign-in is performed by the ExchangeOnlineManagement module itself (`Connect-ExchangeOnline`), with the
Graph account as login hint. The two sessions are verified to belong to the same tenant.

### Data locations

| Data | Location |
|---|---|
| Logs (JSON, tokens redacted, 14 days) | `%LOCALAPPDATA%\ExchangeRbacStudio\Logs` |
| Audit log (JSON Lines, SHA-256 chained) | `%LOCALAPPDATA%\ExchangeRbacStudio\Audit` |
| Reports (JSON + HTML) | `Documents\Exchange RBAC Studio\Reports` |
| Settings | `%APPDATA%\ExchangeRbacStudio\settings.json` |

## Using the application

1. **Connect** (session bar). Microsoft Entra ID and Exchange Online are shown and handled separately; a Graph sign-in
   never implies an Exchange session.
2. **Configure scoped access** (sidebar button or `Ctrl+N`):
   1. *Select application* by name, client ID or service principal object ID. Pasting an **App Registration** object ID
      is detected and explained.
   2. *Select mailboxes* — search or paste a list. Only `UserMailbox` and `SharedMailbox` are accepted; every recipient
      is resolved and its real type validated.
   3. *Select permissions* — roles discovered in the tenant, with capabilities, API permissions, privilege level and
      least-privilege advice.
   4. *Security validation* — read-only preflight (session/tenant, privileges, identifiers, pointer, existing and
      overlapping assignments, **tenant-wide Entra grants**, scope precision, conflicts). Results are *Passed*,
      *Warning*, *Blocked* or *Unable to verify*. Unverifiable security-critical checks block execution.
   5. *Generate scope* — name (editable, collision-checked) and exact-identity filter, verified by Exchange to match
      exactly the selection.
   6. *Review change* — everything that will change, before/after, and the PowerShell preview. **Validate configuration**
      re-runs all checks against the live tenant and locks the plan (fingerprint).
   7. *Execute* — explicit confirmation of the target tenant; live, per-step progress; optional dry run.
   8. *Verify & report* — positive tests for every intended mailbox, optional negative tests, verdict and report.
3. **Manage existing configuration** from Applications (modify / add / remove), Management Scopes and Role Assignments.
   Every change goes through the same review → confirm → execute → verify pipeline.

Keyboard: `Ctrl+1`…`Ctrl+7` navigate, `Ctrl+N` starts the wizard, `F5` refreshes the current page.

## Differences from the usual manual workflow

The commonly used manual workflow was checked against Microsoft documentation (application-rbac, New-ManagementScope,
recipientfilter-properties, Test-ServicePrincipalAuthorization, New/Set/Get-ManagementRoleAssignment, Graph
appRoleAssignments). Where it differed, the implementation follows the documentation:

* **`PrimarySmtpAddress -eq` is not used.** Microsoft states that filters on PrimarySmtpAddress also match proxy
  addresses, and addresses can be reassigned. Scopes match immutable identifiers (Entra object ID by default) and are
  accepted only if `Get-Recipient -RecipientPreviewFilter` resolves them to exactly the selected mailboxes; post-change
  `Test-ServicePrincipalAuthorization` tests confirm it.
* **The role list is longer than the example.** Exchange documents 24 application roles (including MailboxItem,
  MailboxFolder, MailboxConfigItem, SMTP.SendAsApp, EWS.AccessAsApp and two full-access bundles). Roles are discovered
  per tenant with `Get-ManagementRole`; unknown roles are shown as privileged and uncurated.
* **`Get-ManagementRoleAssignment -RoleAssigneeType` does not accept `ServicePrincipal`**, so application assignments are
  enumerated per application role and filtered on `RoleAssigneeType`.
* **Exclusive management scopes do not restrict app access** (documented limitation), so they are never used.
* **Application Access Policies constrain only Entra-granted permissions**; they are reported but never treated as
  restricting RBAC assignments, and never as making a tenant-wide grant safe.
* **`Test-ServicePrincipalAuthorization` ignores Entra grants and bypasses the authorization cache**; real API results
  can lag 30 minutes to 2 hours. Both facts are stated in the UI and the report, and Entra grants are evaluated
  separately.
* **Changing an assignment's scope** uses `Set-ManagementRoleAssignment -CustomResourceScope`; changing a shared scope's
  filter would change other assignments too, so it is refused unless the scope is used only by the application.

## Known limitations and unverified integrations

* **Live Microsoft 365 connectivity has not been exercised against a real tenant in the build environment.** The Graph
  adapter, the PowerShell host and every write path are implemented against the documented APIs and cmdlets and tested
  through realistic fakes; the PowerShell host itself is tested against real PowerShell 7 and the installed
  ExchangeOnlineManagement module (without connecting). Follow [docs/TESTING.md](docs/TESTING.md#integration-testing-in-an-authorized-test-tenant)
  before production use.
* Output property names used for some assignment scope details (`CustomResourceScope`, `RecipientAdministrativeUnitScope`,
  `RecipientGroupScope`) are read defensively; an assignment without a recognised scope is treated as organization-wide
  (fail safe) and the Authorization Tester shows the authoritative scope type.
* Recipient preview (`-RecipientPreviewFilter`) is used to *simulate* scope membership; exact-identity filters make the
  simulation and Exchange's own evaluation equivalent in practice, and the post-change tests verify it. Preview does not
  apply a scope's `RecipientRoot`, which is flagged.
* Creating administrative-unit or group-scoped assignments and broad (attribute/wildcard) scopes is intentionally not
  supported in this release; such existing configuration is displayed, analysed, tested and can be removed or rescoped.
* Sign-in tokens are kept in memory only, so each start requires sign-in.
* The installer, executable and `ExchangeHost.ps1` are not code-signed. Where Group Policy enforces `AllSigned`, sign
  `ExchangeHost.ps1` with your organization's certificate.
* The audit log is local and tamper-evident (hash chain), not tamper-proof.
* Desktop UI tests are opt-in (`ERS_UI_TESTS=1`) because they drive the real desktop. Keyboard tests skip with a reason
  where Windows denies synthetic input (locked, remote or non-foreground sessions); WPF-level keyboard tests always run.

## Documentation

* [Architecture](docs/ARCHITECTURE.md) — layers, execution engine, PowerShell host protocol, design decisions
* [Security](docs/SECURITY.md) — threat model and controls
* [Testing](docs/TESTING.md) — automated tests, coverage map, integration testing procedure

## License

[MIT](LICENSE) © 2026 Maikel Moonen. Provided without warranty; you are responsible for the changes you make to your
tenant.
