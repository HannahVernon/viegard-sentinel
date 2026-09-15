<#
.SYNOPSIS
Installs, upgrades, and reports status for a Viegard Windows satellite.

.DESCRIPTION
Publishes Viegard.PipelineHost as a self-contained win-x64 application, configures it for the sources role only, stores the PostgreSQL password in a locked local secret file, and registers the host as a client-specific Windows service.  The script is written for Windows PowerShell 5.1.  MDaemon is the only implemented client profile today.

.PARAMETER Command
The command to run: install, upgrade, or status.

.PARAMETER Client
The satellite client profile to install, upgrade, or query.  Valid value today: MDaemon.

.PARAMETER InstallDir
The installation directory.  The default is C:\Program Files\Viegard Satellite\<Client>.  The app is published under the app subdirectory and secrets are stored under the secrets subdirectory.

.PARAMETER MDaemonRoot
The MDaemon root folder.  The default prompt value is C:\MDaemon and the folder must contain a Logs subfolder.

.PARAMETER LogDirectory
The MDaemon log directory.  The default is the Logs subfolder under MDaemonRoot.

.PARAMETER PostgresHost
The PostgreSQL host that the satellite should write to.

.PARAMETER PostgresPort
The PostgreSQL TCP port.

.PARAMETER PostgresDatabase
The PostgreSQL database name.

.PARAMETER PostgresUsername
The least-privilege PostgreSQL role used by this satellite.

.PARAMETER PostgresPassword
The PostgreSQL password as a SecureString.  It is written only to the local secret file when that file does not already exist.

.PARAMETER PostgresSchema
The PostgreSQL schema containing Viegard objects.

.PARAMETER InstanceId
The required unique Viegard host instance id for this MDaemon host.

.PARAMETER MDaemonLogKinds
The MDaemon log kinds to enable.  Valid values are SmtpIn, SmtpOut, Imap, Pop3, Screening, and DynamicScreening.  Omit for an interactive prompt defaulting to all supported kinds.

.PARAMETER ServiceAccount
The Windows service account choice: VirtualAccount, LocalService, or Custom.  VirtualAccount is recommended and uses NT SERVICE\<service name> with no password.

.PARAMETER CustomServiceAccountName
The account name used when ServiceAccount is Custom, for example DOMAIN\viegard-satellite.

.PARAMETER CustomServiceAccountPassword
The password used when ServiceAccount is Custom.

.PARAMETER Force
For install, replace an existing appsettings.Production.json without prompting.  For upgrade, republish even when git reports no incoming commits.

.PARAMETER Yes
Treat confirmation prompts as approved.  Use only for automation where the disruptive steps are intentional.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet("install", "upgrade", "status")]
    [string]$Command,

    [string]$Client,

    [string]$InstallDir,

    [string]$MDaemonRoot,

    [string]$LogDirectory,

    [string]$PostgresHost,

    [ValidateRange(1, 65535)]
    [int]$PostgresPort = 5432,

    [string]$PostgresDatabase = "viegard",

    [string]$PostgresUsername,

    [securestring]$PostgresPassword,

    [string]$PostgresSchema = "viegard",

    [string]$InstanceId,

    [string[]]$MDaemonLogKinds,

    [string]$ServiceAccount,

    [string]$CustomServiceAccountName,

    [securestring]$CustomServiceAccountPassword,

    [switch]$Force,

    [switch]$Yes
)

Set-StrictMode -Version 2.0

$script:DefaultInstallRoot = "C:\Program Files\Viegard Satellite"
$script:DatabasePasswordSecretName = "viegard-db-password"
$script:ProvidedParameters = @{}
$script:ClientProfile = $null
$script:ServiceName = $null
$script:ServiceDisplayName = $null
$script:ServiceDescription = $null

foreach ($providedName in $PSBoundParameters.Keys) {
    $script:ProvidedParameters[$providedName] = $true
}

function Test-ParameterProvided {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    return $script:ProvidedParameters.ContainsKey($Name)
}

function Write-ColorLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,

        [Parameter(Mandatory = $true)]
        [System.ConsoleColor]$Color,

        [bool]$ErrorStream = $false
    )

    $previousColor = [Console]::ForegroundColor
    try {
        [Console]::ForegroundColor = $Color
        if ($ErrorStream) {
            [Console]::Error.WriteLine("[viegard] " + $Message)
        }
        else {
            [Console]::Out.WriteLine("[viegard] " + $Message)
        }
    }
    finally {
        [Console]::ForegroundColor = $previousColor
    }
}

function Write-InfoLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    Write-ColorLine -Message $Message -Color Cyan
}

function Write-WarnLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    Write-ColorLine -Message $Message -Color Yellow -ErrorStream $true
}

function Stop-WithMessage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    Write-ColorLine -Message $Message -Color Red -ErrorStream $true
    exit 1
}

function Write-Usage {
    $lines = @(
        "Usage: .\deploy\windows\viegard-satellite.ps1 <install|upgrade|status> -Client MDaemon [options]",
        "",
        "Install example:",
        "  .\deploy\windows\viegard-satellite.ps1 install -Client MDaemon -InstanceId mdaemon-MAIL01",
        "",
        "Automation example:",
        "  .\deploy\windows\viegard-satellite.ps1 install -Yes -Client MDaemon -ServiceAccount VirtualAccount -MDaemonRoot C:\MDaemon -PostgresHost 192.0.2.10 -PostgresUsername viegard_sat_mail01 -PostgresPassword (Read-Host -AsSecureString) -InstanceId mdaemon-MAIL01",
        "",
        "Run Get-Help .\deploy\windows\viegard-satellite.ps1 -Detailed for all parameters."
    )

    foreach ($line in $lines) {
        [Console]::Out.WriteLine($line)
    }
}

function Get-ClientProfiles {
    $profiles = @{}
    $profiles["MDaemon"] = [pscustomobject]@{
        Name = "MDaemon"
        ServiceName = "ViegardSatelliteMDaemon"
        DisplayName = "Viegard Satellite Pipeline (MDaemon)"
        Description = "Viegard satellite pipeline for MDaemon log ingestion."
        InstanceIdPrefix = "mdaemon"
        CollectSettings = { Get-MDaemonProfileSettings }
        BuildSourcesConfiguration = { param($ProfileSettings) New-MDaemonSourcesConfiguration -ProfileSettings $ProfileSettings }
        ReadSettingsFromConfiguration = { param($Configuration) Get-MDaemonProfileSettingsFromConfiguration -Configuration $Configuration }
        GetSummaryLines = {
            param($ProfileSettings)
            return @(
                "  Log dir:      " + $ProfileSettings.LogDirectory,
                "  Log kinds:    " + (($ProfileSettings.MDaemonLogKinds) -join ", ")
            )
        }
    }

    return $profiles
}

function Get-ValidClientNames {
    $profiles = Get-ClientProfiles
    return @($profiles.Keys | Sort-Object)
}

function Resolve-ClientProfile {
    $profiles = Get-ClientProfiles
    $validClients = Get-ValidClientNames
    $validClientList = $validClients -join ", "

    if (Test-ParameterProvided -Name "Client") {
        if ([string]::IsNullOrWhiteSpace($Client)) {
            Stop-WithMessage "Client is required.  Valid values: $validClientList"
        }

        foreach ($validClient in $validClients) {
            if ($validClient.Equals($Client, [StringComparison]::OrdinalIgnoreCase)) {
                return $profiles[$validClient]
            }
        }

        Stop-WithMessage "Unknown client '$Client'.  Valid values: $validClientList"
    }

    while ($true) {
        $candidate = Read-TextValue -Prompt ("Client profile.  Valid values: " + $validClientList) -DefaultValue "MDaemon"
        foreach ($validClient in $validClients) {
            if ($validClient.Equals($candidate, [StringComparison]::OrdinalIgnoreCase)) {
                return $profiles[$validClient]
            }
        }

        Write-WarnLine "Unknown client '$candidate'.  Valid values: $validClientList"
    }
}

function Initialize-ClientProfile {
    $script:ClientProfile = Resolve-ClientProfile
    $script:ServiceName = $script:ClientProfile.ServiceName
    $script:ServiceDisplayName = $script:ClientProfile.DisplayName
    $script:ServiceDescription = $script:ClientProfile.Description
}

function Get-ServiceAccountChoices {
    return @("VirtualAccount", "LocalService", "Custom")
}

function Resolve-ServiceAccountChoice {
    $choices = Get-ServiceAccountChoices
    $choiceList = $choices -join ", "

    if (Test-ParameterProvided -Name "ServiceAccount") {
        if ([string]::IsNullOrWhiteSpace($ServiceAccount)) {
            Stop-WithMessage "ServiceAccount is required when supplied.  Valid values: $choiceList"
        }

        foreach ($choice in $choices) {
            if ($choice.Equals($ServiceAccount, [StringComparison]::OrdinalIgnoreCase)) {
                return $choice
            }
        }

        Stop-WithMessage "Unknown service account choice '$ServiceAccount'.  Valid values: $choiceList"
    }

    if ((Test-ParameterProvided -Name "CustomServiceAccountName") -or (Test-ParameterProvided -Name "CustomServiceAccountPassword")) {
        return "Custom"
    }

    while ($true) {
        $candidate = Read-TextValue -Prompt ("Service account.  Valid values: " + $choiceList) -DefaultValue "VirtualAccount"
        foreach ($choice in $choices) {
            if ($choice.Equals($candidate, [StringComparison]::OrdinalIgnoreCase)) {
                return $choice
            }
        }

        Write-WarnLine "Unknown service account choice '$candidate'.  Valid values: $choiceList"
    }
}

function Get-ServiceAccountConfiguration {
    $choice = Resolve-ServiceAccountChoice
    if ($choice -eq "VirtualAccount") {
        $accountName = "NT SERVICE\" + $script:ServiceName
        return [pscustomobject]@{
            Choice = $choice
            AccountName = $accountName
            ScObjectName = $accountName
            AclPrincipal = $accountName
            Password = $null
            RequiresPassword = $false
        }
    }

    if ($choice -eq "LocalService") {
        return [pscustomobject]@{
            Choice = $choice
            AccountName = "NT AUTHORITY\LOCAL SERVICE"
            ScObjectName = "NT AUTHORITY\LocalService"
            AclPrincipal = "NT AUTHORITY\LOCAL SERVICE"
            Password = $null
            RequiresPassword = $false
        }
    }

    if (Test-ParameterProvided -Name "CustomServiceAccountName") {
        if ([string]::IsNullOrWhiteSpace($CustomServiceAccountName)) {
            Stop-WithMessage "CustomServiceAccountName is required when ServiceAccount is Custom."
        }

        $customAccountName = $CustomServiceAccountName
    }
    else {
        $customAccountName = Read-TextValue -Prompt "Custom service account name, for example DOMAIN\viegard-satellite"
    }

    $customPassword = $null
    if (Test-ParameterProvided -Name "CustomServiceAccountPassword") {
        if ($null -eq $CustomServiceAccountPassword -or -not (Test-SecureStringHasValue -SecureValue $CustomServiceAccountPassword)) {
            Stop-WithMessage "CustomServiceAccountPassword is required when ServiceAccount is Custom."
        }

        $customPassword = $CustomServiceAccountPassword
    }
    else {
        while ($true) {
            $customPassword = Read-Host -Prompt "Custom service account password" -AsSecureString
            if (Test-SecureStringHasValue -SecureValue $customPassword) {
                break
            }

            Write-WarnLine "Custom service account password is required."
        }
    }

    return [pscustomobject]@{
        Choice = $choice
        AccountName = $customAccountName
        ScObjectName = $customAccountName
        AclPrincipal = $customAccountName
        Password = $customPassword
        RequiresPassword = $true
    }
}

function Read-Confirmation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prompt
    )

    if ($Yes.IsPresent) {
        return $true
    }

    $reply = Read-Host -Prompt ($Prompt + " [y/N]")
    if ([string]::IsNullOrWhiteSpace($reply)) {
        return $false
    }

    $normalizedReply = $reply.Trim().ToLowerInvariant()
    return ($normalizedReply -eq "y" -or $normalizedReply -eq "yes")
}

function Test-Administrator {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-CommandAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $commandInfo = Get-Command -Name $Name -ErrorAction SilentlyContinue
    return ($null -ne $commandInfo)
}

function Test-Dotnet10Sdk {
    if (-not (Test-CommandAvailable -Name "dotnet")) {
        return $false
    }

    $sdkLines = & dotnet --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    foreach ($sdkLine in $sdkLines) {
        if ($sdkLine -like "10.*") {
            return $true
        }
    }

    return $false
}

function ConvertTo-PlainText {
    param(
        [Parameter(Mandatory = $true)]
        [securestring]$SecureValue
    )

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Test-SecureStringHasValue {
    param(
        [Parameter(Mandatory = $true)]
        [securestring]$SecureValue
    )

    $plainValue = ConvertTo-PlainText -SecureValue $SecureValue
    try {
        return (-not [string]::IsNullOrEmpty($plainValue))
    }
    finally {
        $plainValue = $null
    }
}

function ConvertTo-Crlf {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $normalized = $Content -replace "`r`n", "`n"
    $normalized = $normalized -replace "`r", "`n"
    $lines = $normalized.Split([string[]]@("`n"), [System.StringSplitOptions]::None)
    $normalized = $lines -join "`r`n"
    if (-not $normalized.EndsWith("`r`n")) {
        $normalized = $normalized + "`r`n"
    }

    return $normalized
}

function New-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path -LiteralPath $Path -PathType Container) {
        return
    }

    if (Test-Path -LiteralPath $Path) {
        Stop-WithMessage "Path exists but is not a directory: $Path"
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Write-TextFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $parentPath = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parentPath)) {
        New-Directory -Path $parentPath
    }

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, (ConvertTo-Crlf -Content $Content), $encoding)
}

function Write-SecretFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [securestring]$Secret
    )

    if (Test-Path -LiteralPath $Path) {
        Write-InfoLine "Secret $script:DatabasePasswordSecretName already exists; keeping it."
        return
    }

    $parentPath = Split-Path -Parent $Path
    New-Directory -Path $parentPath

    $plainSecret = ConvertTo-PlainText -SecureValue $Secret
    try {
        $encoding = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($Path, $plainSecret, $encoding)
        Write-InfoLine "Created local database password secret file."
    }
    finally {
        $plainSecret = $null
    }
}

function Read-TextValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prompt,

        [string]$DefaultValue,

        [bool]$Required = $true
    )

    while ($true) {
        if ([string]::IsNullOrWhiteSpace($DefaultValue)) {
            $rawValue = Read-Host -Prompt $Prompt
        }
        else {
            $rawValue = Read-Host -Prompt ($Prompt + " [" + $DefaultValue + "]")
        }

        if ([string]::IsNullOrWhiteSpace($rawValue)) {
            $candidate = $DefaultValue
        }
        else {
            $candidate = $rawValue.Trim()
        }

        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            return $candidate
        }

        if ($Required) {
            Write-WarnLine ($Prompt + " is required.")
        }
        else {
            return ""
        }
    }
}

function Read-PortValue {
    param(
        [int]$DefaultValue = 5432
    )

    while ($true) {
        $rawValue = Read-Host -Prompt ("PostgreSQL port [" + $DefaultValue + "]")
        if ([string]::IsNullOrWhiteSpace($rawValue)) {
            return $DefaultValue
        }

        $parsedPort = 0
        if ([int]::TryParse($rawValue.Trim(), [ref]$parsedPort) -and $parsedPort -ge 1 -and $parsedPort -le 65535) {
            return $parsedPort
        }

        Write-WarnLine "Enter a TCP port in the range 1-65535."
    }
}

function Test-MDaemonRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $false
    }

    $logsPath = Join-Path $Path "Logs"
    return (Test-Path -LiteralPath $logsPath -PathType Container)
}

function Get-MDaemonRootValue {
    if (Test-ParameterProvided -Name "MDaemonRoot") {
        if (Test-MDaemonRoot -Path $MDaemonRoot) {
            return (Resolve-Path -LiteralPath $MDaemonRoot).ProviderPath
        }

        Stop-WithMessage "MDaemonRoot must exist and contain a Logs subfolder: $MDaemonRoot"
    }

    while ($true) {
        $candidate = Read-TextValue -Prompt "MDaemon root folder" -DefaultValue "C:\MDaemon"
        if (Test-MDaemonRoot -Path $candidate) {
            return (Resolve-Path -LiteralPath $candidate).ProviderPath
        }

        Write-WarnLine "The MDaemon root folder must exist and contain a Logs subfolder."
    }
}

function Get-LogDirectoryValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (Test-ParameterProvided -Name "LogDirectory") {
        if (Test-Path -LiteralPath $LogDirectory -PathType Container) {
            return (Resolve-Path -LiteralPath $LogDirectory).ProviderPath
        }

        Stop-WithMessage "LogDirectory must exist: $LogDirectory"
    }

    $defaultLogPath = Join-Path $RootPath "Logs"
    while ($true) {
        $candidate = Read-TextValue -Prompt "MDaemon log directory" -DefaultValue $defaultLogPath
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            return (Resolve-Path -LiteralPath $candidate).ProviderPath
        }

        Write-WarnLine "The MDaemon log directory must exist."
    }
}

function Test-LogDirectoryReadable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    try {
        Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop | Select-Object -First 1 | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Test-SafeSchemaName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Schema
    )

    if ([string]::IsNullOrWhiteSpace($Schema)) {
        return $false
    }

    if ($Schema.Length -gt 63) {
        return $false
    }

    return [regex]::IsMatch($Schema, "^[a-z][a-z0-9_]*$")
}

function Get-SchemaValue {
    if (Test-ParameterProvided -Name "PostgresSchema") {
        if (Test-SafeSchemaName -Schema $PostgresSchema) {
            return $PostgresSchema
        }

        Stop-WithMessage "PostgresSchema must match ^[a-z][a-z0-9_]*$ and be at most 63 characters."
    }

    while ($true) {
        $candidate = Read-TextValue -Prompt "PostgreSQL schema" -DefaultValue "viegard"
        if (Test-SafeSchemaName -Schema $candidate) {
            return $candidate
        }

        Write-WarnLine "The schema must match ^[a-z][a-z0-9_]*$ and be at most 63 characters."
    }
}

function Read-SecureValue {
    while ($true) {
        $secureValue = Read-Host -Prompt "PostgreSQL password" -AsSecureString
        if (Test-SecureStringHasValue -SecureValue $secureValue) {
            return $secureValue
        }

        Write-WarnLine "PostgreSQL password is required."
    }
}

function Get-ValidLogKinds {
    return @("SmtpIn", "SmtpOut", "Imap", "Pop3", "Screening", "DynamicScreening")
}

function ConvertTo-LogKindList {
    param(
        [string[]]$Kinds
    )

    $validKinds = Get-ValidLogKinds
    if ($null -eq $Kinds -or $Kinds.Count -eq 0) {
        return $validKinds
    }

    $selectedKinds = @()
    foreach ($entry in $Kinds) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        $parts = $entry.Split(",")
        foreach ($part in $parts) {
            $trimmedPart = $part.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmedPart)) {
                continue
            }

            if ($trimmedPart.Equals("all", [StringComparison]::OrdinalIgnoreCase)) {
                return $validKinds
            }

            $canonicalKind = $null
            foreach ($validKind in $validKinds) {
                if ($validKind.Equals($trimmedPart, [StringComparison]::OrdinalIgnoreCase)) {
                    $canonicalKind = $validKind
                    break
                }
            }

            if ($null -eq $canonicalKind) {
                Stop-WithMessage ("Unknown MDaemon log kind '" + $trimmedPart + "'.  Valid values: " + ($validKinds -join ", "))
            }

            $alreadySelected = $false
            foreach ($selectedKind in $selectedKinds) {
                if ($selectedKind.Equals($canonicalKind, [StringComparison]::OrdinalIgnoreCase)) {
                    $alreadySelected = $true
                    break
                }
            }

            if (-not $alreadySelected) {
                $selectedKinds += $canonicalKind
            }
        }
    }

    if ($selectedKinds.Count -eq 0) {
        Stop-WithMessage "At least one MDaemon log kind must be enabled."
    }

    return $selectedKinds
}

function Read-LogKindsValue {
    $validKinds = Get-ValidLogKinds
    if (Test-ParameterProvided -Name "MDaemonLogKinds") {
        return ConvertTo-LogKindList -Kinds $MDaemonLogKinds
    }

    while ($true) {
        $prompt = "MDaemon log kinds to enable, comma-separated.  Valid: " + ($validKinds -join ", ") + ".  Use all for every supported kind [all]"
        $rawValue = Read-Host -Prompt $prompt
        if ([string]::IsNullOrWhiteSpace($rawValue)) {
            return $validKinds
        }

        return ConvertTo-LogKindList -Kinds @($rawValue)
    }
}

function Get-MDaemonProfileSettings {
    $rootPath = Get-MDaemonRootValue
    $logPath = Get-LogDirectoryValue -RootPath $rootPath
    $enabledLogKinds = Read-LogKindsValue

    if (-not (Test-LogDirectoryReadable -Path $logPath)) {
        Stop-WithMessage "The MDaemon log directory exists but is not readable: $logPath"
    }

    return [pscustomobject]@{
        MDaemonRoot = $rootPath
        LogDirectory = $logPath
        MDaemonLogKinds = $enabledLogKinds
        ReadPaths = @($logPath)
    }
}

function Get-MDaemonProfileSettingsFromConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Configuration
    )

    $logPath = [string]$Configuration.Viegard.Sources.MDaemon.LogDirectory
    $enabledLogKinds = @()
    foreach ($fileEntry in $Configuration.Viegard.Sources.MDaemon.Files) {
        $enabledLogKinds += [string]$fileEntry.LogKind
    }

    return [pscustomobject]@{
        MDaemonRoot = ""
        LogDirectory = $logPath
        MDaemonLogKinds = $enabledLogKinds
        ReadPaths = @($logPath)
    }
}

function Get-MDaemonFileEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Kinds
    )

    $entries = @()
    foreach ($kind in $Kinds) {
        if ($kind -eq "SmtpIn") {
            $entries += [ordered]@{ Pattern = "MDaemon-*-SMTP-(in).log"; LogKind = "SmtpIn" }
        }
        elseif ($kind -eq "SmtpOut") {
            $entries += [ordered]@{ Pattern = "MDaemon-*-SMTP-(out).log"; LogKind = "SmtpOut" }
        }
        elseif ($kind -eq "Imap") {
            $entries += [ordered]@{ Pattern = "MDaemon-*-IMAP.log"; LogKind = "Imap" }
        }
        elseif ($kind -eq "Pop3") {
            $entries += [ordered]@{ Pattern = "MDaemon-*-POP3.log"; LogKind = "Pop3" }
        }
        elseif ($kind -eq "Screening") {
            $entries += [ordered]@{ Pattern = "MDaemon-*-Screening.log"; LogKind = "Screening" }
        }
        elseif ($kind -eq "DynamicScreening") {
            $entries += [ordered]@{ Pattern = "DynScrn-*.log"; LogKind = "DynamicScreening" }
        }
        else {
            Stop-WithMessage "Unsupported MDaemon log kind reached configuration generation: $kind"
        }
    }

    return $entries
}

function New-MDaemonSourcesConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$ProfileSettings
    )

    $fileEntries = Get-MDaemonFileEntries -Kinds $ProfileSettings.MDaemonLogKinds
    return [ordered]@{
        Imap = [ordered]@{
            Accounts = @()
        }
        Syslog = [ordered]@{
            Enabled = $false
        }
        MDaemon = [ordered]@{
            Enabled = $true
            LogDirectory = $ProfileSettings.LogDirectory
            Files = @($fileEntries)
            PollInterval = "00:00:05"
            IncludeNoise = $false
            IngestExistingOnFirstRun = $false
        }
    }
}

function Test-TcpConnect {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ServerName,

        [Parameter(Mandatory = $true)]
        [int]$PortNumber,

        [int]$TimeoutMilliseconds = 3000
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $asyncResult = $client.BeginConnect($ServerName, $PortNumber, $null, $null)
        $connected = $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMilliseconds, $false)
        if (-not $connected) {
            return $false
        }

        $client.EndConnect($asyncResult)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function Get-ServiceRecord {
    $filterText = "Name='" + $script:ServiceName + "'"
    return Get-CimInstance -ClassName Win32_Service -Filter $filterText -ErrorAction SilentlyContinue
}

function Get-ServiceExecutablePath {
    param(
        [string]$PathName
    )

    if ([string]::IsNullOrWhiteSpace($PathName)) {
        return $null
    }

    $trimmedPath = $PathName.Trim()
    if ($trimmedPath.StartsWith('"')) {
        $closingQuoteIndex = $trimmedPath.IndexOf('"', 1)
        if ($closingQuoteIndex -gt 1) {
            return $trimmedPath.Substring(1, $closingQuoteIndex - 1)
        }
    }

    $spaceIndex = $trimmedPath.IndexOf(' ')
    if ($spaceIndex -gt 0) {
        return $trimmedPath.Substring(0, $spaceIndex)
    }

    return $trimmedPath
}

function Get-InstallDirFromService {
    $serviceRecord = Get-ServiceRecord
    if ($null -eq $serviceRecord) {
        return $null
    }

    $executablePath = Get-ServiceExecutablePath -PathName $serviceRecord.PathName
    if ([string]::IsNullOrWhiteSpace($executablePath)) {
        return $null
    }

    $appDirectory = Split-Path -Parent $executablePath
    if ([string]::IsNullOrWhiteSpace($appDirectory)) {
        return $null
    }

    $appDirectoryName = Split-Path -Leaf $appDirectory
    if ($appDirectoryName.Equals("app", [StringComparison]::OrdinalIgnoreCase)) {
        return (Split-Path -Parent $appDirectory)
    }

    return $appDirectory
}

function Get-EffectiveInstallDir {
    if (Test-ParameterProvided -Name "InstallDir") {
        return $InstallDir
    }

    $serviceInstallDir = Get-InstallDirFromService
    if (-not [string]::IsNullOrWhiteSpace($serviceInstallDir)) {
        return $serviceInstallDir
    }

    return (Join-Path $script:DefaultInstallRoot $script:ClientProfile.Name)
}

function Get-RepositoryRoot {
    $currentItem = Get-Item -LiteralPath $PSScriptRoot
    while ($null -ne $currentItem) {
        $gitPath = Join-Path $currentItem.FullName ".git"
        if (Test-Path -LiteralPath $gitPath) {
            return $currentItem.FullName
        }

        $currentItem = $currentItem.Parent
    }

    Stop-WithMessage "Could not find the repository root from script path $PSScriptRoot."
}

function Get-GitOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList
    )

    Push-Location $RepositoryRoot
    try {
        $outputLines = & git @ArgumentList 2>&1
        if ($LASTEXITCODE -ne 0) {
            Stop-WithMessage ("git " + ($ArgumentList -join " ") + " failed: " + ($outputLines -join " "))
        }

        return $outputLines
    }
    finally {
        Pop-Location
    }
}

function Invoke-PipelinePublish {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$AppDirectory,

        [bool]$PreserveConfiguration = $false
    )

    New-Directory -Path $AppDirectory
    $configurationPath = Join-Path $AppDirectory "appsettings.Production.json"
    $savedConfiguration = $null

    if ($PreserveConfiguration -and (Test-Path -LiteralPath $configurationPath)) {
        $savedConfiguration = [System.IO.File]::ReadAllText($configurationPath)
    }

    Write-InfoLine "Publishing Viegard.PipelineHost to $AppDirectory ..."
    Push-Location $RepositoryRoot
    try {
        & dotnet publish "src\Viegard.PipelineHost" -c Release -r win-x64 --self-contained -o $AppDirectory
        if ($LASTEXITCODE -ne 0) {
            Stop-WithMessage "dotnet publish failed."
        }
    }
    finally {
        Pop-Location
    }

    if ($PreserveConfiguration -and $null -ne $savedConfiguration) {
        Write-TextFile -Path $configurationPath -Content $savedConfiguration
        Write-InfoLine "Preserved existing appsettings.Production.json."
    }

    $exePath = Join-Path $AppDirectory "Viegard.PipelineHost.exe"
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        Stop-WithMessage "Publish completed, but the service executable was not found: $exePath"
    }
}

function New-SatelliteConfigurationJson {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InstallerConfig
    )

    $sourcesConfiguration = & $script:ClientProfile.BuildSourcesConfiguration $InstallerConfig.ProfileSettings
    $configuration = [ordered]@{
        Logging = [ordered]@{
            LogLevel = [ordered]@{
                Default = "Information"
                "Microsoft.Hosting.Lifetime" = "Information"
                "Microsoft.EntityFrameworkCore" = "Warning"
            }
            EventLog = [ordered]@{
                LogLevel = [ordered]@{
                    Default = "Information"
                    "Microsoft.Hosting.Lifetime" = "Information"
                    "Viegard.PipelineHost.Workers.PipelineStartupService" = "Information"
                    "Microsoft.EntityFrameworkCore" = "Warning"
                }
            }
        }
        Viegard = [ordered]@{
            WindowsService = [ordered]@{
                ServiceName = $InstallerConfig.ServiceName
            }
            Host = [ordered]@{
                InstanceId = $InstallerConfig.InstanceId
                Roles = @("sources")
            }
            Secrets = [ordered]@{
                Provider = "file"
                Directory = $InstallerConfig.SecretsDirectory
            }
            Persistence = [ordered]@{
                Provider = "postgres"
            }
            Database = [ordered]@{
                Host = $InstallerConfig.PostgresHost
                Port = $InstallerConfig.PostgresPort
                Name = $InstallerConfig.PostgresDatabase
                Username = $InstallerConfig.PostgresUsername
                Schema = $InstallerConfig.PostgresSchema
                PasswordSecretName = $script:DatabasePasswordSecretName
                AutoMigrate = $false
            }
            Sources = $sourcesConfiguration
        }
    }

    return ($configuration | ConvertTo-Json -Depth 20)
}

function Write-SatelliteConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InstallerConfig
    )

    $configurationPath = Join-Path $InstallerConfig.AppDirectory "appsettings.Production.json"
    if ((Test-Path -LiteralPath $configurationPath) -and (-not $Force.IsPresent)) {
        if (-not (Read-Confirmation -Prompt "Replace existing appsettings.Production.json with the prompted values?")) {
            Write-InfoLine "Keeping existing appsettings.Production.json."
            return $false
        }
    }

    $json = New-SatelliteConfigurationJson -InstallerConfig $InstallerConfig
    Write-TextFile -Path $configurationPath -Content $json
    Write-InfoLine "Wrote appsettings.Production.json without storing the database password."
    return $true
}

function Invoke-Icacls {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList
    )

    & icacls.exe @ArgumentList | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Stop-WithMessage "icacls failed while applying filesystem permissions."
    }
}

function Set-InitialSecretsAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SecretsDirectory
    )

    New-Directory -Path $SecretsDirectory
    Invoke-Icacls -ArgumentList @($SecretsDirectory, "/inheritance:r")
    Invoke-Icacls -ArgumentList @(
        $SecretsDirectory,
        "/grant:r",
        "*S-1-5-18:(OI)(CI)F",
        "*S-1-5-32-544:(OI)(CI)F"
    )
    Invoke-Icacls -ArgumentList @($SecretsDirectory, "/remove:g", "*S-1-5-32-545", "*S-1-5-11", "*S-1-1-0")
}

function Set-SecretsAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SecretsDirectory,

        [Parameter(Mandatory = $true)]
        [string]$PasswordFile,

        [Parameter(Mandatory = $true)]
        [pscustomobject]$ServiceAccountConfig
    )

    $servicePrincipal = $ServiceAccountConfig.AclPrincipal
    Invoke-Icacls -ArgumentList @($SecretsDirectory, "/inheritance:r")
    Invoke-Icacls -ArgumentList @(
        $SecretsDirectory,
        "/grant:r",
        "*S-1-5-18:(OI)(CI)F",
        "*S-1-5-32-544:(OI)(CI)F",
        "${servicePrincipal}:(OI)(CI)RX"
    )
    Invoke-Icacls -ArgumentList @($SecretsDirectory, "/remove:g", "*S-1-5-32-545", "*S-1-5-11", "*S-1-1-0")

    Invoke-Icacls -ArgumentList @($PasswordFile, "/inheritance:r")
    Invoke-Icacls -ArgumentList @(
        $PasswordFile,
        "/grant:r",
        "*S-1-5-18:F",
        "*S-1-5-32-544:F",
        "${servicePrincipal}:R"
    )
    Invoke-Icacls -ArgumentList @($PasswordFile, "/remove:g", "*S-1-5-32-545", "*S-1-5-11", "*S-1-1-0")
    Write-InfoLine "Locked the secrets ACL to SYSTEM, Administrators, and the selected service account."
}

function Grant-ReadAccessToPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [pscustomobject]$ServiceAccountConfig
    )

    $servicePrincipal = $ServiceAccountConfig.AclPrincipal
    Invoke-Icacls -ArgumentList @($Path, "/grant", "${servicePrincipal}:(OI)(CI)RX")
    Write-InfoLine "Granted the selected service account read access to $Path."
}

function Grant-ClientProfileAccess {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InstallerConfig
    )

    $readPaths = @($InstallerConfig.ProfileSettings.ReadPaths)
    foreach ($readPath in $readPaths) {
        if ([string]::IsNullOrWhiteSpace($readPath)) {
            continue
        }

        Grant-ReadAccessToPath -Path $readPath -ServiceAccountConfig $InstallerConfig.ServiceAccount
    }
}

function Stop-SatelliteService {
    param(
        [switch]$AlreadyConfirmed
    )

    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return
    }

    if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        return
    }

    if ((-not $AlreadyConfirmed.IsPresent) -and (-not (Read-Confirmation -Prompt "Stop the existing $script:ServiceName service now?"))) {
        Stop-WithMessage "Aborted before stopping the service."
    }

    Write-InfoLine "Stopping $script:ServiceName ..."
    Stop-Service -Name $script:ServiceName -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(45))
}

function Invoke-Sc {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [string]$FailureMessage = "sc.exe failed while configuring the Windows service."
    )

    & sc.exe @ArgumentList | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Stop-WithMessage $FailureMessage
    }
}

function Invoke-ScWithOptionalPassword {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [pscustomobject]$ServiceAccountConfig,

        [string]$FailureMessage = "sc.exe failed while configuring the Windows service."
    )

    if ($null -eq $ServiceAccountConfig -or -not $ServiceAccountConfig.RequiresPassword) {
        Invoke-Sc -ArgumentList $ArgumentList -FailureMessage $FailureMessage
        return
    }

    $plainPassword = ConvertTo-PlainText -SecureValue $ServiceAccountConfig.Password
    try {
        $argumentsWithPassword = @($ArgumentList)
        $argumentsWithPassword += @("password=", $plainPassword)
        Invoke-Sc -ArgumentList $argumentsWithPassword -FailureMessage $FailureMessage
    }
    finally {
        $plainPassword = $null
    }
}

function Set-ServiceEnvironment {
    $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\" + $script:ServiceName
    $existingValues = @()
    try {
        $property = Get-ItemProperty -LiteralPath $registryPath -Name "Environment" -ErrorAction Stop
        if ($null -ne $property.Environment) {
            $existingValues = @($property.Environment)
        }
    }
    catch {
        $existingValues = @()
    }

    $newValues = @()
    foreach ($entry in $existingValues) {
        if (-not ([string]$entry).StartsWith("DOTNET_ENVIRONMENT=", [StringComparison]::OrdinalIgnoreCase)) {
            $newValues += $entry
        }
    }

    $newValues += "DOTNET_ENVIRONMENT=Production"
    New-ItemProperty -LiteralPath $registryPath -Name "Environment" -PropertyType MultiString -Value ([string[]]$newValues) -Force | Out-Null
}

function Set-ServiceRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [pscustomobject]$ServiceAccountConfig
    )

    $binaryPath = '"' + $ExecutablePath + '"'
    $serviceRecord = Get-ServiceRecord

    if ($null -eq $serviceRecord) {
        if ($null -eq $ServiceAccountConfig) {
            Stop-WithMessage "Service account configuration is required when creating $script:ServiceName."
        }

        Write-InfoLine "Creating Windows service $script:ServiceName ..."
        $serviceArguments = @(
            "create",
            $script:ServiceName,
            "binPath=",
            $binaryPath,
            "start=",
            "delayed-auto",
            "obj=",
            $ServiceAccountConfig.ScObjectName,
            "DisplayName=",
            $script:ServiceDisplayName
        )
        Invoke-ScWithOptionalPassword -ArgumentList $serviceArguments -ServiceAccountConfig $ServiceAccountConfig -FailureMessage "sc.exe failed while creating the Windows service."
    }
    else {
        Write-InfoLine "Reconfiguring existing Windows service $script:ServiceName ..."
        $serviceArguments = @(
            "config",
            $script:ServiceName,
            "binPath=",
            $binaryPath,
            "start=",
            "delayed-auto",
            "DisplayName=",
            $script:ServiceDisplayName
        )
        if ($null -ne $ServiceAccountConfig) {
            $serviceArguments += @("obj=", $ServiceAccountConfig.ScObjectName)
        }

        Invoke-ScWithOptionalPassword -ArgumentList $serviceArguments -ServiceAccountConfig $ServiceAccountConfig -FailureMessage "sc.exe failed while reconfiguring the Windows service."
    }

    Invoke-Sc -ArgumentList @("description", $script:ServiceName, $script:ServiceDescription)
    Invoke-Sc -ArgumentList @("sidtype", $script:ServiceName, "unrestricted")
    Invoke-Sc -ArgumentList @(
        "failure",
        $script:ServiceName,
        "reset=",
        "86400",
        "actions=",
        "restart/60000/restart/300000/restart/900000"
    )
    Invoke-Sc -ArgumentList @("failureflag", $script:ServiceName, "1")
    Set-ServiceEnvironment
}

function Get-RelevantApplicationEvents {
    param(
        [datetime]$Since = (Get-Date).AddDays(-1),

        [int]$MaxEvents = 5
    )

    $selectedEvents = @()
    try {
        $recentEvents = Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = $Since } -MaxEvents 100 -ErrorAction Stop
    }
    catch {
        return $selectedEvents
    }

    foreach ($eventEntry in $recentEvents) {
        $providerName = [string]$eventEntry.ProviderName
        $message = [string]$eventEntry.Message
        $isRelevant = $false

        if ($providerName.IndexOf("Viegard", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $isRelevant = $true
        }

        if ($message.IndexOf("Viegard pipeline host", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $isRelevant = $true
        }

        if ($message.IndexOf("ViegardSatellite", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $isRelevant = $true
        }

        if ($message.IndexOf("Viegard.PipelineHost", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $isRelevant = $true
        }

        if ($isRelevant) {
            $selectedEvents += $eventEntry
            if ($selectedEvents.Count -ge $MaxEvents) {
                return $selectedEvents
            }
        }
    }

    return $selectedEvents
}

function Write-EventEntries {
    param(
        [object[]]$Events
    )

    if ($null -eq $Events -or $Events.Count -eq 0) {
        Write-WarnLine "No recent matching Application event-log entries were found."
        return
    }

    Write-InfoLine "Recent relevant Application event-log entries:"
    foreach ($eventEntry in $Events) {
        $message = ([string]$eventEntry.Message) -replace "[\r\n]+", " "
        if ($message.Length -gt 260) {
            $message = $message.Substring(0, 260) + "..."
        }

        $line = ("  {0:u} {1}: {2}" -f $eventEntry.TimeCreated, $eventEntry.ProviderName, $message)
        [Console]::Out.WriteLine($line)
    }
}

function Test-SatelliteStartup {
    param(
        [datetime]$Since
    )

    $events = @()
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Stop-WithMessage "Service $script:ServiceName was not found after registration."
        }

        if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
            Stop-WithMessage "Service $script:ServiceName is $($service.Status), not Running."
        }

        $events = Get-RelevantApplicationEvents -Since $Since -MaxEvents 8
        foreach ($eventEntry in $events) {
            $message = [string]$eventEntry.Message
            if ($message.IndexOf("starting with roles: sources", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                Write-EventEntries -Events $events
                Write-InfoLine "Verified startup event confirming the sources role."
                return
            }
        }

        if ($attempt -lt 6) {
            Start-Sleep -Seconds 5
        }
    }

    Write-EventEntries -Events $events
    Stop-WithMessage "The service is Running, but no Application event confirming roles [sources] was found within 30 seconds."
}

function Start-SatelliteService {
    $startedAt = Get-Date
    Write-InfoLine "Starting $script:ServiceName ..."
    Start-Service -Name $script:ServiceName -ErrorAction Stop
    $service = Get-Service -Name $script:ServiceName -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(45))
    Test-SatelliteStartup -Since $startedAt
}

function Read-ConfigFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot
    )

    $configurationPath = Join-Path (Join-Path $InstallRoot "app") "appsettings.Production.json"
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        return $null
    }

    try {
        $rawJson = [System.IO.File]::ReadAllText($configurationPath)
        return ($rawJson | ConvertFrom-Json)
    }
    catch {
        Write-WarnLine "Could not read appsettings.Production.json."
        return $null
    }
}

function Get-InstallConfiguration {
    $effectiveInstallDir = Get-EffectiveInstallDir
    $appDirectory = Join-Path $effectiveInstallDir "app"
    $secretsDirectory = Join-Path $effectiveInstallDir "secrets"
    $passwordFile = Join-Path $secretsDirectory $script:DatabasePasswordSecretName

    $profileSettings = & $script:ClientProfile.CollectSettings
    $serviceAccountConfig = Get-ServiceAccountConfiguration

    if (Test-ParameterProvided -Name "PostgresHost") {
        if ([string]::IsNullOrWhiteSpace($PostgresHost)) {
            Stop-WithMessage "PostgresHost is required."
        }

        $databaseHostName = $PostgresHost
    }
    else {
        $databaseHostName = Read-TextValue -Prompt "PostgreSQL host"
    }

    if (Test-ParameterProvided -Name "PostgresPort") {
        $databasePort = $PostgresPort
    }
    else {
        $databasePort = Read-PortValue -DefaultValue 5432
    }

    if (Test-ParameterProvided -Name "PostgresDatabase") {
        if ([string]::IsNullOrWhiteSpace($PostgresDatabase)) {
            Stop-WithMessage "PostgresDatabase is required."
        }

        $databaseName = $PostgresDatabase
    }
    else {
        $databaseName = Read-TextValue -Prompt "PostgreSQL database" -DefaultValue "viegard"
    }

    if (Test-ParameterProvided -Name "PostgresUsername") {
        if ([string]::IsNullOrWhiteSpace($PostgresUsername)) {
            Stop-WithMessage "PostgresUsername is required."
        }

        $databaseUsername = $PostgresUsername
    }
    else {
        $databaseUsername = Read-TextValue -Prompt "PostgreSQL username"
    }

    $databaseSchema = Get-SchemaValue

    if (Test-ParameterProvided -Name "InstanceId") {
        if ([string]::IsNullOrWhiteSpace($InstanceId)) {
            Stop-WithMessage "InstanceId is required and must differ between MDaemon hosts."
        }

        $hostInstanceId = $InstanceId
    }
    else {
        $suggestedInstanceId = $script:ClientProfile.InstanceIdPrefix + "-" + $env:COMPUTERNAME
        while ($true) {
            $candidate = Read-TextValue -Prompt ("Instance ID, required and unique per MDaemon host.  Suggested value: " + $suggestedInstanceId) -Required $true
            if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                $hostInstanceId = $candidate
                break
            }
        }
    }

    $databasePassword = $null
    if (Test-Path -LiteralPath $passwordFile -PathType Leaf) {
        Write-InfoLine "Existing database password secret will be kept: $passwordFile"
    }
    elseif (Test-ParameterProvided -Name "PostgresPassword") {
        if ($null -eq $PostgresPassword -or -not (Test-SecureStringHasValue -SecureValue $PostgresPassword)) {
            Stop-WithMessage "PostgresPassword is required when the local secret file does not exist."
        }

        $databasePassword = $PostgresPassword
    }
    else {
        $databasePassword = Read-SecureValue
    }

    Write-InfoLine "Testing TCP connectivity to ${databaseHostName}:$databasePort ..."
    if (-not (Test-TcpConnect -ServerName $databaseHostName -PortNumber $databasePort)) {
        Write-WarnLine "Could not connect to PostgreSQL at ${databaseHostName}:$databasePort from this host."
        if (-not (Read-Confirmation -Prompt "Continue anyway?  The service will not ingest until this TCP path works.")) {
            Stop-WithMessage "Aborted because PostgreSQL is unreachable."
        }
    }

    return [pscustomobject]@{
        Client = $script:ClientProfile.Name
        ServiceName = $script:ServiceName
        ServiceDisplayName = $script:ServiceDisplayName
        ServiceDescription = $script:ServiceDescription
        InstallDir = $effectiveInstallDir
        AppDirectory = $appDirectory
        SecretsDirectory = $secretsDirectory
        PasswordFile = $passwordFile
        ProfileSettings = $profileSettings
        ServiceAccount = $serviceAccountConfig
        PostgresHost = $databaseHostName
        PostgresPort = $databasePort
        PostgresDatabase = $databaseName
        PostgresUsername = $databaseUsername
        PostgresSchema = $databaseSchema
        PostgresPassword = $databasePassword
        InstanceId = $hostInstanceId
    }
}

function Write-InstallSummary {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$InstallerConfig
    )

    Write-InfoLine "Install summary:"
    [Console]::Out.WriteLine("  Service name: $script:ServiceName")
    [Console]::Out.WriteLine("  Client:       " + $InstallerConfig.Client)
    [Console]::Out.WriteLine("  Account:      " + $InstallerConfig.ServiceAccount.AccountName + " (" + $InstallerConfig.ServiceAccount.Choice + ")")
    [Console]::Out.WriteLine("  Install dir:  " + $InstallerConfig.InstallDir)
    [Console]::Out.WriteLine("  Instance ID:  " + $InstallerConfig.InstanceId)
    $summaryLines = & $script:ClientProfile.GetSummaryLines $InstallerConfig.ProfileSettings
    foreach ($summaryLine in $summaryLines) {
        [Console]::Out.WriteLine($summaryLine)
    }
    [Console]::Out.WriteLine("  Postgres:     " + $InstallerConfig.PostgresHost + ":" + $InstallerConfig.PostgresPort + "/" + $InstallerConfig.PostgresDatabase + " schema " + $InstallerConfig.PostgresSchema)
    [Console]::Out.WriteLine("")
    Write-InfoLine "Next steps:"
    [Console]::Out.WriteLine("  Confirm the Viegard VM firewall allows this host to reach PostgreSQL TCP 5432.")
    [Console]::Out.WriteLine("  Open the admin UI /queues page and look for telemetry from instance " + $InstallerConfig.InstanceId + ".")
}

function Invoke-Install {
    if (-not (Test-Administrator)) {
        Stop-WithMessage "The install command must run from an elevated PowerShell session."
    }

    if (-not (Test-CommandAvailable -Name "git")) {
        Stop-WithMessage "git is required.  Install Git for Windows and re-run this script."
    }

    if (-not (Test-Dotnet10Sdk)) {
        Stop-WithMessage ".NET 10 SDK is required.  Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and re-run this script.  The installer does not download prerequisites."
    }

    $installerConfig = Get-InstallConfiguration
    $repositoryRoot = Get-RepositoryRoot
    $serviceRecord = Get-ServiceRecord
    if ($null -ne $serviceRecord) {
        Stop-SatelliteService
    }

    Invoke-PipelinePublish -RepositoryRoot $repositoryRoot -AppDirectory $installerConfig.AppDirectory
    $configurationWritten = Write-SatelliteConfiguration -InstallerConfig $installerConfig

    Set-InitialSecretsAcl -SecretsDirectory $installerConfig.SecretsDirectory
    if ($null -ne $installerConfig.PostgresPassword) {
        Write-SecretFile -Path $installerConfig.PasswordFile -Secret $installerConfig.PostgresPassword
    }
    elseif (-not (Test-Path -LiteralPath $installerConfig.PasswordFile -PathType Leaf)) {
        Stop-WithMessage "The database password secret file is missing: $($installerConfig.PasswordFile)"
    }

    $executablePath = Join-Path $installerConfig.AppDirectory "Viegard.PipelineHost.exe"
    Set-ServiceRegistration -ExecutablePath $executablePath -ServiceAccountConfig $installerConfig.ServiceAccount
    Set-SecretsAcl -SecretsDirectory $installerConfig.SecretsDirectory -PasswordFile $installerConfig.PasswordFile -ServiceAccountConfig $installerConfig.ServiceAccount
    Grant-ClientProfileAccess -InstallerConfig $installerConfig
    Start-SatelliteService
    if (-not $configurationWritten) {
        Write-WarnLine "The summary reflects prompted values, but the existing appsettings.Production.json was kept."
    }
    Write-InstallSummary -InstallerConfig $installerConfig
}

function Invoke-Upgrade {
    if (-not (Test-Administrator)) {
        Stop-WithMessage "The upgrade command must run from an elevated PowerShell session."
    }

    if (-not (Test-CommandAvailable -Name "git")) {
        Stop-WithMessage "git is required for upgrade."
    }

    if (-not (Test-Dotnet10Sdk)) {
        Stop-WithMessage ".NET 10 SDK is required.  Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and re-run this script.  The installer does not download prerequisites."
    }

    $repositoryRoot = Get-RepositoryRoot
    $dirtyLines = @(Get-GitOutput -RepositoryRoot $repositoryRoot -ArgumentList @("status", "--porcelain"))
    if ($dirtyLines.Count -gt 0) {
        Stop-WithMessage "The repository has local changes.  Review them before running upgrade."
    }

    $beforeCommit = (@(Get-GitOutput -RepositoryRoot $repositoryRoot -ArgumentList @("rev-parse", "HEAD")))[0]
    Get-GitOutput -RepositoryRoot $repositoryRoot -ArgumentList @("fetch", "origin") | Out-Null

    $upstreamCommit = $null
    $upstreamOutput = @(Get-GitOutput -RepositoryRoot $repositoryRoot -ArgumentList @("rev-parse", "@{u}"))
    if ($upstreamOutput.Count -gt 0) {
        $upstreamCommit = $upstreamOutput[0]
    }

    if (-not [string]::IsNullOrWhiteSpace($upstreamCommit) -and $beforeCommit -ne $upstreamCommit) {
        Write-InfoLine "Incoming commits:"
        $incomingLines = @(Get-GitOutput -RepositoryRoot $repositoryRoot -ArgumentList @("log", "--oneline", ($beforeCommit + ".." + $upstreamCommit)))
        foreach ($line in $incomingLines) {
            [Console]::Out.WriteLine("  " + $line)
        }
    }

    Get-GitOutput -RepositoryRoot $repositoryRoot -ArgumentList @("pull", "--ff-only") | Out-Null
    $afterCommit = (@(Get-GitOutput -RepositoryRoot $repositoryRoot -ArgumentList @("rev-parse", "HEAD")))[0]

    if ($beforeCommit -eq $afterCommit -and (-not $Force.IsPresent)) {
        Write-InfoLine "Already up to date at $($afterCommit.Substring(0, 7)); nothing to do.  Use -Force to republish anyway."
        return
    }

    if ($beforeCommit -ne $afterCommit) {
        Write-InfoLine ("Updated " + $beforeCommit.Substring(0, 7) + ".." + $afterCommit.Substring(0, 7) + ".")
    }

    if (-not (Read-Confirmation -Prompt "Stop, republish, and restart the $script:ServiceName service now?")) {
        Stop-WithMessage "Aborted before upgrade restart."
    }

    $effectiveInstallDir = Get-EffectiveInstallDir
    $appDirectory = Join-Path $effectiveInstallDir "app"
    $serviceAccountConfig = $null
    if ((Test-ParameterProvided -Name "ServiceAccount") -or (Test-ParameterProvided -Name "CustomServiceAccountName") -or (Test-ParameterProvided -Name "CustomServiceAccountPassword")) {
        $serviceAccountConfig = Get-ServiceAccountConfiguration
    }

    Stop-SatelliteService -AlreadyConfirmed
    Invoke-PipelinePublish -RepositoryRoot $repositoryRoot -AppDirectory $appDirectory -PreserveConfiguration $true

    $executablePath = Join-Path $appDirectory "Viegard.PipelineHost.exe"
    Set-ServiceRegistration -ExecutablePath $executablePath -ServiceAccountConfig $serviceAccountConfig
    if ($null -ne $serviceAccountConfig) {
        $secretsDirectory = Join-Path $effectiveInstallDir "secrets"
        $passwordFile = Join-Path $secretsDirectory $script:DatabasePasswordSecretName
        if (Test-Path -LiteralPath $passwordFile -PathType Leaf) {
            Set-SecretsAcl -SecretsDirectory $secretsDirectory -PasswordFile $passwordFile -ServiceAccountConfig $serviceAccountConfig
        }
        else {
            Write-WarnLine "Database password secret not found; skipping secret ACL refresh."
        }

        $configuration = Read-ConfigFile -InstallRoot $effectiveInstallDir
        if ($null -ne $configuration) {
            $profileSettings = & $script:ClientProfile.ReadSettingsFromConfiguration $configuration
            $profileConfig = [pscustomobject]@{
                ProfileSettings = $profileSettings
                ServiceAccount = $serviceAccountConfig
            }
            Grant-ClientProfileAccess -InstallerConfig $profileConfig
        }
        else {
            Write-WarnLine "Could not refresh client source ACLs because the preserved configuration was not readable."
        }
    }

    Start-SatelliteService
    Write-InfoLine "Upgrade complete."
}

function Write-Status {
    $effectiveInstallDir = Get-EffectiveInstallDir
    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    $serviceRecord = Get-ServiceRecord

    if ($null -eq $service) {
        Write-WarnLine "Service $script:ServiceName is not installed."
    }
    else {
        Write-InfoLine ("Service state: " + $service.Status)
        if ($null -ne $serviceRecord -and $serviceRecord.ProcessId -gt 0) {
            try {
                $processIdValue = [int]$serviceRecord.ProcessId
                $process = Get-Process -Id $processIdValue -ErrorAction Stop
                $uptime = (Get-Date) - $process.StartTime
                Write-InfoLine ("Uptime: " + ("{0:dd\.hh\:mm\:ss}" -f $uptime))
            }
            catch {
                Write-WarnLine "Could not determine service uptime."
            }
        }
    }

    Write-InfoLine "Install dir: $effectiveInstallDir"
    $configuration = Read-ConfigFile -InstallRoot $effectiveInstallDir
    if ($null -eq $configuration) {
        Write-WarnLine "No readable appsettings.Production.json found under the install app directory."
    }
    else {
        $configuredInstanceId = [string]$configuration.Viegard.Host.InstanceId
        $databaseHostName = [string]$configuration.Viegard.Database.Host
        $databasePort = [int]$configuration.Viegard.Database.Port
        $databaseName = [string]$configuration.Viegard.Database.Name
        $databaseSchema = [string]$configuration.Viegard.Database.Schema

        Write-InfoLine "Instance ID: $configuredInstanceId"
        Write-InfoLine "Postgres target: ${databaseHostName}:$databasePort/$databaseName schema $databaseSchema"
        if (Test-TcpConnect -ServerName $databaseHostName -PortNumber $databasePort) {
            Write-InfoLine "Postgres TCP check: reachable."
        }
        else {
            Write-WarnLine "Postgres TCP check: unreachable."
        }
    }

    $events = Get-RelevantApplicationEvents -Since (Get-Date).AddDays(-3) -MaxEvents 8
    Write-EventEntries -Events $events
}

if ([string]::IsNullOrWhiteSpace($Command)) {
    Write-Usage
    exit 1
}

Initialize-ClientProfile

if ($Command -eq "install") {
    Invoke-Install
}
elseif ($Command -eq "upgrade") {
    Invoke-Upgrade
}
elseif ($Command -eq "status") {
    Write-Status
}
else {
    Write-Usage
    exit 1
}
