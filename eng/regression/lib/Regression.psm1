# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Shared functions for the API Publisher regression harness (APIPUB-125, APIPUB-146).

    Every item script under ../items imports this module. The functions fall into five groups:
      arms      Get-Arm, Start-RegressionArm, Stop-RegressionArm, Reset-RegressionTarget
      api       Get-ApiUrls, Get-BearerToken, Invoke-Api, Get-ResourceCount, Get-NewestChangeVersion
      publisher Resolve-Publisher, Invoke-Publisher (exe or container, memory sampling, kill trigger)
      proxy     Enable-ProxyFault, Disable-ProxyFault, Reset-ProxyMappings, Reset-ProxyJournal,
                Get-ProxyJournal, Measure-ProxyConcurrency
      results   Compare-Counts, Compare-RequestUrls, Test-LogContains, New-RunFolder, Write-ResultRow,
                Complete-Item

    Conventions (see README.md): nothing here reads developer machine state; every connection detail
    comes from the arm's .env file or from parameters, so the same functions run unchanged under the
    v1.5 GitHub Actions wrapper (APIPUB-149).
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RegressionRoot = Split-Path -Parent $PSScriptRoot
$script:ApiUrlCache = @{}
$script:TokenCache = @{}
$script:PublisherUnderTest = $null   # set by Resolve-Publisher; named in every result row

function Get-RegressionRoot
{
    return $script:RegressionRoot
}

# ----------------------------------------------------------------------------------------------------
# Arms
# ----------------------------------------------------------------------------------------------------

function Read-EnvFile
{
    param([Parameter(Mandatory)] [string] $Path)

    $values = [ordered]@{}

    foreach ($line in Get-Content -Path $Path)
    {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }

        $separator = $trimmed.IndexOf('=')
        if ($separator -lt 1) { continue }

        $key = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim()

        if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'")))
        {
            $value = $value.Substring(1, $value.Length - 2)
        }

        $values[$key] = $value
    }

    return $values
}

function Get-EnvValue
{
    param($Values, [string] $Key, [string] $Default = $null)

    if ($Values.Contains($Key) -and $Values[$Key]) { return $Values[$Key] }

    return $Default
}

<#
.SYNOPSIS
    Loads an arm definition from arms/arm-<name>.env and derives the URLs the item scripts use.
    ODS arms (ARM_TYPE=ods) run from arms/ods-arm.yml; the DMS arm (ARM_TYPE=dms) points at an
    externally started stack (see arms/arm-d-dms.md).

    An optional arms/arm-<name>.local.env (git-ignored) overrides single keys, for example SOURCE_* pointed at the
    Northridge stack, without editing the committed file. When it exists, docker compose reads a merged copy.
#>
function Get-Arm
{
    param([Parameter(Mandatory)] [string] $Name)

    $armName = $Name.Trim().ToUpperInvariant()
    $armsFolder = Join-Path $script:RegressionRoot 'arms'
    $envFile = Join-Path $armsFolder "arm-$($armName.ToLowerInvariant()).env"

    if (-not (Test-Path $envFile))
    {
        $known = @(Get-ChildItem -Path $armsFolder -Filter 'arm-*.env' -File | Where-Object { $_.Name -notlike '*.local.env' } |
            ForEach-Object { $_.BaseName.Substring(4).ToUpperInvariant() })
        throw "Unknown arm '$armName': '$envFile' does not exist. Arms defined in ${armsFolder}: $($known -join ', '). To add one, copy arm-b.env (see README, 'Adding an arm or an item')."
    }

    $values = Read-EnvFile $envFile
    $localFile = [IO.Path]::ChangeExtension($envFile, '.local.env')
    if (Test-Path $localFile)
    {
        $overrides = Read-EnvFile $localFile
        foreach ($key in $overrides.Keys) { $values[$key] = $overrides[$key] }
        Write-Host "Arm $armName uses local overrides from $([IO.Path]::GetFileName($localFile)): $($overrides.Keys -join ', ')"

        # Compose takes one --env-file per call here, so it reads a merged copy (under the git-ignored .logs folder).
        $mergedFolder = Join-Path $armsFolder '.logs'
        New-Item -ItemType Directory -Force -Path $mergedFolder | Out-Null
        $envFile = Join-Path $mergedFolder "arm-$($armName.ToLowerInvariant()).merged.env"
        ($values.Keys | ForEach-Object { "$_=$($values[$_])" }) | Set-Content $envFile
    }
    $type = Get-EnvValue $values 'ARM_TYPE' 'ods'
    $project = Get-EnvValue $values 'COMPOSE_PROJECT_NAME' "apipub-reg-$($armName.ToLowerInvariant())"

    if ($type -eq 'ods')
    {
        # 127.0.0.1, not localhost: the ports are published on IPv4 loopback only (ods-arm.yml), and 'localhost'
        # resolves to ::1 first, which costs about 2 s per request before the IPv4 fallback (measured 2026-09-29:
        # item 1's 768 count requests took half an hour).
        $sourceUrl = "http://127.0.0.1:$(Get-EnvValue $values 'SOURCE_PORT')/"
        $targetUrl = "http://127.0.0.1:$(Get-EnvValue $values 'TARGET_PORT')/"
        $sourceInNetwork = 'http://api-source/'
        $targetInNetwork = 'http://api-target/'
        $network = "${project}_default"
    }
    else
    {
        $sourceUrl = Get-EnvValue $values 'SOURCE_URL'
        $targetUrl = Get-EnvValue $values 'TARGET_URL'
        $sourceInNetwork = Get-EnvValue $values 'SOURCE_URL_IN_NETWORK' $sourceUrl
        $targetInNetwork = Get-EnvValue $values 'TARGET_URL_IN_NETWORK' $targetUrl
        $network = Get-EnvValue $values 'DOCKER_NETWORK' 'bridge'
    }

    $proxyPort = Get-EnvValue $values 'PROXY_PORT'

    return [pscustomobject]@{
        Name               = $armName
        Type               = $type
        Description        = Get-EnvValue $values 'ARM_DESCRIPTION' $armName
        EnvFile            = $envFile
        Env                = $values
        ComposeFile        = Join-Path $script:RegressionRoot 'arms/ods-arm.yml'
        Project            = $project
        Network            = $network
        SourceUrl          = $sourceUrl
        TargetUrl          = $targetUrl
        SourceUrlInNetwork = $sourceInNetwork
        TargetUrlInNetwork = $targetInNetwork
        ProxyUrl           = if ($proxyPort) { "http://127.0.0.1:$proxyPort/" } else { $null }
        ProxyAdminUrl      = if ($proxyPort) { "http://127.0.0.1:$proxyPort/__admin" } else { $null }
        ProxyUrlInNetwork  = 'http://proxy:8080/'
        SourceKey          = Get-EnvValue $values 'SOURCE_KEY'
        SourceSecret       = Get-EnvValue $values 'SOURCE_SECRET'
        TargetKey          = Get-EnvValue $values 'TARGET_KEY'
        TargetSecret       = Get-EnvValue $values 'TARGET_SECRET'
        SourceOdsDatabase  = Get-EnvValue $values 'SOURCE_ODS_DB' 'EdFi_Ods_Populated'
        TargetOdsDatabase  = Get-EnvValue $values 'TARGET_ODS_DB' 'EdFi_Ods_Minimal'
    }
}

function Invoke-ComposeArm
{
    param([Parameter(Mandatory)] $Arm, [Parameter(Mandatory)] [string[]] $ComposeArgs)

    if ($Arm.Type -ne 'ods') { throw "Arm $($Arm.Name) is not compose-managed (see arms/arm-d-dms.md)." }

    $arguments = @('compose', '--env-file', $Arm.EnvFile, '-f', $Arm.ComposeFile, '--profile', 'proxy') + $ComposeArgs
    & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw "docker $($arguments -join ' ') failed ($LASTEXITCODE)." }
}

function Invoke-ArmPsql
{
    <#
    .SYNOPSIS
        Runs a psql command inside one of the arm's database containers (db-ods or db-admin).
        -Sql runs inline; -File copies a script into the container first. -Variables become psql -v values.
    #>
    param(
        [Parameter(Mandatory)] $Arm,
        [ValidateSet('db-ods', 'db-admin')] [string] $Service = 'db-ods',
        [string] $Database = 'postgres',
        [string] $Sql,
        [string] $File,
        [hashtable] $Variables = @{},
        [switch] $TuplesOnly
    )

    $user = Get-EnvValue $Arm.Env 'POSTGRES_USER' 'postgres'
    $flags = @('-v', 'ON_ERROR_STOP=1', '-U', $user, '-d', $Database, '-q')
    if ($TuplesOnly) { $flags += @('-tA') }
    foreach ($key in $Variables.Keys) { $flags += @('-v', "$key=$($Variables[$key])") }

    if ($File)
    {
        $containerPath = "/tmp/$([IO.Path]::GetFileName($File))"
        Invoke-ComposeArm $Arm @('cp', $File, "${Service}:$containerPath")
        $flags += @('-f', $containerPath)
    }
    else
    {
        $flags += @('-c', $Sql)
    }

    $arguments = @('compose', '--env-file', $Arm.EnvFile, '-f', $Arm.ComposeFile, '--profile', 'proxy', 'exec', '-T', $Service, 'psql') + $flags
    $output = & docker @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql in $Service failed ($LASTEXITCODE): $output" }

    return $output
}

function Wait-Url
{
    param([Parameter(Mandatory)] [string] $Url, [int] $TimeoutSeconds = 300, [int] $ExpectedStatus = 200)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline)
    {
        try
        {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 10 -SkipHttpErrorCheck
            if ([int] $response.StatusCode -eq $ExpectedStatus) { return }
        }
        catch { }

        Start-Sleep -Seconds 3
    }

    throw "Timed out after ${TimeoutSeconds}s waiting for '$Url' to answer $ExpectedStatus."
}

function New-OdsDatabaseFromTemplate
{
    param([Parameter(Mandatory)] $Arm, [Parameter(Mandatory)] [string] $Database, [Parameter(Mandatory)] [string] $Template)

    $exists = (Invoke-ArmPsql $Arm -Service db-ods -TuplesOnly -Sql "select 1 from pg_database where datname = '$Database'") -join ''

    if ($exists.Trim() -eq '1')
    {
        Write-Host "  $Database already exists."
        return
    }

    Write-Host "  Creating $Database from $Template ..."
    Invoke-ArmPsql $Arm -Service db-ods -Sql "create database `"$Database`" template `"$Template`"" | Out-Null
}

function Get-RootEducationOrganizationIds
{
    <#
    .SYNOPSIS
        The education organizations of the source template that no other organization contains (LEA, ESC, standalone
        schools, community and post-secondary organizations). Associating the API clients with these lets
        relationship-based authorization reach every document in the template; the Grand Bend TPDM sample data hangs
        off organizations outside the LEA, and a client limited to the LEA cannot read them (first live run of item 1).
    #>
    param([Parameter(Mandatory)] $Arm)

    $sql = "select string_agg(e.educationorganizationid::text, ',' order by e.educationorganizationid) from edfi.educationorganization e " +
        "where not exists (select 1 from auth.educationorganizationidtoeducationorganizationid a " +
        "where a.targeteducationorganizationid = e.educationorganizationid and a.sourceeducationorganizationid <> e.educationorganizationid)"
    $ids = ((Invoke-ArmPsql $Arm -Service db-ods -Database $Arm.SourceOdsDatabase -TuplesOnly -Sql $sql) -join '').Trim()

    # No silent default: a guessed list (the Grand Bend ids) would bootstrap clients that cannot read another source,
    # and the run would only show it much later as count mismatches.
    if (-not $ids) { throw "No top-level education organizations found in $($Arm.SourceOdsDatabase); the API clients cannot be associated. Is the source template populated?" }

    return $ids
}

<#
.SYNOPSIS
    Brings an ODS arm up from Docker Hub images: two API containers (source over the populated template,
    target over the minimal template), one database container per role, and the WireMock fault proxy.
    Idempotent: re-running restarts containers and re-applies the Admin bootstrap.
#>
function Start-RegressionArm
{
    param([Parameter(Mandatory)] $Arm, [int] $TimeoutSeconds = 900)

    if ($Arm.Type -ne 'ods')
    {
        Write-Host "Arm $($Arm.Name) is external ($($Arm.Description)); nothing to start. Checking the endpoints ..."
        Wait-Url $Arm.SourceUrl -TimeoutSeconds 30
        Wait-Url $Arm.TargetUrl -TimeoutSeconds 30
        return
    }

    Write-Host "Starting arm $($Arm.Name): $($Arm.Description)"
    Invoke-ComposeArm $Arm @('up', '-d', '--wait', '--wait-timeout', "$TimeoutSeconds")

    # The sandbox image marks both shipped templates datistemplate=true/datallowconn=false, so the API
    # cannot use them directly; clone each into a connectable database once.
    New-OdsDatabaseFromTemplate $Arm -Database $Arm.SourceOdsDatabase -Template 'EdFi_Ods_Populated_Template'
    New-OdsDatabaseFromTemplate $Arm -Database $Arm.TargetOdsDatabase -Template 'EdFi_Ods_Minimal_Template'

    Write-Host '  Applying the Admin bootstrap (ODS instances, vendor, applications, API clients) ...'
    $edOrgs = Get-RootEducationOrganizationIds $Arm
    Write-Host "  Education organizations the clients are associated with: $edOrgs"
    $variables = @{
        edorgs          = $edOrgs
        pw              = Get-EnvValue $Arm.Env 'POSTGRES_PASSWORD'
        source_db       = $Arm.SourceOdsDatabase
        target_db       = $Arm.TargetOdsDatabase
        source_key      = $Arm.SourceKey
        source_secret   = $Arm.SourceSecret
        target_key      = $Arm.TargetKey
        target_secret   = $Arm.TargetSecret
        source_claimset = Get-EnvValue $Arm.Env 'SOURCE_CLAIM_SET' 'Ed-Fi Sandbox'
        target_claimset = Get-EnvValue $Arm.Env 'TARGET_CLAIM_SET' 'Ed-Fi Sandbox'
    }
    Invoke-ArmPsql $Arm -Service db-admin -Database 'EdFi_Admin' -File (Join-Path $script:RegressionRoot 'arms/bootstrap-pgsql.sql') -Variables $variables | Out-Null

    # New OdsInstances/ApiClients rows are cached by the API; restart both so tokens resolve immediately.
    Invoke-ComposeArm $Arm @('restart', 'api-source', 'api-target')
    Wait-Url $Arm.SourceUrl -TimeoutSeconds 300
    Wait-Url $Arm.TargetUrl -TimeoutSeconds 300
    if ($Arm.ProxyAdminUrl) { Wait-Url "$($Arm.ProxyAdminUrl)/mappings" -TimeoutSeconds 120 }

    Write-Host ''
    Write-Host "Arm $($Arm.Name) is up:"
    Write-Host "  source  $($Arm.SourceUrl)  ($($Arm.SourceKey) -> $($Arm.SourceOdsDatabase))"
    Write-Host "  target  $($Arm.TargetUrl)  ($($Arm.TargetKey) -> $($Arm.TargetOdsDatabase))"
    if ($Arm.ProxyUrl) { Write-Host "  proxy   $($Arm.ProxyUrl)  (WireMock, forwards to the source; admin at $($Arm.ProxyAdminUrl))" }
}

function Stop-RegressionArm
{
    param([Parameter(Mandatory)] $Arm, [switch] $Purge)

    if ($Arm.Type -ne 'ods') { Write-Host "Arm $($Arm.Name) is external; nothing to stop."; return }

    $downArgs = @('down')
    if ($Purge) { $downArgs += '-v' }
    Invoke-ComposeArm $Arm $downArgs
}

<#
.SYNOPSIS
    Puts the arm's target back to the empty minimal template so that items start from a known state.
    Drops and recreates the target database from the shipped template, then restarts the target API.
#>
function Reset-RegressionTarget
{
    param([Parameter(Mandatory)] $Arm)

    if ($Arm.Type -ne 'ods')
    {
        # An external target is reset by the command its .env names (see arms/arm-d-dms.md), if any. Items that need
        # an empty target check for it themselves (D3), so a missing reset shows as a failed check, not as counts
        # that match on data an earlier item published.
        $command = Get-EnvValue $Arm.Env 'TARGET_RESET_COMMAND'
        if (-not $command)
        {
            Write-Warning "Arm $($Arm.Name) is external and names no TARGET_RESET_COMMAND; the target is not reset (see arms/arm-d-dms.md)."
            return
        }

        Write-Host "  Resetting the external target: $command"
        & pwsh -NoProfile -Command $command
        if ($LASTEXITCODE -ne 0) { throw "TARGET_RESET_COMMAND failed ($LASTEXITCODE): $command" }
        Wait-Url $Arm.TargetUrl -TimeoutSeconds 300

        return
    }

    Write-Host "  Resetting target database $($Arm.TargetOdsDatabase) from the minimal template ..."
    Invoke-ComposeArm $Arm @('stop', 'api-target')
    Invoke-ArmPsql $Arm -Service db-ods -Sql "select pg_terminate_backend(pid) from pg_stat_activity where datname = '$($Arm.TargetOdsDatabase)' and pid <> pg_backend_pid()" | Out-Null
    Invoke-ArmPsql $Arm -Service db-ods -Sql "drop database if exists `"$($Arm.TargetOdsDatabase)`"" | Out-Null
    Invoke-ArmPsql $Arm -Service db-ods -Sql "create database `"$($Arm.TargetOdsDatabase)`" template `"EdFi_Ods_Minimal_Template`"" | Out-Null
    Invoke-ComposeArm $Arm @('start', 'api-target')
    Wait-Url $Arm.TargetUrl -TimeoutSeconds 300
}

<#
.SYNOPSIS
    Puts the arm's source back to the shipped populated template, undoing the edits items 1 and 8 make (an edited
    student, deleted calendar dates, re-keyed class periods). Same mechanism as Reset-RegressionTarget; the Admin
    bootstrap is not repeated because the database keeps its name.
#>
function Reset-RegressionSource
{
    param([Parameter(Mandatory)] $Arm)

    if ($Arm.Type -ne 'ods') { throw "Arm $($Arm.Name) is external; its source is arm B's (reset that arm instead)." }

    Write-Host "  Resetting source database $($Arm.SourceOdsDatabase) from the populated template ..."
    Invoke-ComposeArm $Arm @('stop', 'api-source')
    Invoke-ArmPsql $Arm -Service db-ods -Sql "select pg_terminate_backend(pid) from pg_stat_activity where datname = '$($Arm.SourceOdsDatabase)' and pid <> pg_backend_pid()" | Out-Null
    Invoke-ArmPsql $Arm -Service db-ods -Sql "drop database if exists `"$($Arm.SourceOdsDatabase)`"" | Out-Null
    Invoke-ArmPsql $Arm -Service db-ods -Sql "create database `"$($Arm.SourceOdsDatabase)`" template `"EdFi_Ods_Populated_Template`"" | Out-Null
    Invoke-ComposeArm $Arm @('start', 'api-source')
    Wait-Url $Arm.SourceUrl -TimeoutSeconds 300
}

# ----------------------------------------------------------------------------------------------------
# API helpers
# ----------------------------------------------------------------------------------------------------

<#
.SYNOPSIS
    Reads the Discovery document of an ODS/API or DMS instance and returns the URLs it advertises
    (dataManagementApi, oauth, changeQueries). Cached per base URL for the session.
#>
function Get-ApiUrls
{
    param([Parameter(Mandatory)] [string] $BaseUrl)

    $key = $BaseUrl.TrimEnd('/')
    if ($script:ApiUrlCache.ContainsKey($key)) { return $script:ApiUrlCache[$key] }

    $discovery = Invoke-RestMethod -Uri "$key/" -TimeoutSec 60
    $urls = $discovery.urls

    $result = [pscustomobject]@{
        BaseUrl           = "$key/"
        Version           = $discovery.version
        Suite             = $discovery.suite
        DataManagementApi = ("$($urls.dataManagementApi)").TrimEnd('/')
        Oauth             = "$($urls.oauth)"
        ChangeQueries     = if ($urls.PSObject.Properties['changeQueries']) { ("$($urls.changeQueries)").TrimEnd('/') } else { $null }
        Discovery         = $discovery
    }

    if (-not $result.DataManagementApi) { $result.DataManagementApi = "$key/data/v3" }
    if (-not $result.Oauth) { $result.Oauth = "$key/oauth/token" }

    $script:ApiUrlCache[$key] = $result

    return $result
}

function Get-JwtIssuer
{
    # The iss claim of a JWT bearer token, or $null for an opaque token or one that does not decode. Used by D2 and D4
    # to tell the identity provider apart: the DMS advertises its own oauth address in both modes (APIPUB-146), and a
    # Keycloak token's issuer is a realm URL (.../realms/<realm>).
    param([string] $Token)

    if (-not $Token -or $Token.Split('.').Count -ne 3) { return $null }

    $payload = $Token.Split('.')[1].Replace('-', '+').Replace('_', '/')
    $payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
    try { return ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json).iss } catch { return $null }
}

function Get-BearerToken
{
    # Tokens are cached per URL and key: the ODS limits live tokens per client, and the helpers should not compete
    # with the publisher under test for them.
    param([Parameter(Mandatory)] [string] $BaseUrl, [Parameter(Mandatory)] [string] $Key, [Parameter(Mandatory)] [string] $Secret)

    $cacheKey = "$($BaseUrl.TrimEnd('/'))|$Key"
    if ($script:TokenCache.ContainsKey($cacheKey) -and $script:TokenCache[$cacheKey].Expires -gt (Get-Date)) { return $script:TokenCache[$cacheKey].Token }

    $urls = Get-ApiUrls $BaseUrl
    $response = Invoke-RestMethod -Method Post -Uri $urls.Oauth -Body @{ grant_type = 'client_credentials'; client_id = $Key; client_secret = $Secret } -TimeoutSec 60
    # Cached for half the lifetime the API reports (at most 20 minutes), so a short-lived token used by item 4 is not
    # served after it expired.
    $lifetimeSeconds = if ($response.PSObject.Properties['expires_in'] -and $response.expires_in) { [double] $response.expires_in } else { 2400 }
    $script:TokenCache[$cacheKey] = @{ Token = $response.access_token; Expires = (Get-Date).AddSeconds([math]::Min($lifetimeSeconds / 2, 1200)) }

    return $response.access_token
}

function Invoke-Api
{
    <#
    .SYNOPSIS
        One authenticated request against a resource path (e.g. '/ed-fi/students') below dataManagementApi.
        Returns the parsed body for GET, the raw response for everything else (so the caller can read headers).
    #>
    param(
        [Parameter(Mandatory)] [string] $BaseUrl,
        [Parameter(Mandatory)] [string] $Token,
        [Parameter(Mandatory)] [string] $Resource,
        [ValidateSet('GET', 'POST', 'PUT', 'DELETE')] [string] $Method = 'GET',
        [string] $Query = '',
        $Body = $null
    )

    $urls = Get-ApiUrls $BaseUrl
    $uri = "$($urls.DataManagementApi)$Resource$Query"
    $headers = @{ Authorization = "Bearer $Token" }

    if ($Method -eq 'GET')
    {
        # An empty JSON array comes back as $null, and @($null) has a Count of 1; filtering nulls gives callers a
        # real empty collection to count.
        $result = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 300
        return @($result | Where-Object { $null -ne $_ })
    }

    $json = if ($null -ne $Body) { $Body | ConvertTo-Json -Depth 20 } else { $null }

    return Invoke-WebRequest -Method $Method -Uri $uri -Headers $headers -ContentType 'application/json' -Body $json -UseBasicParsing -TimeoutSec 300
}

function Get-ResourceCount
{
    param([Parameter(Mandatory)] [string] $BaseUrl, [Parameter(Mandatory)] [string] $Token, [Parameter(Mandatory)] [string] $Resource)

    $urls = Get-ApiUrls $BaseUrl

    try
    {
        $response = Invoke-WebRequest -Uri "$($urls.DataManagementApi)$Resource`?offset=0&limit=1&totalCount=true" -Headers @{ Authorization = "Bearer $Token" } -UseBasicParsing -TimeoutSec 300
        return [int] ($response.Headers['Total-Count'] | Select-Object -First 1)
    }
    catch
    {
        $status = if ($_.Exception.Response) { [int] $_.Exception.Response.StatusCode } else { 'n/a' }
        return "ERR $status"
    }
}

function Get-ApiResources
{
    # The resource paths an API lists in its Discovery-advertised dependencies document, in dependency order.
    param([Parameter(Mandatory)] [string] $BaseUrl, [Parameter(Mandatory)] [string] $Token, [switch] $ExcludeDescriptors)

    $urls = Get-ApiUrls $BaseUrl
    $dependenciesUrl = if ($urls.Discovery.urls.PSObject.Properties['dependencies']) { "$($urls.Discovery.urls.dependencies)" } else { "$($BaseUrl.TrimEnd('/'))/metadata/data/v3/dependencies" }
    $dependencies = Invoke-RestMethod -Uri $dependenciesUrl -Headers @{ Authorization = "Bearer $Token" } -TimeoutSec 120

    $resources = @($dependencies | ForEach-Object { $_.resource } | Where-Object { $_ } | Select-Object -Unique)
    if ($ExcludeDescriptors) { $resources = @($resources | Where-Object { $_ -notmatch 'Descriptors$' }) }

    return $resources
}

function Get-NewestChangeVersion
{
    param([Parameter(Mandatory)] [string] $BaseUrl, [Parameter(Mandatory)] [string] $Token)

    $urls = Get-ApiUrls $BaseUrl
    if (-not $urls.ChangeQueries) { return $null }

    $versions = Invoke-RestMethod -Uri "$($urls.ChangeQueries)/availableChangeVersions" -Headers @{ Authorization = "Bearer $Token" } -TimeoutSec 60

    return [long] $versions.newestChangeVersion
}

# ----------------------------------------------------------------------------------------------------
# Publisher runs
# ----------------------------------------------------------------------------------------------------

<#
.SYNOPSIS
    Describes the publisher under test: a local build (-Path to EdFiApiPublisher.exe or .dll) or a Docker
    image (-Image, e.g. edfialliance/ods-api-publisher:pre). Exactly one must be given.
#>
function Resolve-Publisher
{
    # -Baseline resolves a second publisher (item 9's v1.3 image) without making it the one the result rows name.
    param([string] $Path, [string] $Image, [switch] $Baseline)

    if ($Path -and $Image) { throw 'Give either -PublisherPath or -PublisherImage, not both.' }

    if ($Path)
    {
        $resolved = (Resolve-Path $Path).Path
        $publisher = [pscustomobject]@{ Mode = 'exe'; Path = $resolved; Image = $null; Folder = (Split-Path -Parent $resolved); Label = $resolved; Identity = (Get-PublisherIdentity -Path $resolved) }
    }
    elseif ($Image)
    {
        $publisher = [pscustomobject]@{ Mode = 'docker'; Path = $null; Image = $Image; Folder = $null; Label = $Image; Identity = (Get-PublisherIdentity -Image $Image) }
    }
    else
    {
        throw 'The publisher under test is required: -PublisherPath <EdFiApiPublisher.exe|.dll> or -PublisherImage <tag>.'
    }

    if (-not $Baseline) { $script:PublisherUnderTest = $publisher }

    return $publisher
}

function Get-PublisherIdentity
{
    <#
    .SYNOPSIS
        What a result row records about the publisher, so the row can be tied to the release candidate: the image and
        its digest for a container, the product version (and the nupkg it came from, see Expand-PublisherPackage) for
        a local build.
    #>
    param([string] $Path, [string] $Image)

    if ($Image)
    {
        $inspect = { & docker image inspect -f '{{if .RepoDigests}}{{index .RepoDigests 0}}{{else}}{{.Id}}{{end}}' $Image 2>$null }
        $digest = & $inspect
        if ($LASTEXITCODE -ne 0 -or -not $digest)
        {
            # Pull now rather than let docker run do it later: the identity is resolved once, before the first run,
            # and every row of the run would otherwise go without the digest that ties it to the RC.
            Write-Host "Pulling $Image to record its digest ..."
            & docker pull --quiet $Image | Out-Null
            $digest = & $inspect
        }
        if ($LASTEXITCODE -ne 0 -or -not $digest) { return "$Image (not found locally or in its registry)" }
        $digest = "$digest".Trim()

        return "$Image ($($digest -replace '^.*@', ''))"
    }

    $version = (Get-Item $Path).VersionInfo.ProductVersion
    $identity = if ($version) { "EdFiApiPublisher $version" } else { [IO.Path]::GetFileName($Path) }

    # A publisher extracted from a nupkg by Expand-PublisherPackage carries the package name one folder up.
    $marker = Join-Path (Split-Path -Parent (Split-Path -Parent $Path)) 'package.txt'
    if (Test-Path $marker) { $identity += " from $((Get-Content $marker -TotalCount 1).Trim())" }

    return $identity
}

<#
.SYNOPSIS
    Extracts a release candidate nupkg (build.ps1 -Command Package) into results/runs/.packages/<package>/ and returns
    the publisher inside it: EdFi.ApiPublisher.Win64/EdFiApiPublisher.exe on Windows, EdFi.ApiPublisher/
    EdFiApiPublisher.dll elsewhere. Re-uses an earlier extraction of the same file (same SHA-256).
#>
function Expand-PublisherPackage
{
    param([Parameter(Mandatory)] [string] $Package, [string] $RunRoot)

    $packageFile = (Resolve-Path $Package).Path
    $hash = (Get-FileHash -Algorithm SHA256 $packageFile).Hash.ToLowerInvariant()
    $root = if ($RunRoot) { $RunRoot } else { Join-Path $script:RegressionRoot 'results/runs' }
    $folder = Join-Path $root ".packages/$([IO.Path]::GetFileNameWithoutExtension($packageFile))"
    $marker = Join-Path $folder 'package.txt'

    if (-not ((Test-Path $marker) -and ((Get-Content $marker) -contains "sha256 $hash")))
    {
        if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        Write-Host "Extracting $([IO.Path]::GetFileName($packageFile)) to $folder ..."
        # A nupkg is a zip; ZipFile does not look at the extension, which older Expand-Archive versions reject.
        [IO.Compression.ZipFile]::ExtractToDirectory($packageFile, $folder)
        @([IO.Path]::GetFileName($packageFile), "sha256 $hash") | Set-Content $marker
    }

    $candidates = if ($IsWindows) { @('EdFi.ApiPublisher.Win64/EdFiApiPublisher.exe', 'EdFi.ApiPublisher/EdFiApiPublisher.dll') } else { @('EdFi.ApiPublisher/EdFiApiPublisher.dll') }
    foreach ($candidate in $candidates)
    {
        $path = Join-Path $folder $candidate
        if (Test-Path $path) { return (Resolve-Path $path).Path }
    }

    throw "No publisher found in $packageFile (looked for $($candidates -join ', '))."
}

function New-RunFolder
{
    param([Parameter(Mandatory)] [string] $Item, [Parameter(Mandatory)] [string] $ArmName, [string] $RunRoot)

    $root = if ($RunRoot) { $RunRoot } else { Join-Path $script:RegressionRoot 'results/runs' }
    $folder = Join-Path $root ("{0}-{1}-{2}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Item, $ArmName.ToLowerInvariant())
    New-Item -ItemType Directory -Force -Path $folder | Out-Null

    return (Resolve-Path $folder).Path
}

function ConvertTo-NetworkUrl
{
    # Maps a host-facing URL (localhost:port) to the name the publisher container must use on the arm's network.
    param($Arm, [string] $Url)

    if (-not $Url) { return $Url }
    $normalized = $Url.TrimEnd('/') + '/'

    # An external arm that leaves *_URL_IN_NETWORK empty (arm D's source) maps to its host-facing URL, so the
    # localhost rewrite below still has to apply to the mapped value.
    $mapped = if ($normalized -eq $Arm.SourceUrl) { $Arm.SourceUrlInNetwork }
        elseif ($normalized -eq $Arm.TargetUrl) { $Arm.TargetUrlInNetwork }
        elseif ($Arm.ProxyUrl -and $normalized -eq $Arm.ProxyUrl) { $Arm.ProxyUrlInNetwork }
    if ($mapped) { $Url = $mapped; $normalized = $mapped.TrimEnd('/') + '/' }

    if ($normalized -match '^https?://(localhost|127\.0\.0\.1)[:/]')
    {
        Write-Warning "URL '$Url' points at localhost; inside the container it is rewritten to host.docker.internal."
        return ($normalized -replace '^(https?://)(localhost|127\.0\.0\.1)', '${1}host.docker.internal')
    }

    return $Url
}

function Write-PublisherConfig
{
    # Renders the four settings files the publisher reads from its working folder into $Folder.
    param([Parameter(Mandatory)] [string] $Folder, [string] $LogLevel, [string] $LogPath, [string] $ConfigurationStoreConnectionString)

    $source = Join-Path $script:RegressionRoot 'lib/publisher-config'
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null

    Copy-Item (Join-Path $source 'apiPublisherSettings.json') (Join-Path $Folder 'apiPublisherSettings.json') -Force
    Copy-Item (Join-Path $source 'plainTextNamedConnections.json') (Join-Path $Folder 'plainTextNamedConnections.json') -Force

    $store = Get-Content (Join-Path $source 'configurationStoreSettings.json') -Raw
    if ($ConfigurationStoreConnectionString) { $store = $store.Replace('__POSTGRESQL_CONNECTION_STRING__', $ConfigurationStoreConnectionString) }
    else { $store = $store.Replace('__POSTGRESQL_CONNECTION_STRING__', 'Host=localhost;Database=edfi_api_publisher_configuration') }
    Set-Content -Path (Join-Path $Folder 'configurationStoreSettings.json') -Value $store -NoNewline

    $logging = Get-Content (Join-Path $source 'logging.template.json') -Raw
    $logging = $logging.Replace('__LEVEL__', $LogLevel).Replace('__PATH__', ($LogPath -replace '\\', '/'))
    Set-Content -Path (Join-Path $Folder 'logging.json') -Value $logging -NoNewline
}

<#
.SYNOPSIS
    Runs the publisher once and returns what every item needs: exit code, wall clock, peak memory and the log path.

.DESCRIPTION
    Exe mode: the process is started with stdout/stderr redirected to the run folder and its working set is sampled
    every second (the approach of eng/Compare-PagingParity.ps1). Docker mode: a container is started on the arm's
    network with the settings files mounted from the run folder, and 'docker stats' is sampled instead; -MemoryLimit
    applies the container limit the memory item needs.

    -Tick runs every sample with a hashtable {Seconds, Log, Arm}; items use it to switch proxy faults on and off
    mid-run. -KillWhen receives the same hashtable and returns $true to kill the process (the resume item).
#>
function Invoke-Publisher
{
    param(
        [Parameter(Mandatory)] $Publisher,
        [Parameter(Mandatory)] $Arm,
        [Parameter(Mandatory)] [string] $RunFolder,
        [string] $LogName = 'publisher.log',
        [string] $SourceUrl, [string] $SourceKey, [string] $SourceSecret,
        [string] $TargetUrl, [string] $TargetKey, [string] $TargetSecret,
        [switch] $NoConnectionArguments,
        [string[]] $Arguments = @(),
        [ValidateSet('Information', 'Debug')] [string] $LogLevel = 'Information',
        [string] $MemoryLimit,
        [hashtable] $Environment = @{},
        [string] $ConfigurationStoreConnectionString,
        [int] $SampleSeconds = 1,
        [int] $TimeoutMinutes = 0,
        [int] $HeartbeatMinutes = 5,
        [scriptblock] $Tick,
        [scriptblock] $KillWhen
    )

    if (-not $SourceUrl) { $SourceUrl = $Arm.SourceUrl }
    if (-not $SourceKey) { $SourceKey = $Arm.SourceKey }
    if (-not $SourceSecret) { $SourceSecret = $Arm.SourceSecret }
    if (-not $TargetUrl) { $TargetUrl = $Arm.TargetUrl }
    if (-not $TargetKey) { $TargetKey = $Arm.TargetKey }
    if (-not $TargetSecret) { $TargetSecret = $Arm.TargetSecret }

    $log = Join-Path $RunFolder $LogName
    $errorLog = [IO.Path]::ChangeExtension($log, '.err.log')
    $memoryCsv = [IO.Path]::ChangeExtension($log, '.memory.csv')
    $argumentsFile = [IO.Path]::ChangeExtension($log, '.args.txt')

    if ($Publisher.Mode -eq 'docker')
    {
        $SourceUrl = ConvertTo-NetworkUrl $Arm $SourceUrl
        $TargetUrl = ConvertTo-NetworkUrl $Arm $TargetUrl
    }

    $connectionArguments = @()
    if (-not $NoConnectionArguments)
    {
        $connectionArguments = @(
            "--sourceUrl=$SourceUrl", "--sourceKey=$SourceKey", "--sourceSecret=$SourceSecret",
            "--targetUrl=$TargetUrl", "--targetKey=$TargetKey", "--targetSecret=$TargetSecret"
        )
        # A change window (and with it --lastChangeVersionProcessed, deletes and key changes) is only established
        # for NAMED connections, so both sides get a name unless the item chose its own.
        if (-not ($Arguments | Where-Object { $_ -like '--sourceName=*' })) { $connectionArguments += "--sourceName=regression-source-$($Arm.Name.ToLowerInvariant())" }
        if (-not ($Arguments | Where-Object { $_ -like '--targetName=*' })) { $connectionArguments += "--targetName=regression-target-$($Arm.Name.ToLowerInvariant())" }
    }
    # The ODS/API 7.x images have no snapshot connection configured, so every read the publisher makes with the
    # Use-Snapshot header answers 404 "Snapshot not found" (seen on the first live run of item 1). Isolation is
    # therefore off unless the item sets --ignoreIsolation itself (the pre-7 stub is the only case that wants it on).
    if (-not ($Arguments | Where-Object { $_ -like '--ignoreIsolation=*' })) { $Arguments = @('--ignoreIsolation=true') + $Arguments }
    $allArguments = $connectionArguments + $Arguments

    # Secrets and passwords are not written to the args file; everything else is, so a run can be repeated by hand.
    ($allArguments | ForEach-Object { $_ -replace '(?i)^(--\w*(Secret|Password)=).*$', '$1<redacted>' }) -join "`n" | Set-Content $argumentsFile

    'elapsed_s,memory_mb' | Set-Content $memoryCsv
    # Long runs (items 3 and 4 on Northridge take hours) print a heartbeat, so a quiet console is not mistaken for a hang.
    $script:nextHeartbeat = $HeartbeatMinutes * 60
    $heartbeat = {
        param([double] $elapsedSeconds, [long] $bytes)
        if ($HeartbeatMinutes -le 0 -or $elapsedSeconds -lt $script:nextHeartbeat) { return }
        $script:nextHeartbeat += $HeartbeatMinutes * 60
        $done = Get-LogMatchCount $log "Streaming of '[^']+' completed"
        Write-Host ("  ... {0} elapsed, {1:N0} MB, {2} resource(s) streamed so far ({3})" -f (Format-Duration $elapsedSeconds), ($bytes / 1MB), $done, [IO.Path]::GetFileName($log))
    }
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $peakBytes = 0L
    $killed = $false
    $exitCode = $null
    $deadline = if ($TimeoutMinutes -gt 0) { (Get-Date).AddMinutes($TimeoutMinutes) } else { [datetime]::MaxValue }

    if ($Publisher.Mode -eq 'exe')
    {
        # The publisher reads logging.json from its own folder, so a Debug run swaps that file for the duration of
        # the run and restores it afterwards (the container run mounts it instead).
        $loggingFile = Join-Path $Publisher.Folder 'logging.json'
        $loggingBackup = $null

        # A backup left behind means an earlier run was killed before its finally block: the backup is the original
        # file and the live one is the harness copy. Put the original back before taking a new backup, so a second
        # swap cannot overwrite the only original.
        foreach ($name in 'logging.json', 'configurationStoreSettings.json')
        {
            $live = Join-Path $Publisher.Folder $name
            if (Test-Path "$live.regression-backup")
            {
                Write-Warning "Restoring $name in $($Publisher.Folder) from the backup an interrupted run left behind."
                Move-Item "$live.regression-backup" $live -Force
            }
        }
        if ($LogLevel -ne 'Information' -or $ConfigurationStoreConnectionString)
        {
            $staging = Join-Path $RunFolder 'config'
            Write-PublisherConfig -Folder $staging -LogLevel $LogLevel -LogPath (Join-Path $RunFolder 'publisher.serilog.log') -ConfigurationStoreConnectionString $ConfigurationStoreConnectionString
            $loggingBackup = "$loggingFile.regression-backup"
            Copy-Item $loggingFile $loggingBackup -Force
            Copy-Item (Join-Path $staging 'logging.json') $loggingFile -Force
            if ($ConfigurationStoreConnectionString)
            {
                $storeFile = Join-Path $Publisher.Folder 'configurationStoreSettings.json'
                Copy-Item $storeFile "$storeFile.regression-backup" -Force
                Copy-Item (Join-Path $staging 'configurationStoreSettings.json') $storeFile -Force
            }
        }

        try
        {
            if ($Publisher.Path -like '*.dll') { $exe = 'dotnet'; $argumentList = @($Publisher.Path) + $allArguments }
            else { $exe = $Publisher.Path; $argumentList = $allArguments }

            $startInfo = @{ FilePath = $exe; ArgumentList = $argumentList; PassThru = $true; NoNewWindow = $true; RedirectStandardOutput = $log; RedirectStandardError = $errorLog; WorkingDirectory = $Publisher.Folder }
            $originalEnvironment = @{}
            foreach ($name in $Environment.Keys) { $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $Environment[$name]) }

            try { $process = Start-Process @startInfo }
            finally { foreach ($name in $originalEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name]) } }

            while (-not $process.HasExited)
            {
                $process.Refresh()
                $workingSet = [long] $process.WorkingSet64
                if ($workingSet -gt $peakBytes) { $peakBytes = $workingSet }
                Add-Content $memoryCsv ('{0:F0},{1:F1}' -f $stopwatch.Elapsed.TotalSeconds, ($workingSet / 1MB))
                & $heartbeat $stopwatch.Elapsed.TotalSeconds $workingSet

                $state = @{ Seconds = $stopwatch.Elapsed.TotalSeconds; Log = $log; Arm = $Arm }
                if ($Tick) { & $Tick $state }
                if (($KillWhen -and (& $KillWhen $state)) -or ((Get-Date) -gt $deadline))
                {
                    Write-Host ("  Killing the publisher after {0:N0}s (trigger reached)." -f $stopwatch.Elapsed.TotalSeconds)
                    Stop-Process -Id $process.Id -Force
                    $killed = $true
                    break
                }

                Start-Sleep -Seconds $SampleSeconds
            }

            $process.WaitForExit()
            $peakBytes = [math]::Max([long] $process.PeakWorkingSet64, $peakBytes)
            $exitCode = $process.ExitCode
        }
        finally
        {
            if ($loggingBackup) { Move-Item $loggingBackup $loggingFile -Force }
            $storeBackup = Join-Path $Publisher.Folder 'configurationStoreSettings.json.regression-backup'
            if (Test-Path $storeBackup) { Move-Item $storeBackup (Join-Path $Publisher.Folder 'configurationStoreSettings.json') -Force }
        }
    }
    else
    {
        $configFolder = Join-Path $RunFolder 'config'
        $logsFolder = Join-Path $RunFolder 'logs'
        New-Item -ItemType Directory -Force -Path $logsFolder | Out-Null
        Write-PublisherConfig -Folder $configFolder -LogLevel $LogLevel -LogPath '/app/logs/publisher.serilog.log' -ConfigurationStoreConnectionString $ConfigurationStoreConnectionString

        $containerName = "apipub-reg-$([IO.Path]::GetFileName($RunFolder))-$([IO.Path]::GetFileNameWithoutExtension($LogName))".ToLowerInvariant() -replace '[^a-z0-9_.-]', '-'
        & docker rm -f $containerName 2>$null | Out-Null

        # host.docker.internal is built into Docker Desktop only; the host-gateway alias gives the same name on a Linux
        # Docker Engine (the APIPUB-149 runner), for URLs ConvertTo-NetworkUrl rewrote away from localhost.
        $dockerArgs = @('run', '-d', '--name', $containerName, '--network', $Arm.Network, '--add-host', 'host.docker.internal:host-gateway', '-w', '/app',
            '-v', "$(Join-Path $configFolder 'apiPublisherSettings.json'):/app/apiPublisherSettings.json:ro",
            '-v', "$(Join-Path $configFolder 'configurationStoreSettings.json'):/app/configurationStoreSettings.json:ro",
            '-v', "$(Join-Path $configFolder 'plainTextNamedConnections.json'):/app/plainTextNamedConnections.json:ro",
            '-v', "$(Join-Path $configFolder 'logging.json'):/app/logging.json:ro",
            '-v', "${logsFolder}:/app/logs")
        if ($MemoryLimit) { $dockerArgs += @('--memory', $MemoryLimit, '--memory-swap', $MemoryLimit) }
        foreach ($name in $Environment.Keys) { $dockerArgs += @('-e', "$name=$($Environment[$name])") }
        $dockerArgs += @('--entrypoint', 'dotnet', $Publisher.Image, 'EdFiApiPublisher.dll') + $allArguments

        & docker @dockerArgs | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "docker run for the publisher failed ($LASTEXITCODE)." }

        try
        {
            while ($true)
            {
                $state = (& docker inspect -f '{{.State.Status}}' $containerName 2>$null)
                if ($state -ne 'running') { break }

                $stats = & docker stats --no-stream --format '{{.MemUsage}}' $containerName 2>$null
                if ($stats)
                {
                    $usage = ($stats -split ' / ')[0].Trim()
                    $bytes = ConvertTo-Bytes $usage
                    if ($bytes -gt $peakBytes) { $peakBytes = $bytes }
                    Add-Content $memoryCsv ('{0:F0},{1:F1}' -f $stopwatch.Elapsed.TotalSeconds, ($bytes / 1MB))
                }

                # The container log is what the publisher writes to the console; it is mirrored to the run folder on
                # every sample so -Tick and -KillWhen can grep it while the run is in flight.
                & docker logs $containerName 2>&1 | Set-Content $log
                & $heartbeat $stopwatch.Elapsed.TotalSeconds $(if ($stats) { $bytes } else { 0L })

                $tickState = @{ Seconds = $stopwatch.Elapsed.TotalSeconds; Log = $log; Arm = $Arm }
                if ($Tick) { & $Tick $tickState }
                if (($KillWhen -and (& $KillWhen $tickState)) -or ((Get-Date) -gt $deadline))
                {
                    Write-Host ("  Killing the publisher container after {0:N0}s (trigger reached)." -f $stopwatch.Elapsed.TotalSeconds)
                    & docker kill $containerName | Out-Null
                    $killed = $true
                    break
                }

                Start-Sleep -Seconds $SampleSeconds
            }

            & docker logs $containerName 2>&1 | Set-Content $log
            $exitCode = [int] (& docker inspect -f '{{.State.ExitCode}}' $containerName)
            $oomKilled = (& docker inspect -f '{{.State.OOMKilled}}' $containerName) -eq 'true'
            if ($oomKilled) { Add-Content $log "[regression harness] container was OOM-killed (memory limit $MemoryLimit)" }
        }
        finally
        {
            & docker rm -f $containerName 2>$null | Out-Null
        }
    }

    $stopwatch.Stop()

    return [pscustomobject]@{
        ExitCode         = $exitCode
        Seconds          = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
        PeakWorkingSetMB = [math]::Round($peakBytes / 1MB, 1)
        Log              = $log
        MemoryCsv        = $memoryCsv
        Killed           = $killed
    }
}

function ConvertTo-Bytes
{
    param([string] $Text)

    if ($Text -match '^([\d.]+)\s*([KMG]i?B)$')
    {
        $number = [double] $Matches[1]
        switch -Regex ($Matches[2]) { '^K' { return [long] ($number * 1KB) } '^M' { return [long] ($number * 1MB) } '^G' { return [long] ($number * 1GB) } }
    }

    return 0L
}

# ----------------------------------------------------------------------------------------------------
# WireMock fault proxy
# ----------------------------------------------------------------------------------------------------

function Get-ProxyMappingFile
{
    param([string] $Name)

    $file = Join-Path $script:RegressionRoot "proxy/mappings/$Name.json"
    if (-not (Test-Path $file)) { throw "Unknown proxy fault '$Name' (expected $file)." }

    return $file
}

<#
.SYNOPSIS
    Loads one fault mapping from proxy/mappings/<Name>.json into the arm's WireMock and returns its id.
    -Replace substitutes __TOKEN__ placeholders (URL_PATTERN, RETRY_AFTER, BASE_URL, ...) before loading.
#>
function Enable-ProxyFault
{
    param([Parameter(Mandatory)] $Arm, [Parameter(Mandatory)] [string] $Name, [hashtable] $Replace = @{}, [hashtable] $Literal = @{})

    if (-not $Arm.ProxyAdminUrl) { throw "Arm $($Arm.Name) has no PROXY_PORT; fault injection is unavailable." }

    $json = Get-Content (Get-ProxyMappingFile $Name) -Raw
    # -Literal replaces exact text (used to turn the quoted "__INVALID_ID__" placeholder into a JSON null, object or array).
    foreach ($text in $Literal.Keys) { $json = $json.Replace([string] $text, [string] $Literal[$text]) }
    foreach ($token in $Replace.Keys) { $json = $json.Replace("__${token}__", [string] $Replace[$token]) }

    if ($json -match '__[A-Z_]+__') { throw "Fault '$Name' still has an unset placeholder: $($Matches[0])." }

    $response = Invoke-RestMethod -Method Post -Uri "$($Arm.ProxyAdminUrl)/mappings" -ContentType 'application/json' -Body $json -TimeoutSec 30
    Write-Host "  Proxy fault '$Name' enabled (mapping $($response.id))."

    return $response.id
}

function Disable-ProxyFault
{
    param([Parameter(Mandatory)] $Arm, [Parameter(Mandatory)] [string] $Id)

    Invoke-RestMethod -Method Delete -Uri "$($Arm.ProxyAdminUrl)/mappings/$Id" -TimeoutSec 30 | Out-Null
    Write-Host "  Proxy fault $Id disabled."
}

function Reset-ProxyMappings
{
    param([Parameter(Mandatory)] $Arm)

    if (-not $Arm.ProxyAdminUrl) { return }
    Invoke-RestMethod -Method Post -Uri "$($Arm.ProxyAdminUrl)/mappings/reset" -TimeoutSec 30 | Out-Null

    # The pass-through on disk forwards to the arm's own api-source. PROXY_FORWARD_URL (for a Northridge long run,
    # http://host.docker.internal:8001) forwards everything elsewhere instead, one priority above it.
    $forward = Get-EnvValue $Arm.Env 'PROXY_FORWARD_URL'
    if ($forward)
    {
        $mapping = @{ name = 'regression harness: pass-through to PROXY_FORWARD_URL'; priority = 9; request = @{ urlPattern = '.*' }; response = @{ proxyBaseUrl = $forward.TrimEnd('/') } } | ConvertTo-Json -Depth 5
        Invoke-RestMethod -Method Post -Uri "$($Arm.ProxyAdminUrl)/mappings" -ContentType 'application/json' -Body $mapping -TimeoutSec 30 | Out-Null
        Write-Host "  Proxy forwards to $forward (PROXY_FORWARD_URL)."
    }
}

function Reset-ProxyJournal
{
    param([Parameter(Mandatory)] $Arm)

    if (-not $Arm.ProxyAdminUrl) { return }
    Invoke-RestMethod -Method Delete -Uri "$($Arm.ProxyAdminUrl)/requests" -TimeoutSec 30 | Out-Null
}

function Get-ProxyJournal
{
    # Returns the WireMock request journal entries (request, response status, loggedDate, timing) for this arm.
    param([Parameter(Mandatory)] $Arm, [string] $UrlPattern = '.*')

    $journal = Invoke-RestMethod -Uri "$($Arm.ProxyAdminUrl)/requests?limit=1000000" -TimeoutSec 120

    return @($journal.requests | Where-Object { $_.request.url -match $UrlPattern })
}

function Save-ProxyJournal
{
    <#
    .SYNOPSIS
        Writes journal entries to the run folder as evidence, with every Authorization header replaced: the proxy
        preserves request headers, so the journal holds bearer tokens and, for token requests, the Basic credentials.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Entries, [Parameter(Mandatory)] [string] $Path, [switch] $Compress)

    $json = $Entries | ConvertTo-Json -Depth 8 -Compress:$Compress
    if ($null -eq $json) { $json = '[]' }
    $json = $json -replace '("Authorization"\s*:\s*)"[^"]*"', '$1"<redacted>"'
    Set-Content -Path $Path -Value $json
}

<#
.SYNOPSIS
    The Authorization header of the most recent data request the proxy forwarded: the bearer token the publisher is
    using right now. Item 4 invalidates exactly that token through the 401-for-token mapping.
#>
function Get-ProxyCurrentAuthorization
{
    param([Parameter(Mandatory)] $Arm, [string] $UrlPattern = '^/data/v3/')

    $entries = @(Get-ProxyJournal $Arm $UrlPattern | Where-Object { $_.request.headers -and $_.request.headers.PSObject.Properties['Authorization'] })
    if ($entries.Count -eq 0) { return $null }

    return ($entries | Sort-Object { [long] $_.request.loggedDate } | Select-Object -Last 1).request.headers.Authorization
}

<#
.SYNOPSIS
    The page reads the proxy served, per resource, from the journal: GETs below dataManagementApi that address a page
    (an offset= or pageToken= query). Count requests (limit=0&totalCount=true), /partitions calls and the /deletes and
    /keyChanges streams are left out. This is independent of the publisher's own log, so a re-read of a page the
    publisher does not log as one still shows up.

.OUTPUTS
    One row per resource: Resource, Paging (Offset, Cursor or Mixed), Successful (2xx page reads), Failed (non-2xx),
    Duplicates (page URLs answered 2xx more than once) and Partitions (pageTokens in the resource's /partitions
    response, when the journal holds its body; otherwise $null).
#>
function Get-ProxyPageReads
{
    param([Parameter(Mandatory)] $Arm, [string] $DataPath = '/data/v3')

    $prefix = $DataPath.TrimEnd('/')
    $byResource = @{}

    foreach ($entry in (Get-ProxyJournal $Arm "^$([regex]::Escape($prefix))/"))
    {
        if ($entry.request.method -ne 'GET') { continue }

        $parts = ([string] $entry.request.url).Split('?', 2)
        $path = $parts[0].Substring($prefix.Length)
        $query = if ($parts.Count -eq 2) { $parts[1] } else { '' }
        $status = if ($entry.PSObject.Properties['response'] -and $entry.response) { [int] $entry.response.status } else { [int] $entry.responseDefinition.status }

        if ($path -match '/(deletes|keyChanges)$') { continue }

        $isPartitions = $path.EndsWith('/partitions')
        $resource = if ($isPartitions) { $path.Substring(0, $path.Length - '/partitions'.Length) } else { $path }

        if (-not $byResource.ContainsKey($resource))
        {
            $byResource[$resource] = @{ Successful = 0; Failed = 0; Offset = $false; Cursor = $false; Urls = @{}; Partitions = $null }
        }
        $row = $byResource[$resource]

        if ($isPartitions)
        {
            if ($status -ge 200 -and $status -lt 300 -and $entry.response.PSObject.Properties['body'] -and $entry.response.body)
            {
                try { $row.Partitions = @((ConvertFrom-Json $entry.response.body).pageTokens).Count } catch { }
            }
            continue
        }

        $isOffset = $query -match '(^|&)offset='
        $isCursor = $query -match '(^|&)pageToken='
        if (-not ($isOffset -or $isCursor)) { continue }

        if ($isOffset) { $row.Offset = $true }
        if ($isCursor) { $row.Cursor = $true }

        if ($status -ge 200 -and $status -lt 300)
        {
            $row.Successful++
            $row.Urls[$entry.request.url] = 1 + $(if ($row.Urls.ContainsKey($entry.request.url)) { $row.Urls[$entry.request.url] } else { 0 })
        }
        else { $row.Failed++ }
    }

    return @(foreach ($resource in ($byResource.Keys | Sort-Object))
    {
        $row = $byResource[$resource]
        [pscustomobject]@{
            Resource   = $resource
            Paging     = if ($row.Offset -and $row.Cursor) { 'Mixed' } elseif ($row.Cursor) { 'Cursor' } elseif ($row.Offset) { 'Offset' } else { 'None' }
            Successful = $row.Successful
            Failed     = $row.Failed
            Duplicates = @($row.Urls.Keys | Where-Object { $row.Urls[$_] -gt 1 } | Sort-Object)
            UrlHits    = $row.Urls
            Partitions = $row.Partitions
        }
    })
}

<#
.SYNOPSIS
    Checks the page reads of one run (Get-ProxyPageReads) against the source's Total-Count for each resource
    (Compare-Counts rows) and returns the resources that do not add up, with the reason.

.DESCRIPTION
    No page URL may be answered successfully twice: a resource re-read from its first page after a failure reuses the
    first page's URL. The number of successful reads must also be what the paging mode produces for T items at page
    size S:
      offset/limit  floor(T/S) + 1   (a full last page is followed by one empty continuation read), 0 or 1 when T = 0
      cursor        between ceil(T/S) + P and ceil(T/S) + 2P - 1 for P partitions (each walk ends on an empty page);
                    at least ceil(T/S) when the journal does not hold the /partitions body
    A resource the publisher streams in more than one pass (-Passes, e.g. students, staffs and contacts, which get a
    "#Retry" authorization pass after their school associations) reads every page once per pass: each URL may then be
    answered that many times, and the expected counts are multiplied by the number of passes.
#>
function Test-ProxyPageReads
{
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $PageReads, [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $CountRows, [Parameter(Mandatory)] [int] $PageSize, [hashtable] $Passes = @{})

    $readsByResource = @{}
    foreach ($read in $PageReads) { $readsByResource[$read.Resource] = $read }

    $problems = foreach ($count in $CountRows)
    {
        if (-not ($count.Source -is [int])) { continue }

        $total = [int] $count.Source
        $passCount = if ($Passes.ContainsKey($count.Resource)) { [int] $Passes[$count.Resource] } else { 1 }
        $passNote = if ($passCount -gt 1) { " in $passCount passes" } else { '' }
        $read = $readsByResource[$count.Resource]
        if (-not $read)
        {
            if ($total -gt 0) { [pscustomobject]@{ Resource = $count.Resource; Problem = "no page reads in the proxy journal for $total source item(s)" } }
            continue
        }

        $overRead = @($read.UrlHits.Keys | Where-Object { $read.UrlHits[$_] -gt $passCount } | Sort-Object)
        if ($overRead.Count -gt 0)
        {
            [pscustomobject]@{ Resource = $count.Resource; Problem = "$($overRead.Count) page URL(s) answered 2xx more than $passCount time(s)$passNote, first $($overRead[0])" }
            continue
        }

        $pages = [math]::Ceiling($total / $PageSize)
        switch ($read.Paging)
        {
            'Offset'
            {
                $expected = if ($total -eq 0) { @(0, $passCount) } else { @(([math]::Floor($total / $PageSize) + 1) * $passCount) }
                if ($read.Successful -notin $expected) { [pscustomobject]@{ Resource = $count.Resource; Problem = "$($read.Successful) successful offset page read(s) for $total item(s) at page size $PageSize$passNote (expected $($expected -join ' or '))" } }
            }
            'Cursor'
            {
                if ($null -ne $read.Partitions)
                {
                    $low = ($pages + $read.Partitions) * $passCount
                    $high = ($pages + [math]::Max(2 * $read.Partitions - 1, $read.Partitions)) * $passCount
                    if ($read.Successful -lt $low -or $read.Successful -gt $high) { [pscustomobject]@{ Resource = $count.Resource; Problem = "$($read.Successful) successful cursor page read(s) for $total item(s) in $($read.Partitions) partition(s) at page size $PageSize$passNote (expected $low to $high)" } }
                }
                elseif ($read.Successful -lt ($pages * $passCount)) { [pscustomobject]@{ Resource = $count.Resource; Problem = "$($read.Successful) successful cursor page read(s) for $total item(s) at page size $PageSize$passNote (expected at least $($pages * $passCount))" } }
            }
            'None'
            {
                # Only a /partitions call was journaled: an empty resource answers {"pageTokens": []} and is not read
                if ($total -gt 0) { [pscustomobject]@{ Resource = $count.Resource; Problem = "no page reads in the proxy journal for $total source item(s)" } }
            }
            default
            {
                [pscustomobject]@{ Resource = $count.Resource; Problem = 'offset and cursor page reads in the journal for one resource' }
            }
        }
    }

    return @($problems)
}

<#
.SYNOPSIS
    Computes the maximum number of requests that were in flight at the same time from the journal entries, using each
    entry's loggedDate and total service time. This is the evidence for the in-flight cap of item 12.
#>
function Measure-ProxyConcurrency
{
    param([Parameter(Mandatory)] [object[]] $Entries)

    $events = New-Object System.Collections.Generic.List[object]
    foreach ($entry in $Entries)
    {
        $start = [long] $entry.request.loggedDate
        $duration = if ($entry.PSObject.Properties['timing'] -and $entry.timing) { [long] $entry.timing.totalTime } else { 1L }
        $events.Add([pscustomobject]@{ At = $start; Delta = 1 })
        $events.Add([pscustomobject]@{ At = $start + [math]::Max($duration, 1L); Delta = -1 })
    }

    $inFlight = 0; $peak = 0
    foreach ($event in ($events | Sort-Object At, Delta))
    {
        $inFlight += $event.Delta
        if ($inFlight -gt $peak) { $peak = $inFlight }
    }

    return $peak
}

# ----------------------------------------------------------------------------------------------------
# Comparisons and results
# ----------------------------------------------------------------------------------------------------

function Get-StreamedResources
{
    # The resources a run streamed, taken from its log ("Streaming of '/ed-fi/x' completed").
    param([Parameter(Mandatory)] [string] $Log)

    return @(Select-String -Path $Log -Pattern "Streaming of '(/[^']+)' completed" | ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique)
}

<#
.SYNOPSIS
    Compares Total-Count on source and target for every resource the run streamed (or -Resources).
    Works for ODS/API and DMS targets alike because it goes through each instance's Discovery URLs.
#>
function Compare-Counts
{
    param(
        [Parameter(Mandatory)] $Arm,
        [string] $Log,
        [string[]] $Resources,
        [string] $SourceUrl, [string] $SourceKey, [string] $SourceSecret,
        [string] $TargetUrl, [string] $TargetKey, [string] $TargetSecret,
        [string] $ReportCsv
    )

    if (-not $SourceUrl) { $SourceUrl = $Arm.SourceUrl }
    if (-not $SourceKey) { $SourceKey = $Arm.SourceKey }
    if (-not $SourceSecret) { $SourceSecret = $Arm.SourceSecret }
    if (-not $TargetUrl) { $TargetUrl = $Arm.TargetUrl }
    if (-not $TargetKey) { $TargetKey = $Arm.TargetKey }
    if (-not $TargetSecret) { $TargetSecret = $Arm.TargetSecret }

    if (-not $Resources) { $Resources = Get-StreamedResources $Log }
    if (-not $Resources) { throw "No streamed resources found in '$Log'; nothing to compare." }

    $sourceToken = Get-BearerToken $SourceUrl $SourceKey $SourceSecret
    $targetToken = Get-BearerToken $TargetUrl $TargetKey $TargetSecret

    $rows = foreach ($resource in $Resources)
    {
        $source = Get-ResourceCount $SourceUrl $sourceToken $resource
        $target = Get-ResourceCount $TargetUrl $targetToken $resource
        # A count that could not be read is a mismatch even when both sides fail the same way ("ERR 403" on both).
        [pscustomobject]@{ Resource = $resource; Source = $source; Target = $target; Match = (($source -is [int]) -and ($target -is [int]) -and ($source -eq $target)) }
    }

    if ($ReportCsv) { $rows | Export-Csv -NoTypeInformation -Path $ReportCsv }

    $mismatches = @($rows | Where-Object { -not $_.Match })
    if ($mismatches.Count -gt 0)
    {
        Write-Host "  Resources whose counts differ (every row is in $ReportCsv):"
        $mismatches | Format-Table -AutoSize | Out-String -Width 160 | Write-Host
    }
    # Summed by hand: Measure-Object returns nothing for an empty input (every count an error), and .Sum on nothing
    # throws under strict mode.
    $sourceItems = 0L; $targetItems = 0L
    foreach ($row in $rows)
    {
        if ($row.Source -is [int]) { $sourceItems += $row.Source }
        if ($row.Target -is [int]) { $targetItems += $row.Target }
    }

    Write-Host ("Resources: {0}; source items {1:N0}; target items {2:N0}; mismatches: {3}" -f $rows.Count, $sourceItems, $targetItems, $mismatches.Count)

    return [pscustomobject]@{
        Rows        = @($rows)
        Mismatches  = $mismatches
        SourceItems = [long] $sourceItems
        TargetItems = [long] $targetItems
        Summary     = ("{0} resources, source {1:N0}, target {2:N0}, {3} mismatch(es)" -f $rows.Count, $sourceItems, $targetItems, $mismatches.Count)
    }
}

function Get-RequestUrlShapes
{
    # Every URL in a log, reduced to scheme, host, path and the query parameter NAMES, so paging values do not
    # count as differences between two runs. A backslash ends a URL: v1.4 logs the Discovery document as an escaped
    # JSON string (\"http://host/data/v3/\"). A bare base URL (path "/") is left out: v1.4 names the base it
    # resolves Discovery routes against ("relative to 'http://proxy:8080/'"), which v1.3 used without logging it.
    param([Parameter(Mandatory)] [string] $Log)

    $shapes = New-Object System.Collections.Generic.HashSet[string]
    foreach ($match in (Select-String -Path $Log -Pattern "https?://[^\s'`"\]\)\\]+" -AllMatches))
    {
        foreach ($url in $match.Matches.Value)
        {
            $url = $url.TrimEnd('.', ',', ';')
            $parts = $url.Split('?', 2)
            $shape = $parts[0]
            if ($parts.Count -eq 1 -and $shape -match '^https?://[^/]+/?$') { continue }
            if ($parts.Count -eq 2)
            {
                $names = @($parts[1].Split('&') | ForEach-Object { $_.Split('=')[0] } | Sort-Object -Unique)
                $shape += '?' + ($names -join '&')
            }
            [void] $shapes.Add($shape)
        }
    }

    return $shapes
}

<#
.SYNOPSIS
    Diffs the request URL shapes two runs logged (item 9: v1.3 baseline vs the RC; D1: hardcoded segments).
#>
function Compare-RequestUrls
{
    param([Parameter(Mandatory)] [string] $LogA, [Parameter(Mandatory)] [string] $LogB)

    $a = Get-RequestUrlShapes $LogA
    $b = Get-RequestUrlShapes $LogB

    return [pscustomobject]@{
        OnlyInA = @($a | Where-Object { -not $b.Contains($_) } | Sort-Object)
        OnlyInB = @($b | Where-Object { -not $a.Contains($_) } | Sort-Object)
        Common  = @($a | Where-Object { $b.Contains($_) }).Count
    }
}

function Get-RunSummaryTotal
{
    <#
    .SYNOPSIS
        The Total row of the last "Publishing run summary" in a log: Expected, Attempted, Failed, Skipped, Published
        (Expected is $null when the summary shows it as unknown). $null when the log has no summary.
    #>
    param([Parameter(Mandatory)] [string] $Log)

    if (-not (Test-Path $Log)) { return $null }

    $lines = @(Get-Content $Log)
    $start = [array]::FindLastIndex([string[]] $lines, [Predicate[string]] { param($line) $line -match 'Publishing run summary' })
    if ($start -lt 0) { return $null }

    $number = { param($text) if ($text -match '^[\d,]+$') { [long] ($text -replace ',', '') } else { $null } }
    foreach ($line in $lines[($start + 1)..($lines.Count - 1)])
    {
        if ($line -match '^\s+Total\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s*$')
        {
            return [pscustomobject]@{
                Expected  = & $number $Matches[1]
                Attempted = & $number $Matches[2]
                Failed    = & $number $Matches[3]
                Skipped   = & $number $Matches[4]
                Published = & $number $Matches[5]
            }
        }
    }

    return $null
}

function Get-PublishedErrorRecords
{
    <#
    .SYNOPSIS
        The error records the publisher reported (SerilogErrorPublisher writes each batch as one JSON array after its
        log prefix). Returns the parsed records of every batch in the log.
    #>
    param([Parameter(Mandatory)] [string] $Log)

    if (-not (Test-Path $Log)) { return @() }

    $lines = @(Get-Content $Log)
    $records = New-Object System.Collections.Generic.List[object]

    for ($index = 0; $index -lt $lines.Count; $index++)
    {
        if ($lines[$index] -notmatch 'SerilogErrorPublisher - \[\s*$') { continue }

        $json = New-Object System.Text.StringBuilder
        [void] $json.AppendLine('[')
        for ($index++; $index -lt $lines.Count; $index++)
        {
            [void] $json.AppendLine($lines[$index])
            if ($lines[$index] -match '^\]\s*$') { break }
        }

        try { foreach ($record in @(ConvertFrom-Json $json.ToString())) { $records.Add($record) } }
        catch { Write-Warning "An error record batch in $Log could not be parsed: $($_.Exception.Message)" }
    }

    return $records.ToArray()
}

<#
.SYNOPSIS
    Creates the PostgreSQL configuration store of docs/ConfigurationStore/PostgreSql.md in the arm's db-admin container
    and stores one source and one target connection (key and secret pgcrypto-encrypted). -LastChangeVersion seeds the
    change version recorded for the target. Returns the connection string the publisher under test uses to reach it.
#>
function Initialize-PostgreSqlConfigurationStore
{
    param(
        [Parameter(Mandatory)] $Arm,
        [Parameter(Mandatory)] $Publisher,
        [Parameter(Mandatory)] [string] $RunFolder,
        [Parameter(Mandatory)] [string] $SourceName,
        [Parameter(Mandatory)] [string] $TargetName,
        [Parameter(Mandatory)] [string] $EncryptionPassword,
        [string] $SourceUrl,
        [Nullable[long]] $LastChangeVersion,
        [string] $Database = 'edfi_api_publisher_configuration'
    )

    if (-not $SourceUrl) { $SourceUrl = $Arm.SourceUrl }
    $targetUrl = $Arm.TargetUrl
    if ($Publisher.Mode -eq 'docker')
    {
        # The container reaches the store and both APIs on the arm's network.
        $SourceUrl = ConvertTo-NetworkUrl $Arm $SourceUrl
        $targetUrl = ConvertTo-NetworkUrl $Arm $targetUrl
    }

    $exists = (Invoke-ArmPsql $Arm -Service db-admin -TuplesOnly -Sql "select 1 from pg_database where datname = '$Database'") -join ''
    if ($exists.Trim() -ne '1') { Invoke-ArmPsql $Arm -Service db-admin -Sql "create database $Database" | Out-Null }

    $prefix = '/ed-fi/apiPublisher/connections'
    $seed = if ($null -ne $LastChangeVersion) { "insert into dbo.configuration_value (configuration_key, configuration_value) values ('$prefix/$SourceName/lastChangeVersionsProcessed', '{`"$TargetName`": $LastChangeVersion}');" } else { '' }
    $setup = @"
create schema if not exists dbo;
create table if not exists dbo.configuration_value (
    configuration_key varchar(450) not null constraint configuration_value_pk primary key,
    configuration_value text,
    configuration_value_encrypted bytea);
create extension if not exists pgcrypto;
delete from dbo.configuration_value where configuration_key like '$prefix/$SourceName/%' or configuration_key like '$prefix/$TargetName/%';
insert into dbo.configuration_value (configuration_key, configuration_value) values
    ('$prefix/$SourceName/url', '$SourceUrl'),
    ('$prefix/$TargetName/url', '$targetUrl');
insert into dbo.configuration_value (configuration_key, configuration_value_encrypted) values
    ('$prefix/$SourceName/key', pgp_sym_encrypt('$($Arm.SourceKey)', '$EncryptionPassword')),
    ('$prefix/$SourceName/secret', pgp_sym_encrypt('$($Arm.SourceSecret)', '$EncryptionPassword')),
    ('$prefix/$TargetName/key', pgp_sym_encrypt('$($Arm.TargetKey)', '$EncryptionPassword')),
    ('$prefix/$TargetName/secret', pgp_sym_encrypt('$($Arm.TargetSecret)', '$EncryptionPassword'));
$seed
"@
    $setupFile = Join-Path $RunFolder 'store-setup.sql'
    Set-Content -Path $setupFile -Value $setup
    Invoke-ArmPsql $Arm -Service db-admin -Database $Database -File $setupFile | Out-Null
    Write-Host "  configuration store ready in db-admin/$Database ($SourceName -> $SourceUrl, $TargetName -> $targetUrl)"

    $user = Get-EnvValue $Arm.Env 'POSTGRES_USER' 'postgres'
    $password = Get-EnvValue $Arm.Env 'POSTGRES_PASSWORD'
    if ($Publisher.Mode -eq 'docker') { return "Host=db-admin;Port=5432;Database=$Database;Username=$user;Password=$password" }

    return "Host=127.0.0.1;Port=$(Get-EnvValue $Arm.Env 'ADMIN_DB_PORT');Database=$Database;Username=$user;Password=$password"
}

function Get-StoredLastChangeVersion
{
    # The change version the PostgreSQL configuration store records for one source and target pair, or $null.
    param([Parameter(Mandatory)] $Arm, [Parameter(Mandatory)] [string] $SourceName, [Parameter(Mandatory)] [string] $TargetName, [string] $Database = 'edfi_api_publisher_configuration')

    $json = ((Invoke-ArmPsql $Arm -Service db-admin -Database $Database -TuplesOnly -Sql "select configuration_value from dbo.configuration_value where configuration_key = '/ed-fi/apiPublisher/connections/$SourceName/lastChangeVersionsProcessed'") -join '').Trim()
    if (-not $json) { return $null }

    $values = ConvertFrom-Json $json
    if (-not $values.PSObject.Properties[$TargetName]) { return $null }

    return [long] $values.$TargetName
}

function Test-LogContains
{
    param([Parameter(Mandatory)] [string] $Log, [Parameter(Mandatory)] [string] $Pattern)

    if (-not (Test-Path $Log)) { return $false }

    return [bool] (Select-String -Path $Log -Pattern $Pattern -Quiet)
}

function Get-LogMatchCount
{
    param([Parameter(Mandatory)] [string] $Log, [Parameter(Mandatory)] [string] $Pattern)

    if (-not (Test-Path $Log)) { return 0 }

    return @(Select-String -Path $Log -Pattern $Pattern).Count
}

function Format-Duration
{
    param([double] $Seconds)

    $span = [timespan]::FromSeconds($Seconds)
    if ($span.TotalHours -ge 1) { return ('{0:N1} h' -f $span.TotalHours) }
    if ($span.TotalMinutes -ge 1) { return ('{0:N1} min' -f $span.TotalMinutes) }

    return ('{0:N0} s' -f $span.TotalSeconds)
}

<#
.SYNOPSIS
    Appends one row to the results markdown file (created with its header if missing). One row per item and arm.
#>
function Write-ResultRow
{
    param(
        [Parameter(Mandatory)] [string] $ResultsFile,
        [Parameter(Mandatory)] [string] $Item,
        [Parameter(Mandatory)] [string] $ArmName,
        [Parameter(Mandatory)] [bool] $Passed,
        [string] $Counts = '',
        [double] $Seconds = 0,
        [string] $Log = '',
        [string] $Notes = '',
        [string] $Publisher = $(if ($script:PublisherUnderTest) { $script:PublisherUnderTest.Identity } else { '' })
    )

    $resolvedResults = [IO.Path]::GetFullPath($ResultsFile)
    if (-not (Test-Path $resolvedResults))
    {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedResults) | Out-Null
        @(
            "# API Publisher regression results",
            "",
            "One row per test item and arm, appended by the item scripts under ``eng/regression/items`` (APIPUB-125, APIPUB-146).",
            "",
            "| Date | Item | Arm | Result | Publisher | Counts | Duration | Log | Notes |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"
        ) | Set-Content $resolvedResults
    }
    elseif (-not ((Get-Content $resolvedResults) -match '^\| Date \| Item \| Arm \| Result \| Publisher \|'))
    {
        # A table from before the Publisher column would take the row one cell off from Counts onwards.
        throw "$resolvedResults has no '| Date | Item | Arm | Result | Publisher |' table header; add the Publisher column after Result or pass a new -ResultsFile."
    }

    $logLink = ''
    if ($Log)
    {
        $relative = [IO.Path]::GetRelativePath((Split-Path -Parent $resolvedResults), $Log) -replace '\\', '/'
        $logLink = "[$([IO.Path]::GetFileName($Log))]($relative)"
    }

    $clean = { param($text) ("$text" -replace '\|', '\|' -replace '[\r\n]+', ' ').Trim() }
    $row = '| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |' -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $Item, $ArmName, ($(if ($Passed) { 'PASS' } else { 'FAIL' })), (& $clean $Publisher), (& $clean $Counts), (Format-Duration $Seconds), $logLink, (& $clean $Notes)
    Add-Content -Path $resolvedResults -Value $row
}

<#
.SYNOPSIS
    Ends an item: prints the verdict, writes the result row and returns the exit code the script should exit with.
    Items call it as: exit (Complete-Item ...). Failures list the assertions that did not hold.
#>
function Complete-Item
{
    param(
        [Parameter(Mandatory)] [string] $Item,
        [Parameter(Mandatory)] $Arm,
        [Parameter(Mandatory)] [string] $ResultsFile,
        [Parameter(Mandatory)] [System.Collections.IList] $Failures,
        [string] $Counts = '',
        [double] $Seconds = 0,
        [string] $Log = '',
        [string] $Notes = ''
    )

    $passed = ($Failures.Count -eq 0)
    $allNotes = @($Notes) + @($Failures) | Where-Object { $_ }

    Write-ResultRow -ResultsFile $ResultsFile -Item $Item -ArmName $Arm.Name -Passed $passed -Counts $Counts -Seconds $Seconds -Log $Log -Notes ($allNotes -join '; ')

    if ($passed)
    {
        Write-Host "PASS  item $Item on arm $($Arm.Name)$(if ($Notes) { " ($Notes)" })" -ForegroundColor Green
        return 0
    }

    Write-Host "FAIL  item $Item on arm $($Arm.Name):" -ForegroundColor Red
    foreach ($failure in $Failures) { Write-Host "      - $failure" -ForegroundColor Red }

    return 1
}

<#
.SYNOPSIS
    Ends an item that threw before it reached Complete-Item: writes a FAIL row naming the exception and where it was
    raised, so the reason reaches the results file and not only the console. Items call it from a script-level trap:
        trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
#>
function Complete-ItemAfterError
{
    param(
        [Parameter(Mandatory)] [string] $Item,
        [Parameter(Mandatory)] [string] $ArmName,
        [Parameter(Mandatory)] [string] $ResultsFile,
        [System.Collections.IList] $Failures,
        [Parameter(Mandatory)] [System.Management.Automation.ErrorRecord] $ErrorRecord
    )

    $position = $ErrorRecord.InvocationInfo
    $where = if ($position -and $position.ScriptName) { " at $([IO.Path]::GetFileName($position.ScriptName)):$($position.ScriptLineNumber)" } else { '' }
    $message = "the item stopped on an error$where`: $($ErrorRecord.Exception.Message)"

    $notes = @($message)
    if ($Failures) { $notes += @($Failures) }
    Write-ResultRow -ResultsFile $ResultsFile -Item $Item -ArmName $ArmName.Trim().ToUpperInvariant() -Passed $false -Notes ($notes -join '; ')

    Write-Host "FAIL  item $Item on arm $($ArmName.Trim().ToUpperInvariant()): $message" -ForegroundColor Red
    if ($Failures) { foreach ($failure in $Failures) { Write-Host "      - $failure" -ForegroundColor Red } }

    return 1
}

function New-FailureList
{
    return New-Object System.Collections.Generic.List[string]
}

function Assert-Condition
{
    # Records a failed assertion instead of throwing, so one item reports every failed check at once.
    param([Parameter(Mandatory)] [System.Collections.IList] $Failures, [Parameter(Mandatory)] [bool] $Condition, [Parameter(Mandatory)] [string] $Message)

    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $Failures.Add($Message) }
}

Export-ModuleMember -Function @(
    'Get-RegressionRoot', 'Read-EnvFile', 'Get-Arm', 'Invoke-ArmPsql', 'Wait-Url',
    'Start-RegressionArm', 'Stop-RegressionArm', 'Reset-RegressionTarget', 'Reset-RegressionSource', 'Get-RootEducationOrganizationIds',
    'Get-ApiUrls', 'Get-JwtIssuer', 'Get-BearerToken', 'Invoke-Api', 'Get-ResourceCount', 'Get-ApiResources', 'Get-NewestChangeVersion',
    'Resolve-Publisher', 'Get-PublisherIdentity', 'Expand-PublisherPackage', 'New-RunFolder', 'Invoke-Publisher',
    'Enable-ProxyFault', 'Disable-ProxyFault', 'Reset-ProxyMappings', 'Reset-ProxyJournal', 'Get-ProxyJournal', 'Save-ProxyJournal', 'Get-ProxyCurrentAuthorization', 'Get-ProxyPageReads', 'Test-ProxyPageReads', 'Measure-ProxyConcurrency',
    'Get-StreamedResources', 'Compare-Counts', 'Compare-RequestUrls', 'Get-RunSummaryTotal', 'Get-PublishedErrorRecords', 'Initialize-PostgreSqlConfigurationStore', 'Get-StoredLastChangeVersion', 'Test-LogContains', 'Get-LogMatchCount',
    'Format-Duration', 'Write-ResultRow', 'Complete-Item', 'Complete-ItemAfterError', 'New-FailureList', 'Assert-Condition'
)
