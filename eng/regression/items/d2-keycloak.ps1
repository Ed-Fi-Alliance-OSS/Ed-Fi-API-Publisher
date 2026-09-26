# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item D2: DMS with Keycloak, token endpoint taken from Discovery's oauth URL on a different host, refresh during a
    run (APIPUB-109, APIPUB-146). Confirms the DMS advertises an oauth URL whose host is not the DMS itself, publishes
    with Keycloak enabled, and checks the refresh interval line plus the absence of unauthorized responses. The
    two-interval long run is shared with item 4 (run it against the DMS by pointing arm D at Northridge).
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
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item DMS Keycloak authentication on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$target = Get-ApiUrls $armDef.TargetUrl
$oauthHost = ([uri] $target.Oauth).Authority
$dmsHost = ([uri] $armDef.TargetUrl).Authority
Write-Host "  DMS oauth endpoint: $($target.Oauth)"
Assert-Condition $failures ($oauthHost -ne $dmsHost) "the DMS advertises its token endpoint on a different host ($oauthHost vs DMS $dmsHost), i.e. Keycloak"

$token = $null
try { $token = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret } catch { }
Assert-Condition $failures ($null -ne $token) 'a bearer token can be obtained from the advertised oauth endpoint with the arm D target credentials'

$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -Arguments @('--includeDescriptors=true')
Assert-Condition $failures ($result.ExitCode -eq 0) "the publish into the DMS exited with 0 (was $($result.ExitCode))"
Assert-Condition $failures (Test-LogContains $result.Log 'Bearer token refresh interval for') 'the startup log reports the refresh interval derived from the Keycloak token lifetime'
Assert-Condition $failures (-not (Test-LogContains $result.Log 'rejected as unauthorized')) 'no request was rejected as unauthorized'
$refreshes = Get-LogMatchCount $result.Log 'Bearer token refreshed successfully'
if ($MinimumRefreshes -gt 0) { Assert-Condition $failures ($refreshes -ge $MinimumRefreshes) "at least $MinimumRefreshes refreshes happened ($refreshes)" }

$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "counts match between the ODS source and the DMS ($($counts.Mismatches.Count) mismatch(es))"

$notes = "oauth $($target.Oauth); $refreshes refresh(es)"
if ($Preliminary) { $notes = "PRELIMINARY (APIPUB-109 not merged): $notes" }

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes $notes)
