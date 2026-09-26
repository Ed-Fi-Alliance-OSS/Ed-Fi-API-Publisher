# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 3: memory long run under a container memory limit with a forced-500 segment (APIPUB-112, 133, 134).
    Runs the publisher as a container (-PublisherImage) on the arm's network with --memory, samples the container
    memory, and mid-run turns the proxy's 500 fault on for a window. Once with offset/limit paging, once with cursor.
    Passes when: exit 0, no OOM kill, peak below the limit, every resource streamed exactly once (retries did not
    re-stream the resource), the fault was actually hit, and counts match.

    Long run: hours on Northridge. On Grand Bend the same script is a short dry run of the mechanics; point the arm's
    SOURCE_* values at the Northridge stack (see README, "Manual residue") for the ticket run.
.PARAMETER MemoryLimit
    Docker memory limit for the publisher container (default 768m).
.PARAMETER FaultAfterSeconds / FaultSeconds
    When the 500 window starts and how long it lasts.
.PARAMETER PagingModes
    Comma-separated: Offset, Cursor (default both).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [string] $MemoryLimit = '768m',
    [int] $FaultAfterSeconds = 120,
    [int] $FaultSeconds = 60,
    [string] $PagingModes = 'Offset,Cursor',
    [int] $TimeoutMinutes = 720
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '03'
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item memory long run on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); limit $MemoryLimit; run folder $run"

if ($publisher.Mode -ne 'docker')
{
    $failures.Add('item 3 needs the publisher as a container (-PublisherImage) so that a memory limit applies')
    exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures)
}

$limitMB = switch -Regex ($MemoryLimit) { '^(\d+)m$' { [int] $Matches[1] } '^(\d+)g$' { [int] $Matches[1] * 1024 } default { throw "MemoryLimit must look like 768m or 2g." } }

# The 500s must stay transient: page reads retry with an exponential backoff from --retryStartingDelayMilliseconds,
# so the retry budget (1 s * (2^attempts - 1)) is sized to outlast the window with margin. The default budget
# (5 attempts from 100 ms, about 3 s) abandons every read caught in a window of more than a few seconds.
$retryAttempts = [int] [math]::Ceiling([math]::Log($FaultSeconds * 1.5 + 1, 2))
$retryArguments = @('--retryStartingDelayMilliseconds=1000', "--maxRetryAttempts=$retryAttempts")
$seconds = 0.0
$peaks = @()
$countSummaries = @()

foreach ($mode in ($PagingModes.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }))
{
    Write-Host ''
    Write-Host "--- paging mode: $mode ---"
    Reset-RegressionTarget $armDef
    Reset-ProxyMappings $armDef
    Reset-ProxyJournal $armDef

    $script:faultId = $null
    $script:faultStartedAt = $null
    $script:faultDone = $false

    $tick = {
        param($state)
        if (-not $script:faultId -and $state.Seconds -ge $FaultAfterSeconds)
        {
            $script:faultId = Enable-ProxyFault $armDef '500-window' -Replace @{ URL_PATTERN = '/data/v3/.*' }
            $script:faultStartedAt = $state.Seconds
        }
        elseif ($script:faultId -and -not $script:faultDone -and $state.Seconds -ge ($script:faultStartedAt + $FaultSeconds))
        {
            Disable-ProxyFault $armDef $script:faultId
            $script:faultDone = $true
        }
    }

    $disableCursor = ($mode -eq 'Offset').ToString().ToLowerInvariant()
    $result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName "$($mode.ToLowerInvariant()).log" `
        -SourceUrl $armDef.ProxyUrl -Arguments (@("--disableCursorPaging=$disableCursor", '--includeDescriptors=true') + $retryArguments) `
        -MemoryLimit $MemoryLimit -SampleSeconds 5 -Tick $tick -TimeoutMinutes $TimeoutMinutes
    if ($script:faultId -and -not $script:faultDone) { Disable-ProxyFault $armDef $script:faultId }
    $seconds += $result.Seconds
    $peaks += "$mode $($result.PeakWorkingSetMB) MB"

    Assert-Condition $failures ($result.ExitCode -eq 0) "$mode run exited with 0 (was $($result.ExitCode))"
    Assert-Condition $failures (-not (Test-LogContains $result.Log 'OOM-killed')) "$mode run was not OOM-killed"
    Assert-Condition $failures ($result.PeakWorkingSetMB -lt $limitMB) "$mode run peak memory $($result.PeakWorkingSetMB) MB stayed below the $limitMB MB limit"
    Assert-Condition $failures ($null -ne $script:faultId) "$mode run lasted long enough for the 500 window to start at ${FaultAfterSeconds}s (ran $($result.Seconds)s)"

    $injected = @(Get-ProxyJournal $armDef '^/data/v3/' | Where-Object { $_.responseDefinition.status -eq 500 }).Count
    Assert-Condition $failures ($injected -gt 0) "$mode run hit the injected 500s ($injected responses)"
    $retried = Get-LogMatchCount $result.Log "failed with status 'InternalServerError'\. Retrying"
    Assert-Condition $failures ($retried -gt 0) "$mode run retried the failed page reads ($retried retry lines)"

    $resources = Get-StreamedResources $result.Log
    $restreamed = @($resources | Where-Object { (Get-LogMatchCount $result.Log ("Streaming of '{0}' completed" -f [regex]::Escape($_))) -ne 1 })
    Assert-Condition $failures ($resources.Count -gt 0 -and $restreamed.Count -eq 0) "$mode run streamed every resource exactly once ($($restreamed.Count) re-streamed: $($restreamed -join ', '))"

    $counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run "$($mode.ToLowerInvariant())-counts.csv")
    Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "$mode run counts match ($($counts.Mismatches.Count) mismatch(es))"
    $countSummaries += "${mode}: $($counts.Summary)"
}

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts ($countSummaries -join ' / ') -Seconds $seconds -Log (Join-Path $run 'offset.log') -Notes "limit $MemoryLimit; peak $($peaks -join ', '); 500 window ${FaultSeconds}s after ${FaultAfterSeconds}s; retry budget $retryAttempts attempts from 1 s; memory samples next to each log")
