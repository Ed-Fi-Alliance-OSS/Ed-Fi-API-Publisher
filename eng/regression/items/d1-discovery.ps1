# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item D1: DMS target, resource URLs resolved from the Discovery document's dataManagementApi, no hardcoded
    data/v3 (APIPUB-108, APIPUB-146). A Debug-level publish into the DMS; every target URL the log shows must start
    with what the DMS Discovery advertises. Depends on PR #174 being in the build under test; until then the row is
    marked preliminary.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [switch] $Preliminary
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = 'D1'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item DMS Discovery routing on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$target = Get-ApiUrls $armDef.TargetUrl
Write-Host "  DMS Discovery: version $($target.Version); dataManagementApi $($target.DataManagementApi); oauth $($target.Oauth)"

$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -Arguments @('--includeDescriptors=true') -LogLevel Debug
Assert-Condition $failures ($result.ExitCode -eq 0) "the publish into the DMS exited with 0 (was $($result.ExitCode))"

$targetHost = ([uri] $armDef.TargetUrl).Authority
$targetUrls = @(Select-String -Path $result.Log -Pattern "https?://$([regex]::Escape($targetHost))[^\s'`"\]\)]*" -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique)
# Every URL must start with one the Discovery document advertises (dataManagementApi, oauth, dependencies, metadata,
# ...). The base URL is allowed only as itself (the Discovery request): as a prefix it would admit every URL on the
# host, a hardcoded /data/v3 included.
$allowedPrefixes = @($target.Discovery.urls.PSObject.Properties | ForEach-Object { "$($_.Value)".TrimEnd('/') } | Where-Object { $_ })
$baseUrl = $target.BaseUrl.TrimEnd('/')
$offRoute = @($targetUrls | Where-Object { $url = $_; ($url.TrimEnd('/') -ne $baseUrl) -and -not ($allowedPrefixes | Where-Object { $url.StartsWith($_) }) })
$targetUrls | Set-Content (Join-Path $run 'target-urls.txt')

Assert-Condition $failures ($targetUrls.Count -gt 0) "the Debug log shows target URLs ($($targetUrls.Count) distinct)"
Assert-Condition $failures ($offRoute.Count -eq 0) "every target URL starts with a Discovery-advertised prefix ($($offRoute.Count) off-route; see target-urls.txt)"
if (-not $target.DataManagementApi.Contains('/data/v3'))
{
    $hardcoded = @($targetUrls | Where-Object { $_ -match '/data/v3(/|$)' }).Count
    Assert-Condition $failures ($hardcoded -eq 0) "no target URL uses a hardcoded data/v3 segment ($hardcoded found)"
}

$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "counts match between the ODS source and the DMS ($($counts.Mismatches.Count) mismatch(es))"

$notes = "DMS $($target.Version); dataManagementApi $($target.DataManagementApi)"
if ($Preliminary) { $notes = "PRELIMINARY (PR #174 not merged): $notes" }

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes $notes)
