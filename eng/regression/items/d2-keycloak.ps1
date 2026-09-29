# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item D2: DMS with Keycloak, token endpoint taken from Discovery's oauth URL, refresh during a run (APIPUB-109,
    APIPUB-146). Confirms the token the advertised oauth URL hands out is a Keycloak token (realm issuer), that the
    publisher took the target token endpoint from Discovery, the refresh interval line, and the absence of
    unauthorized responses. The two-interval long run is shared with item 4 (run it against the DMS by pointing arm D
    at Northridge).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [int] $MinimumRefreshes = 0,
    [switch] $Preliminary
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = 'D2'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item DMS Keycloak authentication on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$target = Get-ApiUrls $armDef.TargetUrl
Write-Host "  DMS oauth endpoint: $($target.Oauth)"

# The DMS advertises its own address for oauth also when Keycloak issues the tokens (it fronts the token endpoint;
# measured on APIPUB-146), so the host says nothing about the identity provider. The token does: a Keycloak token's
# issuer is a realm URL.
$token = $null
try { $token = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret } catch { }
Assert-Condition $failures ($null -ne $token) 'a bearer token can be obtained from the advertised oauth endpoint with the arm D target credentials'
$issuer = $null
if ($token -and $token.Split('.').Count -eq 3)
{
    $payload = $token.Split('.')[1].Replace('-', '+').Replace('_', '/')
    $payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
    try { $issuer = ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json).iss } catch { }
}
Assert-Condition $failures ("$issuer" -match '/realms/') "the token was issued by Keycloak (iss '$issuer')"

$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -Arguments @('--includeDescriptors=true')
Assert-Condition $failures ($result.ExitCode -eq 0) "the publish into the DMS exited with 0 (was $($result.ExitCode))"
# APIPUB-109: with nothing stated in configuration the target's token endpoint is the one Discovery declares, and the
# resolver names it at startup.
$declaredPattern = "declares its token endpoint (as|at) '$([regex]::Escape($target.Oauth.TrimEnd('/')))/?'"
Assert-Condition $failures (Test-LogContains $result.Log $declaredPattern) "the publisher requested the target token from the Discovery oauth URL $($target.Oauth)"
Assert-Condition $failures (Test-LogContains $result.Log 'Bearer token refresh interval for') 'the startup log reports the refresh interval derived from the Keycloak token lifetime'
Assert-Condition $failures (-not (Test-LogContains $result.Log 'rejected as unauthorized')) 'no request was rejected as unauthorized'
$refreshes = Get-LogMatchCount $result.Log 'Bearer token refreshed successfully'
if ($MinimumRefreshes -gt 0) { Assert-Condition $failures ($refreshes -ge $MinimumRefreshes) "at least $MinimumRefreshes refreshes happened ($refreshes)" }

$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "counts match between the ODS source and the DMS ($($counts.Mismatches.Count) mismatch(es))"

$notes = "oauth $($target.Oauth); $refreshes refresh(es)"
if ($Preliminary) { $notes = "PRELIMINARY (APIPUB-109 not merged): $notes" }

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes $notes)
