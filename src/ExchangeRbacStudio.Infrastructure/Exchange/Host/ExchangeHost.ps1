#Requires -Version 7.2
<#
.SYNOPSIS
    Exchange RBAC Studio - isolated Exchange Online execution host.

.DESCRIPTION
    Long-running dispatcher started by Exchange RBAC Studio in a dedicated pwsh.exe process, so that the
    ExchangeOnlineManagement module (and its authentication libraries) never share a process with the
    application's own Microsoft Graph libraries.

    Protocol: one JSON request per line on stdin, one JSON response per line on stdout. Response lines are
    prefixed with a per-session marker; any other console output (banners, warnings written to the host) is
    ignored by the client.

    Security model:
      * Only commands in $Catalog (or the fixed Host.* operations below) can be invoked.
      * Every parameter must be declared for the command, with a declared type; anything else is rejected.
      * Parameters are bound by splatting. Request content is never evaluated as PowerShell code
        (no Invoke-Expression, no [scriptblock]::Create, no string-built command lines).
      * Requests are parsed with System.Text.Json, so values keep their JSON types (no implicit conversion).

    This file is part of the application and must not be modified by users.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9]{16,64}$')]
    [string] $ProtocolMarker
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

$MaxStringLength = 16384
$ModuleName = 'ExchangeOnlineManagement'
$AllowedEnvironments = @('O365Default', 'O365USGovGCCHigh', 'O365USGovDoD', 'O365China', 'O365GermanyCloud')

# Output projections: only these properties ever leave the host (data minimization).
$Projections = @{
    ServicePrincipal = @('Identity', 'Name', 'DisplayName', 'AppId', 'ObjectId', 'ServiceId', 'Id')
    ManagementScope  = @('Name', 'Identity', 'Guid', 'RecipientFilter', 'RecipientRoot', 'ScopeRestrictionType', 'Exclusive', 'WhenChangedUTC')
    RoleAssignment   = @('Name', 'Identity', 'Guid', 'Role', 'RoleAssignee', 'RoleAssigneeName', 'RoleAssigneeType', 'AssignmentMethod',
                         'CustomResourceScope', 'RecipientWriteScope', 'CustomRecipientWriteScope', 'RecipientAdministrativeUnitScope',
                         'RecipientGroupScope', 'ExclusiveRecipientWriteScope', 'Enabled', 'WhenCreatedUTC')
    Recipient        = @('Identity', 'Name', 'DisplayName', 'Alias', 'PrimarySmtpAddress', 'RecipientType', 'RecipientTypeDetails',
                         'ExchangeGuid', 'Guid', 'ExternalDirectoryObjectId')
    AuthorizationRow = @('RoleName', 'GrantedPermissions', 'AllowedResourceScope', 'ScopeType', 'InScope')
    AccessPolicy     = @('Identity', 'AppId', 'ScopeName', 'ScopeIdentity', 'AccessRight', 'Description', 'IsValid')
    ManagementRole   = @('Name', 'RoleType', 'Description', 'Identity')
    Connection       = @('State', 'TokenStatus', 'TenantID', 'UserPrincipalName', 'Organization', 'ConnectionUri', 'TokenExpiryTimeUTC', 'ModuleName', 'ConnectionId')
    Organization     = @('Name', 'DisplayName', 'Guid')
}

# Allowlisted Exchange Online cmdlets with their permitted parameters and parameter types.
$Catalog = @{
    'Get-ServicePrincipal'               = @{ Parameters = @{ Identity = 'String' }; Projection = 'ServicePrincipal' }
    'Get-ManagementScope'                = @{ Parameters = @{ Identity = 'String' }; Projection = 'ManagementScope' }
    'Get-ManagementRoleAssignment'       = @{ Parameters = @{ Identity = 'String'; Role = 'String'; RoleAssignee = 'String' }; Projection = 'RoleAssignment' }
    'Get-Recipient'                      = @{ Parameters = @{ Identity = 'String'; Anr = 'String'; ResultSize = 'String' }; Projection = 'Recipient' }
    'Test-ServicePrincipalAuthorization' = @{ Parameters = @{ Identity = 'String'; Resource = 'String' }; Projection = 'AuthorizationRow' }
    'Get-ApplicationAccessPolicy'        = @{ Parameters = @{ Identity = 'String' }; Projection = 'AccessPolicy' }
    'Get-OrganizationConfig'             = @{ Parameters = @{}; Projection = 'Organization' }

    'New-ServicePrincipal'               = @{ Parameters = @{ AppId = 'Guid'; ObjectId = 'Guid'; DisplayName = 'String' }; Projection = 'ServicePrincipal'; Write = $true }
    'Remove-ServicePrincipal'            = @{ Parameters = @{ Identity = 'String'; Confirm = 'Switch' }; Write = $true }
    'New-ManagementScope'                = @{ Parameters = @{ Name = 'String'; RecipientRestrictionFilter = 'String' }; Projection = 'ManagementScope'; Write = $true }
    'Set-ManagementScope'                = @{ Parameters = @{ Identity = 'String'; RecipientRestrictionFilter = 'String'; Confirm = 'Switch' }; Write = $true }
    'Remove-ManagementScope'             = @{ Parameters = @{ Identity = 'String'; Confirm = 'Switch' }; Write = $true }
    'New-ManagementRoleAssignment'       = @{ Parameters = @{ Name = 'String'; App = 'Guid'; Role = 'String'; CustomResourceScope = 'String' }; Projection = 'RoleAssignment'; Write = $true }
    'Set-ManagementRoleAssignment'       = @{ Parameters = @{ Identity = 'String'; CustomResourceScope = 'String'; Confirm = 'Switch' }; Write = $true }
    'Remove-ManagementRoleAssignment'    = @{ Parameters = @{ Identity = 'String'; Confirm = 'Switch' }; Write = $true }
}

# Fixed host operations implemented in this script.
$HostOperations = @{
    'Host.Hello'                         = @{}
    'Host.ImportModule'                  = @{ MinimumVersion = 'String' }
    'Host.Connect'                       = @{ UserPrincipalName = 'String'; ExchangeEnvironmentName = 'String'; UseWam = 'Switch'; CommandName = 'StringArray' }
    'Host.Disconnect'                    = @{}
    'Host.ConnectionInformation'         = @{}
    'Host.CommandAvailability'           = @{ CommandName = 'StringArray' }
    'Host.GetApplicationRoles'           = @{}
    'Host.GetApplicationRoleAssignments' = @{ Role = 'StringArray' }
    'Host.GetScopeReferences'            = @{ ScopeName = 'String' }
    'Host.PreviewRecipientFilter'        = @{ Filter = 'String'; ResultSize = 'Int' }
    'Host.Shutdown'                      = @{}
}

function New-HostError {
    param([Parameter(Mandatory)][string] $Kind, [Parameter(Mandatory)][string] $Message)
    $exception = [System.InvalidOperationException]::new($Message)
    return [System.Management.Automation.ErrorRecord]::new($exception, "ERS.$Kind", [System.Management.Automation.ErrorCategory]::InvalidOperation, $null)
}

function ConvertTo-WireValue {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return $Value }
    if ($Value -is [bool]) { return $Value }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) { return $Value }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture) }
    if ($Value -is [datetimeoffset]) { return $Value.UtcDateTime.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture) }
    if ($Value -is [guid]) { return $Value.ToString('D') }
    if ($Value -is [System.Collections.IEnumerable]) {
        $items = [System.Collections.Generic.List[string]]::new()
        foreach ($item in $Value) { if ($null -ne $item) { $items.Add([string]$item) } }
        return , $items.ToArray()
    }
    return [string]$Value
}

function ConvertTo-WireRecord {
    param([Parameter(Mandatory)] $InputObject, [Parameter(Mandatory)][string[]] $Properties)
    $record = [ordered]@{}
    foreach ($name in $Properties) {
        $property = $InputObject.PSObject.Properties[$name]
        if ($null -eq $property) { $record[$name] = $null }
        else { $record[$name] = ConvertTo-WireValue -Value $property.Value }
    }
    return $record
}

function ConvertTo-Records {
    param($Results, [string] $Projection)
    $records = [System.Collections.Generic.List[object]]::new()
    if ($null -eq $Results) { return , $records }
    foreach ($item in @($Results)) {
        if ($null -eq $item) { continue }
        if ([string]::IsNullOrEmpty($Projection)) { continue }
        $records.Add((ConvertTo-WireRecord -InputObject $item -Properties $Projections[$Projection]))
    }
    return , $records
}

function ConvertFrom-JsonParameters {
    param([hashtable] $Declared, $Element, [bool] $HasElement)
    $bound = @{}
    if (-not $HasElement) { return $bound }
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw (New-HostError 'InvalidRequest' 'Parameters must be a JSON object.') }

    foreach ($property in $Element.EnumerateObject()) {
        $name = $property.Name
        if (-not $Declared.ContainsKey($name)) { throw (New-HostError 'InvalidRequest' "Parameter '$name' is not allowed for this command.") }
        $value = $property.Value
        switch ($Declared[$name]) {
            'String' {
                if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw (New-HostError 'InvalidRequest' "Parameter '$name' must be a string.") }
                $text = $value.GetString()
                if ($text.Length -gt $MaxStringLength) { throw (New-HostError 'InvalidRequest' "Parameter '$name' exceeds the maximum length.") }
                $bound[$name] = $text
            }
            'Guid' {
                $parsed = [guid]::Empty
                if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or -not [guid]::TryParse($value.GetString(), [ref] $parsed)) {
                    throw (New-HostError 'InvalidRequest' "Parameter '$name' must be a GUID.")
                }
                $bound[$name] = $parsed.ToString('D')
            }
            'Switch' {
                if ($value.ValueKind -eq [System.Text.Json.JsonValueKind]::True) { $bound[$name] = $true }
                elseif ($value.ValueKind -eq [System.Text.Json.JsonValueKind]::False) { $bound[$name] = $false }
                else { throw (New-HostError 'InvalidRequest' "Parameter '$name' must be a boolean.") }
            }
            'Int' {
                $number = 0
                if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or -not $value.TryGetInt32([ref] $number)) {
                    throw (New-HostError 'InvalidRequest' "Parameter '$name' must be an integer.")
                }
                $bound[$name] = $number
            }
            'StringArray' {
                if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) { throw (New-HostError 'InvalidRequest' "Parameter '$name' must be an array of strings.") }
                $items = [System.Collections.Generic.List[string]]::new()
                foreach ($item in $value.EnumerateArray()) {
                    if ($item.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw (New-HostError 'InvalidRequest' "Parameter '$name' must contain only strings.") }
                    $items.Add($item.GetString())
                }
                $bound[$name] = $items.ToArray()
            }
            default { throw (New-HostError 'InvalidRequest' "Parameter '$name' has an unsupported declared type.") }
        }
    }
    return $bound
}

function Get-SessionCommand {
    param([Parameter(Mandatory)][string] $Name)
    $command = Get-Command -Name $Name -CommandType Function, Cmdlet -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        throw (New-HostError 'CommandNotAvailable' "The command '$Name' is not available in this Exchange Online session. Exchange only exposes cmdlets permitted by your role assignments; connect first, or activate the required role and reconnect.")
    }
    return $command
}

function Invoke-AllowlistedCommand {
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][hashtable] $Bound, [System.Collections.Generic.List[string]] $Warnings)
    $command = Get-SessionCommand -Name $Name
    $warningRecords = $null
    $results = & $command @Bound -ErrorAction Stop -WarningAction SilentlyContinue -WarningVariable warningRecords
    foreach ($w in @($warningRecords)) { if ($null -ne $w) { $Warnings.Add([string]$w) } }
    return , @($results)
}

function Test-ExchangeConnected {
    $connections = @(Get-ConnectionInformation -ErrorAction SilentlyContinue)
    return [bool]($connections | Where-Object { $_.State -eq 'Connected' } | Select-Object -First 1)
}

function Invoke-HostOperation {
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][hashtable] $Bound, [hashtable] $Meta, [System.Collections.Generic.List[string]] $Warnings)

    switch ($Name) {
        'Host.Hello' {
            $versions = @(Get-Module -ListAvailable -Name $ModuleName | Sort-Object Version -Descending | ForEach-Object { $_.Version.ToString() })
            return , @([pscustomobject]@{
                    PSVersion      = $PSVersionTable.PSVersion.ToString()
                    PSEdition      = [string]$PSVersionTable.PSEdition
                    ProcessId      = $PID
                    ModuleVersions = $versions
                })
        }
        'Host.ImportModule' {
            $minimum = [version]'3.0.0'
            if ($Bound.ContainsKey('MinimumVersion') -and -not [version]::TryParse($Bound.MinimumVersion, [ref] $minimum)) {
                throw (New-HostError 'InvalidRequest' 'MinimumVersion is not a valid version.')
            }
            $available = @(Get-Module -ListAvailable -Name $ModuleName | Sort-Object Version -Descending)
            if ($available.Count -eq 0) {
                throw (New-HostError 'ModuleNotFound' "The $ModuleName module is not installed for PowerShell 7. Install it with: Install-Module $ModuleName -Scope CurrentUser")
            }
            $candidate = $available | Where-Object { $_.Version -ge $minimum } | Select-Object -First 1
            if ($null -eq $candidate) {
                throw (New-HostError 'ModuleVersion' "$ModuleName $($available[0].Version) is installed, but version $minimum or later is required. Update it with: Update-Module $ModuleName")
            }
            $loaded = Get-Module -Name $ModuleName
            if ($null -eq $loaded) { $loaded = Import-Module -ModuleInfo $candidate -PassThru -DisableNameChecking -ErrorAction Stop -WarningAction SilentlyContinue }
            return , @([pscustomobject]@{ Version = $loaded.Version.ToString(); ModuleBase = $loaded.ModuleBase })
        }
        'Host.Connect' {
            $connect = Get-Command -Name 'Connect-ExchangeOnline' -ErrorAction SilentlyContinue
            if ($null -eq $connect) { throw (New-HostError 'ModuleNotFound' "$ModuleName is not loaded.") }
            if ($Bound.ContainsKey('ExchangeEnvironmentName') -and $Bound.ExchangeEnvironmentName -notin $AllowedEnvironments) {
                throw (New-HostError 'InvalidRequest' 'Unsupported Exchange environment.')
            }

            $splat = @{ ShowBanner = $false; ErrorAction = 'Stop' }
            if ($Bound.ContainsKey('UserPrincipalName') -and -not [string]::IsNullOrWhiteSpace($Bound.UserPrincipalName)) { $splat.UserPrincipalName = $Bound.UserPrincipalName }
            if ($Bound.ContainsKey('ExchangeEnvironmentName')) { $splat.ExchangeEnvironmentName = $Bound.ExchangeEnvironmentName }
            if ($Bound.ContainsKey('CommandName') -and $Bound.CommandName.Count -gt 0) { $splat.CommandName = $Bound.CommandName }
            if ($connect.Parameters.ContainsKey('SkipLoadingFormatData')) { $splat.SkipLoadingFormatData = $true }
            if ($connect.Parameters.ContainsKey('SkipLoadingCmdletHelp')) { $splat.SkipLoadingCmdletHelp = $true }
            $useWam = $Bound.ContainsKey('UseWam') -and $Bound.UseWam
            if (-not $useWam -and $connect.Parameters.ContainsKey('DisableWAM')) { $splat.DisableWAM = $true }

            if (Test-ExchangeConnected) { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null }
            Connect-ExchangeOnline @splat -WarningAction SilentlyContinue | Out-Null
            return , @(Get-ConnectionInformation -ErrorAction Stop | Where-Object { $_.State -eq 'Connected' } | Select-Object -First 1)
        }
        'Host.Disconnect' {
            if ((Get-Command -Name 'Disconnect-ExchangeOnline' -ErrorAction SilentlyContinue) -and (Test-ExchangeConnected)) {
                Disconnect-ExchangeOnline -Confirm:$false -ErrorAction Stop | Out-Null
            }
            return , @()
        }
        'Host.ConnectionInformation' {
            if (-not (Get-Command -Name 'Get-ConnectionInformation' -ErrorAction SilentlyContinue)) { return , @() }
            return , @(Get-ConnectionInformation -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Connected' })
        }
        'Host.CommandAvailability' {
            $rows = foreach ($commandName in $Bound.CommandName) {
                [pscustomobject]@{ Name = $commandName; Available = [bool](Get-Command -Name $commandName -CommandType Function, Cmdlet -ErrorAction SilentlyContinue) }
            }
            return , @($rows)
        }
        'Host.GetApplicationRoles' {
            $command = Get-SessionCommand -Name 'Get-ManagementRole'
            return , @(& $command -ErrorAction Stop -WarningAction SilentlyContinue | Where-Object { $_.Name -like 'Application *' })
        }
        'Host.GetApplicationRoleAssignments' {
            $command = Get-SessionCommand -Name 'Get-ManagementRoleAssignment'
            $all = [System.Collections.Generic.List[object]]::new()
            foreach ($role in $Bound.Role) {
                try {
                    foreach ($assignment in @(& $command -Role $role -ErrorAction Stop -WarningAction SilentlyContinue)) {
                        if ([string]$assignment.RoleAssigneeType -eq 'ServicePrincipal') { $all.Add($assignment) }
                    }
                }
                catch {
                    if ($_.Exception.Message -match "couldn't be found|could not be found|not found") { $Warnings.Add("Role '$role' was not found.") }
                    else { throw }
                }
            }
            return , $all.ToArray()
        }
        'Host.GetScopeReferences' {
            $command = Get-SessionCommand -Name 'Get-ManagementRoleAssignment'
            $scopeName = $Bound.ScopeName
            $scopeProperties = @('CustomResourceScope', 'CustomRecipientWriteScope', 'ExclusiveRecipientWriteScope', 'CustomConfigWriteScope', 'ExclusiveConfigWriteScope', 'RecipientGroupScope')
            $references = [System.Collections.Generic.List[object]]::new()
            foreach ($assignment in @(& $command -ErrorAction Stop -WarningAction SilentlyContinue)) {
                foreach ($propertyName in $scopeProperties) {
                    $property = $assignment.PSObject.Properties[$propertyName]
                    if ($null -eq $property -or $null -eq $property.Value) { continue }
                    $value = [string]$property.Value
                    if ($value -eq $scopeName -or $value.EndsWith("\$scopeName", [System.StringComparison]::OrdinalIgnoreCase)) {
                        $assignee = if ($assignment.RoleAssigneeName) { [string]$assignment.RoleAssigneeName } else { [string]$assignment.RoleAssignee }
                        $references.Add([pscustomobject]@{
                                AssignmentName      = [string]$assignment.Name
                                Role                = [string]$assignment.Role
                                RoleAssigneeName    = $assignee
                                RoleAssigneeType    = [string]$assignment.RoleAssigneeType
                                ReferencingProperty = $propertyName
                            })
                        break
                    }
                }
            }
            return , $references.ToArray()
        }
        'Host.PreviewRecipientFilter' {
            $command = Get-SessionCommand -Name 'Get-Recipient'
            $resultSize = if ($Bound.ContainsKey('ResultSize')) { [Math]::Max(1, [Math]::Min(5000, $Bound.ResultSize)) } else { 1000 }
            $results = @(& $command -RecipientPreviewFilter $Bound.Filter -ResultSize $resultSize -ErrorAction Stop -WarningAction SilentlyContinue)
            $Meta['truncated'] = ($results.Count -ge $resultSize)
            return , $results
        }
        'Host.Shutdown' {
            try {
                if ((Get-Command -Name 'Disconnect-ExchangeOnline' -ErrorAction SilentlyContinue) -and (Test-ExchangeConnected)) {
                    Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
                }
            }
            catch { }
            $script:ShutdownRequested = $true
            return , @()
        }
    }
}

function Get-HostProjection {
    param([string] $Name)
    switch ($Name) {
        'Host.Hello' { return $null }
        'Host.ImportModule' { return $null }
        'Host.Connect' { return 'Connection' }
        'Host.ConnectionInformation' { return 'Connection' }
        'Host.GetApplicationRoles' { return 'ManagementRole' }
        'Host.GetApplicationRoleAssignments' { return 'RoleAssignment' }
        'Host.PreviewRecipientFilter' { return 'Recipient' }
        default { return $null }
    }
}

function ConvertTo-PlainRecords {
    param($Results)
    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($item in @($Results)) {
        if ($null -eq $item) { continue }
        $record = [ordered]@{}
        foreach ($property in $item.PSObject.Properties) { $record[$property.Name] = ConvertTo-WireValue -Value $property.Value }
        $records.Add($record)
    }
    return , $records
}

function New-ErrorPayload {
    param([Parameter(Mandatory)][System.Management.Automation.ErrorRecord] $ErrorRecord)
    $exception = $ErrorRecord.Exception
    $kind = $null
    if ($ErrorRecord.FullyQualifiedErrorId -like 'ERS.*') { $kind = ($ErrorRecord.FullyQualifiedErrorId -split '[.,]')[1] }
    return [ordered]@{
        kind          = $kind
        message       = [string]$exception.Message
        errorId       = [string]$ErrorRecord.FullyQualifiedErrorId
        category      = [string]$ErrorRecord.CategoryInfo.Category
        reason        = [string]$ErrorRecord.CategoryInfo.Reason
        exceptionType = $exception.GetType().FullName
        targetObject  = if ($null -ne $ErrorRecord.TargetObject) { [string]$ErrorRecord.TargetObject } else { $null }
        innerMessage  = if ($null -ne $exception.InnerException) { [string]$exception.InnerException.Message } else { $null }
    }
}

function Write-Response {
    param([hashtable] $Response)
    $json = ConvertTo-Json -InputObject $Response -Depth 8 -Compress -WarningAction SilentlyContinue
    [Console]::Out.WriteLine($ProtocolMarker + $json)
    [Console]::Out.Flush()
}

function Invoke-Request {
    param([string] $Line)

    $id = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $meta = @{}
    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($Line)
        $root = $document.RootElement
        $commandName = $null
        $parametersElement = $null
        $hasParameters = $false
        foreach ($property in $root.EnumerateObject()) {
            switch ($property.Name) {
                'id' { $id = $property.Value.GetString() }
                'command' { $commandName = $property.Value.GetString() }
                'parameters' { $parametersElement = $property.Value; $hasParameters = $true }
            }
        }

        if ([string]::IsNullOrWhiteSpace($commandName)) { throw (New-HostError 'InvalidRequest' 'Missing command.') }

        if ($HostOperations.ContainsKey($commandName)) {
            $bound = ConvertFrom-JsonParameters -Declared $HostOperations[$commandName] -Element $parametersElement -HasElement $hasParameters
            $results = Invoke-HostOperation -Name $commandName -Bound $bound -Meta $meta -Warnings $warnings
            $projection = Get-HostProjection -Name $commandName
            $records = if ($null -ne $projection) { ConvertTo-Records -Results $results -Projection $projection } else { ConvertTo-PlainRecords -Results $results }
        }
        elseif ($Catalog.ContainsKey($commandName)) {
            $entry = $Catalog[$commandName]
            $bound = ConvertFrom-JsonParameters -Declared $entry.Parameters -Element $parametersElement -HasElement $hasParameters
            $results = Invoke-AllowlistedCommand -Name $commandName -Bound $bound -Warnings $warnings
            $records = ConvertTo-Records -Results $results -Projection $entry.Projection
            $meta['count'] = @($results).Count
        }
        else {
            throw (New-HostError 'InvalidRequest' "Command '$commandName' is not allowed.")
        }

        Write-Response @{ id = $id; ok = $true; records = $records; warnings = $warnings; meta = $meta; error = $null; durationMs = $stopwatch.ElapsedMilliseconds }
    }
    catch {
        Write-Response @{ id = $id; ok = $false; records = @(); warnings = $warnings; meta = $meta; error = (New-ErrorPayload -ErrorRecord $_); durationMs = $stopwatch.ElapsedMilliseconds }
    }
    finally {
        if ($null -ne $document) { $document.Dispose() }
    }
}

$script:ShutdownRequested = $false
while (-not $script:ShutdownRequested) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    Invoke-Request -Line $line
}

try {
    if ((Get-Command -Name 'Disconnect-ExchangeOnline' -ErrorAction SilentlyContinue) -and (Test-ExchangeConnected)) {
        Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    }
}
catch { }
