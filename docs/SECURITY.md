# Security considerations

Exchange RBAC Studio is privileged administration software: it changes who can read and send mail. This document lists
the threats considered and the controls implemented.

## Reporting a vulnerability

Please report security issues privately through GitHub: **Security → Report a vulnerability** on the
[repository page](https://github.com/maikelmoonen-bit/exchange-rbac-studio/security). Do not open a public issue, and
never include tenant data, tokens or logs that have not been redacted.

## Authentication and authorization

* Interactive sign-in only (system browser for Graph; ExchangeOnlineManagement's own sign-in for Exchange). MFA and
  Conditional Access apply. The application never asks for a password.
* Delegated Graph permissions are limited to `User.Read` and `Application.Read.All` (read-only).
* Tokens live only in memory (Azure.Identity cache for Graph; the module's own cache inside the isolated host process)
  and are discarded on disconnect or exit. Nothing is persisted; nothing is logged.
* Effective Exchange privileges are discovered per session (the module only exposes permitted cmdlets). Missing
  privileges block execution with PIM guidance; *Reconnect* re-evaluates them after activation.

## Tenant isolation

* Graph and Exchange must be signed in to the same tenant; a mismatch discards the second sign-in.
* Every plan carries its tenant ID; execution re-reads the live Exchange connection and refuses a different tenant.
* Cached data is keyed to a session ID and dropped whenever the session or tenant changes. Confirmation screens always
  show the tenant name and ID.

## Injection resistance

* No user-supplied value is ever evaluated as PowerShell: the host binds typed parameters by splatting from an allowlist.
  Unknown commands or parameters, wrong types and oversize strings are rejected (tested against real PowerShell 7).
* Scope filters contain only GUIDs formatted by the application. Names are restricted to `[A-Za-z0-9 _.-]`, ≤ 64 chars.
* Search terms and identities are validated at the boundary (length, control characters, wildcards).
* Report HTML encodes every value; the preview quoting never produces expandable strings.

## Change safety

* Read-only discovery, preflight, preview and dry run never call a write cmdlet.
* Execution requires an explicitly validated plan, a fingerprint match, a live re-plan match, a tenant confirmation and
  (for removals, by default) typing the object name.
* Writes are executed once and verified by reading back; no blind retries; partial results are reported truthfully.
* A machine-wide lock per tenant prevents concurrent executions from multiple instances.
* Shared scopes are never modified implicitly; scopes referenced by any role assignment (application or administrator)
  cannot be removed; compensation only removes objects created by the failed run.
* Existing Entra ID permissions are never revoked automatically; remediation is described instead.

## Security verdict

A configuration is reported as *securely scoped* only when every security-critical preflight check passed, execution
completed, positive authorization tests passed, and no tenant-wide Entra grant, privilege-escalation permission or
organization-wide Exchange assignment overlaps the requested capabilities. Unverifiable checks never count as passed.

## Data handling

* Only the recipient properties needed are retrieved (name, addresses, type, GUIDs).
* Logs pass through a redactor (JWTs, bearer/basic credentials, `client_secret=`, password-style keys, JSON token fields)
  and are retained 14 days. Diagnostic exports contain redacted logs and an environment summary only.
* The audit log records administrator, tenant, action, target, outcome and the command preview. It is hash-chained so
  modification or deletion of earlier entries is detectable (**Change History → Verify integrity**).
* Reports contain identifiers, outcomes and findings; never secrets or tokens (redacted again on export).

## Operational recommendations

* Use your own app registration as the Graph sign-in client and restrict who can use it (assignment required, Conditional Access).
* Install per-machine (MSI) so the program folder and `ExchangeHost.ps1` are protected from modification by users.
* Sign `ExchangeHost.ps1` and the binaries with your code-signing certificate if your environment requires it.
* Validate changes in a test tenant first, and complete the end-to-end verification in the report before removing
  tenant-wide Entra permissions.
