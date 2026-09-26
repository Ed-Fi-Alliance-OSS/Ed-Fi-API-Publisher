# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 11: resumable cursor paging (APIPUB-142). Run A is killed once the watched resource has requested
    -KillAfterPages pages; run B restarts with --resumeLastRun=true, must report that it resumes from recorded
    positions, finish with exit 0, match the source counts and remove the run state. Port of the APIPUB-141
    resume-test.sh with a small page size so Grand Bend produces enough pages.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [string] $Include = '/ed-fi/students,/ed-fi/studentSectionAssociations',
    [string] $WatchedResource = '/ed-fi/studentSectionAssociations',
    [int] $PageSize = 25,
    [int] $KillAfterPages = 20,
    [int] $TimeoutMinutes = 60
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '11'
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item resumable cursor paging on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); kill after $KillAfterPages pages of $WatchedResource; run folder $run"

if ($armDef.Name -eq 'A') { Write-Warning 'Arm A has no cursor paging; the resume here exercises the offset/limit run state instead.' }

Reset-RegressionTarget $armDef

# The state folder must be visible to the publisher: for a container it lives under the mounted logs folder.
$stateHost = if ($publisher.Mode -eq 'docker') { Join-Path $run 'logs/state' } else { Join-Path $run 'state' }
$statePublisher = if ($publisher.Mode -eq 'docker') { '/app/logs/state' } else { $stateHost }
New-Item -ItemType Directory -Force -Path $stateHost | Out-Null

$arguments = @("--include=$Include", '--includeDescriptors=true', "--streamingPageSize=$PageSize", "--runStatePath=$statePublisher")
$pagePattern = '"{0}": Retrieving page items' -f [regex]::Escape($WatchedResource)

$killWhen = { param($state) (Get-LogMatchCount $state.Log $pagePattern) -ge $KillAfterPages }
# The per-page read lines the kill trigger counts are logged at Debug.
$runA = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'run-a.log' -Arguments $arguments -KillWhen $killWhen -TimeoutMinutes $TimeoutMinutes -LogLevel Debug
$pagesA = Get-LogMatchCount $runA.Log $pagePattern

Assert-Condition $failures $runA.Killed "run A was killed mid-resource after $pagesA page request(s) of $WatchedResource"
Assert-Condition $failures (Test-LogContains $runA.Log 'Run state for this run is kept at') 'run A announced where its run state is kept'
$stateFiles = @(Get-ChildItem -Path $stateHost -Filter '*.json' -File -ErrorAction SilentlyContinue)
Assert-Condition $failures ($stateFiles.Count -ge 1) "a run state file survived the kill ($($stateFiles.Count) file(s) in $stateHost)"

$runB = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'run-b.log' -Arguments ($arguments + @('--resumeLastRun=true')) -TimeoutMinutes $TimeoutMinutes -LogLevel Debug
$pagesB = Get-LogMatchCount $runB.Log $pagePattern

Assert-Condition $failures (Test-LogContains $runB.Log 'Resuming') 'run B reported that it resumed the previous run'
Assert-Condition $failures (Test-LogContains $runB.Log 'restart at a confirmed page') 'run B restarted its partitions at confirmed pages rather than from the start'
Assert-Condition $failures ($runB.ExitCode -eq 0) "run B exited with 0 (was $($runB.ExitCode))"
Assert-Condition $failures (-not (Test-LogContains $runB.Log 'no run state was found')) 'run B found the run state'

$counts = Compare-Counts -Arm $armDef -Resources ($Include.Split(',') | ForEach-Object { $_.Trim() }) -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "source and target counts match after the resume ($($counts.Mismatches.Count) mismatch(es))"

$leftover = @(Get-ChildItem -Path $stateHost -Filter '*.json' -File -ErrorAction SilentlyContinue)
Assert-Condition $failures ($leftover.Count -eq 0) "the run state was removed after the clean finish ($($leftover.Count) file(s) left)"

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds ($runA.Seconds + $runB.Seconds) -Log $runB.Log -Notes "page size $PageSize; run A killed after $pagesA pages; run B requested $pagesB pages of $WatchedResource")
