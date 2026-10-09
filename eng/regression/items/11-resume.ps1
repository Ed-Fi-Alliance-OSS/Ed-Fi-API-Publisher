# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 11: resumable cursor paging (APIPUB-142). Run A is killed once the watched resource has requested
    -KillAfterPages pages and its run state records a partition confirmed up to page -KillAfterConfirmedPage; run B
    restarts with --resumeLastRun=true, must report that at least one partition restarts at
    a confirmed page, finish with exit 0, match the source counts and remove the run state. Run C then reads the same
    change window uninterrupted as the reference: run B must request fewer pages of the watched resource than run C
    (it skipped the confirmed pages), and runs A and B together at least as many (nothing was skipped that run A did
    not read). Port of the APIPUB-141 resume-test.sh with a small page size so Grand Bend produces enough pages.
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
    [int] $KillAfterConfirmedPage = 3,
    [int] $TimeoutMinutes = 60
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '11'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
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

# The kill waits for the run state to record a confirmed page of the watched resource, not just for page reads: a page
# is confirmed once every document it gave came back from the target, and the state is flushed every 5 s, so a kill
# right after the first reads (0.8 s in on 2026-09-29) leaves nothing of that resource to resume from. The file is
# replaced atomically, so reading it mid-run is safe.
$script:confirmedAtKill = 0
$killWhen = {
    param($state)
    if ((Get-LogMatchCount $state.Log $pagePattern) -lt $KillAfterPages) { return $false }
    foreach ($file in @(Get-ChildItem -Path $stateHost -Filter '*.json' -File -ErrorAction SilentlyContinue))
    {
        try { $runState = Get-Content $file.FullName -Raw | ConvertFrom-Json } catch { continue }
        $watched = @($runState.Resources | Where-Object { $_.ResourceUrl -eq $WatchedResource -and -not $_.IsAuthorizationRetryPass })
        $furthest = @($watched | ForEach-Object { $_.Partitions } | ForEach-Object { [int] $_.LastCompletedPageNumber } | Measure-Object -Maximum).Maximum
        if ($furthest -ge $KillAfterConfirmedPage) { $script:confirmedAtKill = $furthest; return $true }
    }
    return $false
}
# The per-page read lines the kill trigger counts are logged at Debug.
$runA = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'run-a.log' -Arguments $arguments -KillWhen $killWhen -TimeoutMinutes $TimeoutMinutes -LogLevel Debug
$pagesA = Get-LogMatchCount $runA.Log $pagePattern

Assert-Condition $failures $runA.Killed "run A was killed mid-resource after $pagesA page request(s) of $WatchedResource, with a partition confirmed up to page $script:confirmedAtKill"
Assert-Condition $failures (Test-LogContains $runA.Log 'Run state for this run is kept at') 'run A announced where its run state is kept'
$stateFiles = @(Get-ChildItem -Path $stateHost -Filter '*.json' -File -ErrorAction SilentlyContinue)
Assert-Condition $failures ($stateFiles.Count -ge 1) "a run state file survived the kill ($($stateFiles.Count) file(s) in $stateHost)"

$runB = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'run-b.log' -Arguments ($arguments + @('--resumeLastRun=true')) -TimeoutMinutes $TimeoutMinutes -LogLevel Debug
$pagesB = Get-LogMatchCount $runB.Log $pagePattern

# The summary line is fixed template text and is written even as "0 of N partition(s) restart at a confirmed page",
# so the confirmed count is read out of it rather than the line's presence.
$resumeLine = Select-String -Path $runB.Log -Pattern '(\d+) of (\d+) partition\(s\) restart at a confirmed page' | Select-Object -First 1
$confirmed = if ($resumeLine) { [int] $resumeLine.Matches[0].Groups[1].Value } else { 0 }
$recorded = if ($resumeLine) { [int] $resumeLine.Matches[0].Groups[2].Value } else { 0 }
Assert-Condition $failures ($null -ne $resumeLine) 'run B reported that it resumed the previous run'
Assert-Condition $failures ($confirmed -ge 1) "run B restarted at least one partition at a confirmed page ($confirmed of $recorded)"
# The summary counts every resource; the watched one must be among those resumed (on 2026-09-29 102 finished
# resources were, and the watched one was read in full).
Assert-Condition $failures (Test-LogContains $runB.Log ('"{0}": Resuming \d+ partition\(s\) from the positions' -f [regex]::Escape($WatchedResource))) "run B resumed $WatchedResource itself from its recorded positions"
Assert-Condition $failures ($runB.ExitCode -eq 0) "run B exited with 0 (was $($runB.ExitCode))"
Assert-Condition $failures (-not (Test-LogContains $runB.Log 'no run state was found')) 'run B found the run state'

$counts = Compare-Counts -Arm $armDef -Resources ($Include.Split(',') | ForEach-Object { $_.Trim() }) -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "source and target counts match after the resume ($($counts.Mismatches.Count) mismatch(es))"

$leftover = @(Get-ChildItem -Path $stateHost -Filter '*.json' -File -ErrorAction SilentlyContinue)
Assert-Condition $failures ($leftover.Count -eq 0) "the run state was removed after the clean finish ($($leftover.Count) file(s) left)"

# Reference: the same selection read without interruption. Runs A and B recorded the last change version for the
# named target, so --lastChangeVersionProcessed=0 makes run C read the full source again instead of an empty window.
$referenceState = Join-Path $stateHost 'reference'
New-Item -ItemType Directory -Force -Path $referenceState | Out-Null
$referenceStatePublisher = if ($publisher.Mode -eq 'docker') { "$statePublisher/reference" } else { $referenceState }
$referenceArguments = @($arguments | Where-Object { $_ -notlike '--runStatePath=*' }) + @("--runStatePath=$referenceStatePublisher", '--lastChangeVersionProcessed=0')
$runC = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'run-c-reference.log' -Arguments $referenceArguments -TimeoutMinutes $TimeoutMinutes -LogLevel Debug
$pagesC = Get-LogMatchCount $runC.Log $pagePattern

Assert-Condition $failures ($runC.ExitCode -eq 0) "reference run C exited with 0 (was $($runC.ExitCode))"
Assert-Condition $failures ($pagesB -lt $pagesC) "run B requested fewer pages of $WatchedResource than the uninterrupted reference ($pagesB vs $pagesC): the confirmed pages were not read again"
Assert-Condition $failures (($pagesA + $pagesB) -ge $pagesC) "runs A and B together requested at least as many pages as the reference ($pagesA + $pagesB vs $pagesC): no unread page was skipped"

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds ($runA.Seconds + $runB.Seconds + $runC.Seconds) -Log $runB.Log -Notes "page size $PageSize; run A killed after $pagesA pages; run B requested $pagesB pages of $WatchedResource ($confirmed of $recorded partition(s) confirmed), uninterrupted reference $pagesC")
