# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 13: the README key scenarios the other items do not reach (APIPUB-125 task list). One full publish into an
    empty target per scenario:
      Exclude              --exclude=<ExcludeResource>: the resource and the dependents the publisher derives from the
                           dependency metadata are not streamed and stay empty on the target; everything else matches
      ChangeVersionPaging  --useChangeVersionPaging=true with a small window, so the source is read in several
                           change version windows; counts match
      LowParallelism       every degree of parallelism at 1; counts match
      HighParallelism      degrees of parallelism well above the shipped defaults; counts match
    Remediations (--remediationsScriptFile) stay a manual check: they need Node.js next to the publisher (not in the
    image), and a healthy ODS target gives no failure a remediation plan is needed for, because the publisher already
    resolves missing references itself (see README, "Manual residue").
.PARAMETER Scenarios
    Comma-separated subset of Exclude, ChangeVersionPaging, LowParallelism, HighParallelism (default all).
.PARAMETER ExcludeResource
    Resource excluded by the Exclude scenario (default /ed-fi/gradebookEntries, whose dependents include
    /ed-fi/studentGradebookEntries).
.PARAMETER ChangeVersionWindowSize
    --changeVersionPagingWindowSize for the ChangeVersionPaging scenario (default 5000, several windows on Grand Bend).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [string] $Scenarios = 'Exclude,ChangeVersionPaging,LowParallelism,HighParallelism',
    [string] $ExcludeResource = '/ed-fi/gradebookEntries',
    [int] $ChangeVersionWindowSize = 5000,
    [int] $TimeoutMinutes = 120
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '13'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
$seconds = 0.0
$summaries = @()
Write-Host "Item $item README scenarios on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$scenarioArguments = @{
    Exclude             = @('--disableCursorPaging=true', '--includeDescriptors=true', "--exclude=$ExcludeResource")
    ChangeVersionPaging = @('--includeDescriptors=true', '--useChangeVersionPaging=true', "--changeVersionPagingWindowSize=$ChangeVersionWindowSize")
    LowParallelism      = @('--includeDescriptors=true', '--maxDegreeOfParallelismForResourceProcessing=1', '--maxDegreeOfParallelismForPostResourceItem=1', '--maxDegreeOfParallelismForStreamResourcePages=1')
    HighParallelism     = @('--includeDescriptors=true', '--maxDegreeOfParallelismForResourceProcessing=16', '--maxDegreeOfParallelismForPostResourceItem=60', '--maxDegreeOfParallelismForStreamResourcePages=16')
}

foreach ($scenario in ($Scenarios.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }))
{
    if (-not $scenarioArguments.ContainsKey($scenario)) { $failures.Add("unknown scenario '$scenario' (known: $($scenarioArguments.Keys -join ', '))"); continue }

    Write-Host ''
    Write-Host "--- scenario: $scenario ---"
    Reset-RegressionTarget $armDef

    # Change version paging is judged from the requests the proxy saw, so that scenario reads the source through it.
    $viaProxy = ($scenario -eq 'ChangeVersionPaging') -and $armDef.ProxyUrl
    $sourceUrl = if ($viaProxy) { Reset-ProxyMappings $armDef; Reset-ProxyJournal $armDef; $armDef.ProxyUrl } else { $armDef.SourceUrl }

    $result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName "$($scenario.ToLowerInvariant()).log" -SourceUrl $sourceUrl -Arguments $scenarioArguments[$scenario] -TimeoutMinutes $TimeoutMinutes
    $seconds += $result.Seconds
    Assert-Condition $failures ($result.ExitCode -eq 0) "${scenario}: publish exited with 0 (was $($result.ExitCode))"

    $counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run "$($scenario.ToLowerInvariant())-counts.csv")
    Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "${scenario}: every streamed resource has the same Total-Count on source and target ($($counts.Mismatches.Count) mismatch(es))"
    Assert-Condition $failures ($counts.TargetItems -gt 0) "${scenario}: the target received items ($($counts.TargetItems))"

    switch ($scenario)
    {
        'Exclude'
        {
            $streamed = Get-StreamedResources $result.Log
            Assert-Condition $failures ($ExcludeResource -notin $streamed) "Exclude: $ExcludeResource was not streamed"

            # The dependents the publisher excluded with it are what it streamed in a plain run and not here; the
            # check is that none of them reached the target (they could not have: their references are missing).
            $targetToken = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret
            $excludedOnTarget = Get-ResourceCount $armDef.TargetUrl $targetToken $ExcludeResource
            Assert-Condition $failures ($excludedOnTarget -is [int] -and $excludedOnTarget -eq 0) "Exclude: the target has no $ExcludeResource ($excludedOnTarget)"
            $dependent = '/ed-fi/studentGradebookEntries'
            if ($ExcludeResource -eq '/ed-fi/gradebookEntries')
            {
                Assert-Condition $failures ($dependent -notin $streamed) "Exclude: the dependent $dependent was excluded with it"
            }
        }
        'ChangeVersionPaging'
        {
            if (-not $viaProxy) { $failures.Add("ChangeVersionPaging: arm $($armDef.Name) has no proxy, so the change version windows cannot be checked"); break }

            # Every page read carries its window as minChangeVersion/maxChangeVersion; a small window size must give
            # several distinct windows, and change version paging runs on offset/limit, never on pageToken.
            # /data/v3 for an ODS/API source, /api/data for a DMS source.
            $dataPattern = "^$([regex]::Escape((Get-ProxySourcePaths $armDef).Data))/"
            $pages = @(Get-ProxyJournal $armDef "$dataPattern.*[?&]offset=" | Where-Object { $_.request.method -eq 'GET' })
            $pages | ForEach-Object { $_.request.url } | Set-Content (Join-Path $run 'changeversionpaging-page-urls.txt')
            $windows = @($pages | ForEach-Object { if ($_.request.url -match '[?&]minChangeVersion=(\d+).*[?&]maxChangeVersion=(\d+)') { "$($Matches[1])-$($Matches[2])" } } | Sort-Object -Unique)
            $cursorReads = @(Get-ProxyJournal $armDef "$dataPattern.*[?&]pageToken=").Count
            Assert-Condition $failures ($windows.Count -gt 1) "ChangeVersionPaging: page reads span $($windows.Count) change version window(s) at window size $ChangeVersionWindowSize (more than 1 expected)"
            Assert-Condition $failures ($cursorReads -eq 0) "ChangeVersionPaging: no cursor page reads ($cursorReads)"
        }
    }

    $summaries += "${scenario}: $($counts.Summary)"
}

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts ($summaries -join ' / ') -Seconds $seconds -Log (Join-Path $run "$(($Scenarios.Split(',')[0]).Trim().ToLowerInvariant()).log") -Notes "scenarios $Scenarios; exclude $ExcludeResource; change version window $ChangeVersionWindowSize")
