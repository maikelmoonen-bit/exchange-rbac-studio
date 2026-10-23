# Testing

## Test projects

| Project | Runs by default | What it covers |
|---|---|---|
| `tests/ExchangeRbacStudio.Tests` | Yes | Domain rules, application services and execution engine against realistic fakes; infrastructure (audit log, settings, lock, reports, error classification, record mapping); **real PowerShell 7 host** protocol tests (no tenant contacted) |
| `tests/ExchangeRbacStudio.App.Tests` | Yes | Every page, dialog and wizard step rendered with the real resources in light/dark and at compact width with **zero binding errors**; end-to-end wizard over fakes; view-model gating; keyboard shortcuts and tab order; every icon has a renderable glyph |
| `tests/ExchangeRbacStudio.UiTests` | Opt-in `ERS_UI_TESTS=1` | FlaUI drives the built executable: launch, session bar, all navigation, theme, resize, Connect dialog open/close, wizard connection gate, keyboard (skipped where synthetic input is not permitted) |
| `tests/ExchangeRbacStudio.TestSupport` | — | Fakes: in-memory Exchange model with real create/reuse/conflict semantics, failure injection, filter evaluation and `Test-ServicePrincipalAuthorization` rows; fake Graph; fixtures for a small tenant |

```powershell
dotnet test --project tests\ExchangeRbacStudio.Tests\ExchangeRbacStudio.Tests.csproj
dotnet test --project tests\ExchangeRbacStudio.App.Tests\ExchangeRbacStudio.App.Tests.csproj
$env:ERS_UI_TESTS = '1'; dotnet test --project tests\ExchangeRbacStudio.UiTests\ExchangeRbacStudio.UiTests.csproj
$env:ERS_SNAPSHOT_DIR = 'C:\temp\shots'   # optional: render page screenshots over fixtures
```

## Coverage of the required areas

| Area | Representative tests |
|---|---|
| Service principal identity validation | `ServicePrincipalIdentityValidationTests` (deleted SP, AppId conflict, pointer mismatch, App Registration ID detection) |
| Mailbox recipient-type validation | `RecipientSupportTests`, `MailboxValidationServiceTests`, wizard picker test |
| Permission mapping | `PermissionMappingTests` (24 roles, lattice, Mail.ReadWrite ⊅ Mail.Send, discovered/unknown roles, Entra reach) |
| Organization-wide grant detection | `OrganizationWideGrantDetectionTests`, `PreflightTests.Tenant_wide_entra_grant…`, `ReportBuilderTests.Undermining…` |
| Scope filter generation | `ScopeFilterGenerationTests`, `ScopeNamingTests` |
| Scope matching and overmatching | `ScopeMatchingTests`, `ScopeDesignerTests`, `PreflightTests.Overmatching_scope_is_rejected` |
| Duplicate assignment detection | `DuplicateAssignmentDetectionTests`, `IdempotentConfigurationTests` |
| Idempotent configuration | `IdempotentConfigurationTests.Executing_the_same_configuration_twice…` |
| Positive / negative authorization evaluation | `AuthorizationEvaluationTests`, `AuthorizationTestServiceTests` |
| Partial execution recovery | `ChangeExecutionTests.Partial_execution_is_recovered…`, `ManagementPlanTests.Compensation…` |
| Error handling and session expiry | `ExchangeErrorClassifierTests`, `SessionTests`, `ChangeExecutionTests.Expired_session…`, drift, lock, audit fail-closed |
| UI: wizard navigation, validation, search, dialogs, theme, resize, keyboard, responsiveness | `WizardWorkflowTests`, `ComponentViewModelTests`, `ViewRenderingTests`, `KeyboardTests`, `DesktopUiTests` |

Mocked tests prove the logic, not the live connection. The integration procedure below is required to verify the
Microsoft 365 integrations.

## Integration testing in an authorized test tenant

Use a **dedicated, non-production tenant** (for example a Microsoft 365 developer tenant) and an administrator account
that is explicitly authorized for testing. Never run write steps against production.

### Preparation

1. In the test tenant create: an app registration + Enterprise Application *ERS Test App* with **no** application
   permissions; two user mailboxes (A, B) and one shared mailbox (S); one distribution group (DL).
2. Install PowerShell 7.4+ and ExchangeOnlineManagement 3.7+; build or install the application.
3. Choose the sign-in client and connect. Confirm both status badges show the test tenant and the same tenant ID.

### Read-only verification

| # | Action | Expected |
|---|---|---|
| R1 | Dashboard → Refresh | Counts equal `(Get-ServicePrincipal).Count`, `(Get-ManagementScope).Count` and the application role assignments in a manual PowerShell session |
| R2 | Wizard step 1: search *ERS Test App*, then paste its App Registration object ID | Found; the App Registration ID is explained and mapped to the service principal |
| R3 | Step 2: add A, S; try DL | A and S validated; DL rejected with explanation |
| R4 | Steps 3–5 with *Application Mail.Read* | All preflight checks pass; scope verified with exactly A and S |
| R5 | Step 6 → Validate configuration | Preview lists `New-ServicePrincipal`, `New-ManagementScope`, `New-ManagementRoleAssignment`; **no change in the tenant yet** (check with `Get-ManagementScope`) |
| R6 | Step 7 → Dry run | Dry run completed; still no change in the tenant |

### Write verification (test tenant only)

| # | Action | Expected |
|---|---|---|
| W1 | Step 7 → confirm → Execute | Completed; `Get-ServicePrincipal`, `Get-ManagementScope`, `Get-ManagementRoleAssignment -Role "Application Mail.Read"` show the objects |
| W2 | Step 8 with B as negative test | A and S in scope, B out of scope; report saved (JSON + HTML) |
| W3 | Run the wizard again with the same input | Validation reports no changes required; executing changes nothing |
| W4 | Grant *Mail.Read* (application) to the app in Entra ID, re-run preflight | Entra check **Blocked**, verdict *not securely scoped*, negative test fails |
| W5 | Remove the Entra grant; End-to-end: acquire an app-only token for the app and call `GET /users/{A}/messages?$top=1` and `/users/{B}/messages?$top=1` | A succeeds, B returns 403 (allow up to 2 hours for the authorization cache) |
| W6 | Applications → *Polo/ERS Test App* → Modify access: add B, remove nothing | Plan shows `Set-ManagementScope` with before/after filter; executes and verifies |
| W7 | Role Assignments → Remove the assignment (type its name) | Removed; Management Scopes → Remove the now-unused scope succeeds; removing a scope still in use is refused |
| W8 | Disconnect, sign in to a *different* tenant | No data from the first tenant is shown; the wizard resets |
| W9 | Change History → Verify integrity | Intact; every step above appears with administrator and outcome |

### Failure scenarios worth exercising

* Remove the Exchange role (PIM deactivate) → preflight blocks with PIM guidance; activate → *Reconnect & re-run* passes.
* Disconnect the network during a read → error panel with diagnostics, application stays responsive.
* Close the sign-in browser window → sign-in cancelled cleanly; *Cancel sign-in* stops the Exchange host.
* Make a conflicting change from another PowerShell session between *Validate* and *Execute* → execution aborts with
  "configuration changed after it was validated" and no changes.
