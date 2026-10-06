# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7.4
<#
.SYNOPSIS
    Starts, refreshes or stops the Northridge source for the long runs (items 3 and 4): EdFi_Ods_Northridge on the
    host SQL Server, plus the Admin/Security SQL Server container and the ODS/API 7.3.2 source container on 127.0.0.1:8001
    (arms/northridge/northridge.yml). The publisher's target stays arm B's.
.DESCRIPTION
    Host side (Windows, an elevated PowerShell, a Windows login that is sysadmin on the instance):
      - restores EdFi_Ods_Northridge from the public v73 backup when the database does not exist: downloads the .7z into
        arms/northridge/.backups and extracts it with 7-Zip first;
      - applies fix-northridge-gradingperiodname.sql once (empty GradingPeriodName keys the target API rejects);
      - creates or re-passwords the SQL login the container uses (host-login.sql), turns on mixed authentication and
        TCP/IP on 1433 if they are off (restarting the service), opens the firewall for the local subnet, and sets
        'max server memory' when -SqlMaxMemoryMB is given.
    Container side: docker compose up (builds db-admin on first use), the Admin bootstrap, a restart of the API so the
    new client resolves, and a smoke test (Discovery document, token, one students page). Ends by printing the
    arm-b.local.env lines the items need (see README, "Northridge source").
.EXAMPLE
    .\Start-NorthridgeSource.ps1                        # everything; the restore is skipped when the database exists
    .\Start-NorthridgeSource.ps1 -SqlMaxMemoryMB 20480  # AWS host: leave RAM for Docker Desktop's VM
    .\Start-NorthridgeSource.ps1 -SkipHostSql           # containers only (host database already prepared)
    .\Start-NorthridgeSource.ps1 -Down                  # stop the containers (Admin volumes and host database kept)
    .\Start-NorthridgeSource.ps1 -Down -Purge           # also remove the Admin volumes; the host database stays
#>
[CmdletBinding()]
param(
    [switch] $Down,
    [switch] $Purge,
    [switch] $SkipHostSql,
    [switch] $SkipDownload,
    [string] $BackupFolder,
    [string] $SqlInstance = 'localhost',
    [int] $SqlMaxMemoryMB = 0,
    [int] $TimeoutSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/Regression.psm1') -Force

$folder = Join-Path $PSScriptRoot 'arms/northridge'
$definition = Read-EnvDefinition (Join-Path $folder 'northridge.env')
$values = $definition.Values
if ($definition.Overrides.Count -gt 0) { Write-Host "Northridge uses local overrides from northridge.local.env: $($definition.Overrides -join ', ')" }

$composeFile = Join-Path $folder 'northridge.yml'
$project = Get-EnvValue $values 'COMPOSE_PROJECT_NAME' 'apipub-reg-northridge'
$port = Get-EnvValue $values 'NORTHRIDGE_PORT' '8001'
$sourceUrl = "http://127.0.0.1:$port/"
$hostDb = Get-EnvValue $values 'HOST_NORTHRIDGE_DB' 'EdFi_Ods_Northridge'
$hostUser = Get-EnvValue $values 'HOST_SQLSERVER_USER' 'edfi_docker'
$hostPassword = Get-EnvValue $values 'HOST_SQLSERVER_PASSWORD'
$clientKey = Get-EnvValue $values 'NORTHRIDGE_KEY' 'northridgeKey'
$clientSecret = Get-EnvValue $values 'NORTHRIDGE_SECRET' 'northridgeSecret'
if (-not $hostPassword) { throw 'HOST_SQLSERVER_PASSWORD is empty in arms/northridge/northridge.env (or its .local.env overlay).' }
if (-not $BackupFolder) { $BackupFolder = Join-Path $folder '.backups' }

function Invoke-Compose
{
    param([Parameter(Mandatory)] [string[]] $ComposeArgs)

    $arguments = @('compose', '--env-file', $definition.EnvFile, '-f', $composeFile) + $ComposeArgs
    & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw "docker $($arguments -join ' ') failed ($LASTEXITCODE)." }
}

function Invoke-HostSql
{
    <#
    .SYNOPSIS
        Runs T-SQL on the host SQL Server with the current Windows login (sqlcmd -E). -Sql inline or -File; -Variables
        become sqlcmd -v values. -Scalar returns the trimmed single value (no headers). Throws on a SQL error (-b).
    #>
    param([string] $Sql, [string] $File, [string] $Database = 'master', [hashtable] $Variables = @{}, [switch] $Scalar)

    $arguments = @('-S', $SqlInstance, '-E', '-C', '-b', '-d', $Database, '-W')
    if ($Scalar) { $arguments += @('-h', '-1') }
    foreach ($key in $Variables.Keys) { $arguments += @('-v', "$key=$($Variables[$key])") }
    if ($File) { $arguments += @('-i', $File) } else { $arguments += @('-Q', $Sql) }

    $output = & sqlcmd @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed ($LASTEXITCODE) on $SqlInstance/${Database}: $($output -join "`n")" }
    if ($Scalar) { return (($output | Where-Object { $_ -and "$_".Trim() }) | Select-Object -First 1).ToString().Trim() }

    return $output
}

function Get-SevenZip
{
    $candidates = @((Get-Command '7z' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source), (Join-Path $env:ProgramFiles '7-Zip\7z.exe')) | Where-Object { $_ -and (Test-Path $_) }
    if (-not $candidates) { throw '7-Zip is required to extract the Northridge backup (7z.exe on the PATH or in Program Files).' }

    return $candidates[0]
}

function Get-NorthridgeBackupFile
{
    $backupFile = Get-EnvValue $values 'NORTHRIDGE_BACKUP_FILE' 'EdFi_Ods_Northridge_v73_20241218.bak'
    $bak = Join-Path $BackupFolder $backupFile
    if (Test-Path $bak) { return $bak }

    $url = Get-EnvValue $values 'NORTHRIDGE_BACKUP_URL'
    $archive = Join-Path $BackupFolder ([IO.Path]::GetFileName(([uri] $url).AbsolutePath))
    New-Item -ItemType Directory -Force -Path $BackupFolder | Out-Null
    if (-not (Test-Path $archive))
    {
        if ($SkipDownload) { throw "'$bak' does not exist and -SkipDownload was given; put the .bak (or the .7z) in '$BackupFolder'." }
        Write-Host "  Downloading $url (about 730 MB) ..."
        $progress = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing } finally { $ProgressPreference = $progress }
    }

    Write-Host "  Extracting $([IO.Path]::GetFileName($archive)) (about 15 GB) ..."
    $sevenZip = Get-SevenZip
    & $sevenZip x -y "-o$BackupFolder" $archive | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed ($LASTEXITCODE) extracting '$archive'." }
    if (-not (Test-Path $bak))
    {
        $extracted = @(Get-ChildItem -Path $BackupFolder -Filter '*.bak' -File)
        if ($extracted.Count -ne 1) { throw "Expected '$backupFile' after extraction; found: $($extracted.Name -join ', ')." }
        $bak = $extracted[0].FullName
    }

    return $bak
}

function Restore-NorthridgeDatabase
{
    $bak = Get-NorthridgeBackupFile

    # The service account, not the current user, reads the backup file.
    $serviceAccount = Invoke-HostSql -Scalar -Sql "SET NOCOUNT ON; SELECT TOP 1 service_account FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server (%'"
    if ($serviceAccount) { & icacls $BackupFolder /grant "${serviceAccount}:(OI)(CI)R" /T /Q | Out-Null }

    $dataPath = (Invoke-HostSql -Scalar -Sql "SET NOCOUNT ON; SELECT CONVERT(nvarchar(500), SERVERPROPERTY('InstanceDefaultDataPath'))").TrimEnd('\')
    $logPath = (Invoke-HostSql -Scalar -Sql "SET NOCOUNT ON; SELECT CONVERT(nvarchar(500), SERVERPROPERTY('InstanceDefaultLogPath'))").TrimEnd('\')
    $fileList = Invoke-HostSql -Sql "RESTORE FILELISTONLY FROM DISK = N'$bak'" -Scalar:$false
    # Rows are "LogicalName PhysicalName Type ..." (-W trims the columns); skip the header and the dashes.
    $moves = @()
    $dataIndex = 0
    foreach ($line in $fileList)
    {
        $parts = -split "$line"
        if ($parts.Count -lt 3 -or $parts[0] -in @('LogicalName', '') -or $parts[0].StartsWith('-')) { continue }
        $logical = $parts[0]
        switch ($parts[2])
        {
            'D' { $target = if ($dataIndex -eq 0) { "$dataPath\$hostDb.mdf" } else { "$dataPath\${hostDb}_$dataIndex.ndf" }; $dataIndex++ }
            'L' { $target = "$logPath\${hostDb}_log.ldf" }
            default { continue }
        }
        $moves += "MOVE N'$logical' TO N'$target'"
    }
    if ($moves.Count -lt 2) { throw "RESTORE FILELISTONLY on '$bak' did not list a data and a log file: $($fileList -join ' | ')" }

    Write-Host "  Restoring $hostDb from $([IO.Path]::GetFileName($bak)) (about 17 GB; several minutes) ..."
    Invoke-HostSql -Sql "RESTORE DATABASE [$hostDb] FROM DISK = N'$bak' WITH $($moves -join ', '), RECOVERY, STATS = 10" | ForEach-Object { Write-Host "    $_" }
    # Read-only workload: a simple recovery model and a small log keep the disk footprint near the data size.
    $logLogical = ($moves | Where-Object { $_ -like "*_log.ldf'" } | Select-Object -First 1) -replace "^MOVE N'([^']+)'.*", '$1'
    Invoke-HostSql -Sql "ALTER DATABASE [$hostDb] SET RECOVERY SIMPLE; USE [$hostDb]; DBCC SHRINKFILE (N'$logLogical', 1024) WITH NO_INFOMSGS;" | Out-Null
}

function Set-HostSqlNetworkConfiguration
{
    $restart = $false
    $registry = 'Software\Microsoft\MSSQLServer\MSSQLServer'

    $loginMode = Invoke-HostSql -Scalar -Sql "SET NOCOUNT ON; DECLARE @v int; EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'$registry', N'LoginMode', @v OUTPUT; SELECT ISNULL(@v, 0)"
    if ($loginMode -ne '2')
    {
        Write-Host '  Enabling mixed-mode authentication (SQL logins) ...'
        Invoke-HostSql -Sql "EXEC master.dbo.xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'$registry', N'LoginMode', REG_DWORD, 2" | Out-Null
        $restart = $true
    }

    $tcpEnabled = Invoke-HostSql -Scalar -Sql "SET NOCOUNT ON; DECLARE @v int; EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'$registry\SuperSocketNetLib\Tcp', N'Enabled', @v OUTPUT; SELECT ISNULL(@v, 0)"
    $tcpPort = Invoke-HostSql -Scalar -Sql "SET NOCOUNT ON; DECLARE @v nvarchar(20); EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'$registry\SuperSocketNetLib\Tcp\IPAll', N'TcpPort', @v OUTPUT; SELECT ISNULL(@v, '')"
    if ($tcpEnabled -ne '1' -or $tcpPort -ne '1433')
    {
        Write-Host '  Enabling TCP/IP on port 1433 ...'
        Invoke-HostSql -Sql @"
EXEC master.dbo.xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'$registry\SuperSocketNetLib\Tcp', N'Enabled', REG_DWORD, 1;
EXEC master.dbo.xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'$registry\SuperSocketNetLib\Tcp\IPAll', N'TcpPort', REG_SZ, N'1433';
EXEC master.dbo.xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'$registry\SuperSocketNetLib\Tcp\IPAll', N'TcpDynamicPorts', REG_SZ, N'';
"@ | Out-Null
        $restart = $true
    }

    if ($restart)
    {
        $serviceName = Invoke-HostSql -Scalar -Sql "SET NOCOUNT ON; SELECT TOP 1 servicename FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server (%'"
        if (-not (Test-Elevated)) { throw "The SQL Server authentication/network settings were changed and '$serviceName' must restart for them to apply; run this script from an elevated PowerShell (or restart the service by hand and re-run)." }
        Write-Host "  Restarting '$serviceName' so the authentication and network changes take effect ..."
        Get-Service -DisplayName $serviceName | Restart-Service -Force
        $deadline = (Get-Date).AddMinutes(3)
        while ($true)
        {
            try { Invoke-HostSql -Scalar -Sql 'SELECT 1' | Out-Null; break }
            catch { if ((Get-Date) -gt $deadline) { throw } ; Start-Sleep -Seconds 5 }
        }
    }

    $ruleName = 'API Publisher regression: SQL Server 1433 for Docker (local subnet)'
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue))
    {
        if (Test-Elevated)
        {
            Write-Host '  Adding the Windows Firewall rule for 1433 from the local subnet (Docker Desktop reaches the host that way) ...'
            New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort 1433 -RemoteAddress LocalSubnet -Action Allow -Profile Any | Out-Null
        }
        else
        {
            # A laptop that already ran the Northridge stack has whatever rule it needed; a new host gets the rule from an
            # elevated run. The smoke test at the end tells whether the container reaches the host.
            Write-Warning "Not elevated: no firewall rule added for 1433. If the smoke test fails to reach the host SQL Server, run elevated: New-NetFirewallRule -DisplayName '$ruleName' -Direction Inbound -Protocol TCP -LocalPort 1433 -RemoteAddress LocalSubnet -Action Allow -Profile Any"
        }
    }
}

function Test-Elevated
{
    return ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Initialize-HostSqlServer
{
    if (-not $IsWindows) { throw 'The host SQL Server steps run on Windows only (the runbook keeps EdFi_Ods_Northridge on the host SQL Server); use -SkipHostSql once the database is prepared another way.' }
    if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { throw 'sqlcmd was not found on the PATH (install the SQL Server command line tools).' }

    $version = Invoke-HostSql -Scalar -Sql "SET NOCOUNT ON; SELECT CONVERT(nvarchar(50), SERVERPROPERTY('ProductVersion')) + ' ' + CONVERT(nvarchar(100), SERVERPROPERTY('Edition'))"
    Write-Host "Host SQL Server $SqlInstance ($version):"

    $exists = Invoke-HostSql -Scalar -Sql "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'$hostDb') IS NULL THEN 0 ELSE 1 END"
    if ($exists -eq '1') { Write-Host "  $hostDb exists; restore skipped." } else { Restore-NorthridgeDatabase }

    $emptyNames = Invoke-HostSql -Database $hostDb -Scalar -Sql "SET NOCOUNT ON; SELECT COUNT(*) FROM edfi.GradingPeriod WHERE GradingPeriodName = ''"
    if ([int] $emptyNames -gt 0)
    {
        Write-Host "  Applying fix-northridge-gradingperiodname.sql ($emptyNames grading periods without a name) ..."
        Invoke-HostSql -Database $hostDb -File (Join-Path $folder 'fix-northridge-gradingperiodname.sql') | ForEach-Object { Write-Host "    $_" }
    }
    else { Write-Host '  GradingPeriodName fix already applied.' }

    Write-Host "  Login $hostUser (db_owner on $hostDb) ..."
    Invoke-HostSql -File (Join-Path $folder 'host-login.sql') -Variables @{ host_user = $hostUser; host_password = $hostPassword; host_db = $hostDb } | Out-Null

    Set-HostSqlNetworkConfiguration

    if ($SqlMaxMemoryMB -gt 0)
    {
        Write-Host "  max server memory = $SqlMaxMemoryMB MB"
        Invoke-HostSql -Sql "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; EXEC sp_configure 'max server memory (MB)', $SqlMaxMemoryMB; RECONFIGURE;" | Out-Null
    }
}

if ($Down)
{
    $downArgs = @('down')
    if ($Purge) { $downArgs += '-v' }
    Invoke-Compose $downArgs
    Write-Host "Northridge source stack stopped. The host database $hostDb is untouched (drop it by hand when you no longer need it)."
    return
}

if (-not $SkipHostSql) { Initialize-HostSqlServer }

Write-Host "Starting the Northridge source stack ($project) ..."
Invoke-Compose @('up', '-d', '--build', '--wait', '--wait-timeout', "$TimeoutSeconds")

Write-Host '  Applying the Admin bootstrap (ODS instance, vendor, application, API client) ...'
$edOrgs = @((Get-EnvValue $values 'NORTHRIDGE_ED_ORGS' '255900,255901') -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($edOrgs.Count -ne 2) { throw "NORTHRIDGE_ED_ORGS must list the two education organizations of the backup (LEA and ESC); got '$($edOrgs -join ',')'." }
$sqlUser = Get-EnvValue $values 'SQLSERVER_USER' 'edfi'
$sqlPassword = Get-EnvValue $values 'SQLSERVER_PASSWORD'
$variables = [ordered]@{
    host_server   = Get-EnvValue $values 'HOST_SQLSERVER_SERVER' 'host.docker.internal,1433'
    host_user     = $hostUser
    host_password = $hostPassword
    host_db       = $hostDb
    client_key    = $clientKey
    client_secret = $clientSecret
    claimset      = Get-EnvValue $values 'NORTHRIDGE_CLAIM_SET' 'Ed-Fi API Publisher - Reader'
    edorg1        = $edOrgs[0]
    edorg2        = $edOrgs[1]
}
Invoke-Compose @('cp', (Join-Path $folder 'bootstrap-northridge-mssql.sql'), 'db-admin:/tmp/bootstrap-northridge-mssql.sql')
$execArgs = @('exec', '-T', 'db-admin', '/opt/mssql-tools18/bin/sqlcmd', '-C', '-b', '-W', '-U', $sqlUser, '-P', $sqlPassword, '-d', 'EdFi_Admin', '-i', '/tmp/bootstrap-northridge-mssql.sql')
foreach ($key in $variables.Keys) { $execArgs += @('-v', "$key=$($variables[$key])") }
Invoke-Compose $execArgs

# New OdsInstances/ApiClients rows are cached by the API; restart so the token resolves immediately.
Invoke-Compose @('restart', 'api-source')
Wait-Url $sourceUrl -TimeoutSeconds 300

Write-Host '  Smoke test: token and one students page through the API ...'
$token = Get-BearerToken -BaseUrl $sourceUrl -Key $clientKey -Secret $clientSecret
$students = Invoke-Api -BaseUrl $sourceUrl -Token $token -Resource '/ed-fi/students' -Query '?limit=1'
if ($students.Count -lt 1) { throw "The Northridge API answered an empty students page; check the OdsInstances connection string (host SQL Server reachable as $($variables.host_server) with login $hostUser?)." }

Write-Host ''
Write-Host "Northridge source is up: $sourceUrl ($clientKey -> $hostDb on the host SQL Server)"
Write-Host 'For the long runs put these lines in arms/arm-b.local.env (with arm B already up; see README, "Northridge source"):'
Write-Host "  SOURCE_PORT=$port"
Write-Host "  SOURCE_KEY=$clientKey"
Write-Host "  SOURCE_SECRET=$clientSecret"
Write-Host "  PROXY_FORWARD_URL=http://host.docker.internal:$port"
