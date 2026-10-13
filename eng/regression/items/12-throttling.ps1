# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 12: source throttling (APIPUB-140). The proxy answers 429 with Retry-After for a window; the publisher must
    wait out the stated interval and finish clean, and the proxy's request journal must show that the number of
    in-flight source requests never exceeded --maxConcurrentSourceRequests.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [int] $MaxConcurrent = 4,
    [int] $RetryAfterSeconds = 3,
    [int] $FaultAfterSeconds = 15,
    [int] $FaultSeconds = 20,
    # Default: every ed-fi resource below the source's data path (/data/v3 for an ODS/API, /api/data for a DMS).
    [string] $ThrottledPattern
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '12'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item source throttling on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); cap $MaxConcurrent; Retry-After ${RetryAfterSeconds}s; run folder $run"

Reset-RegressionTarget $armDef
Reset-ProxyMappings $armDef
Reset-ProxyJournal $armDef
$dataPath = (Get-ProxySourcePaths $armDef).Data
if (-not $ThrottledPattern) { $ThrottledPattern = "$dataPath/ed-fi/.*" }

# A read is abandoned once its 429 retries run out (default: MaxRetryAttempts, 5), so the retry budget must outlast
# the window: window / Retry-After plus a margin. With the default budget a 20 s window at 3 s exhausts reads.
$retryAttempts = [math]::Ceiling($FaultSeconds / [math]::Max($RetryAfterSeconds, 1)) + 5

$script:faultId = $null; $script:faultStartedAt = $null; $script:faultDone = $false
$tick = {
    param($state)
    if (-not $script:faultId -and $state.Seconds -ge $FaultAfterSeconds)
    {
        $script:faultId = Enable-ProxyFault $armDef '429-retry-after' -Replace @{ URL_PATTERN = $ThrottledPattern; RETRY_AFTER = $RetryAfterSeconds }
        $script:faultStartedAt = $state.Seconds
    }
    elseif ($script:faultId -and -not $script:faultDone -and $state.Seconds -ge ($script:faultStartedAt + $FaultSeconds))
    {
        Disable-ProxyFault $armDef $script:faultId
        $script:faultDone = $true
    }
}

$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -SourceUrl $armDef.ProxyUrl -LogLevel Debug `
    -Arguments @("--maxConcurrentSourceRequests=$MaxConcurrent", "--tooManyRequestsRetryAttempts=$retryAttempts", '--includeDescriptors=true') -Tick $tick
if ($script:faultId -and -not $script:faultDone) { Disable-ProxyFault $armDef $script:faultId }

Assert-Condition $failures ($null -ne $script:faultId) "the run lasted long enough for the 429 window to start at ${FaultAfterSeconds}s (ran $($result.Seconds)s)"
Assert-Condition $failures ($result.ExitCode -eq 0) "the run exited with 0 (was $($result.ExitCode))"

$journal = Get-ProxyJournal $armDef "^$([regex]::Escape($dataPath))/"
$throttled = @($journal | Where-Object { $_.responseDefinition.status -eq 429 }).Count
Assert-Condition $failures ($throttled -gt 0) "the source answered 429 during the window ($throttled responses)"

$waits = @(Select-String -Path $result.Log -Pattern 'rejected as too many requests by the .* API\. Waiting (\d+(?:\.\d+)?)s' | ForEach-Object { [double] $_.Matches[0].Groups[1].Value })
Assert-Condition $failures ($waits.Count -gt 0) "the publisher logged the Retry-After waits ($($waits.Count) wait(s))"

# The wait is measured where the source sees it, not taken from the publisher's own log line: for every 429, the gap
# until the proxy received the next request for the same URL (both timestamps from the proxy's clock).
$byUrl = $journal | Group-Object { $_.request.url }
$gaps = @(foreach ($group in $byUrl)
{
    $entries = @($group.Group | Sort-Object { [long] $_.request.loggedDate })
    for ($index = 0; $index -lt $entries.Count - 1; $index++)
    {
        if ($entries[$index].responseDefinition.status -eq 429) { ([long] $entries[$index + 1].request.loggedDate - [long] $entries[$index].request.loggedDate) / 1000.0 }
    }
})
Assert-Condition $failures ($gaps.Count -gt 0) "the journal shows the requests that followed the 429s ($($gaps.Count) retried)"
$shortest = if ($gaps.Count -gt 0) { ($gaps | Measure-Object -Minimum).Minimum } else { $null }
# 50 ms of slack for the proxy's own timestamping; a retry that ignores Retry-After comes back within milliseconds.
Assert-Condition $failures ($gaps.Count -gt 0 -and $shortest -ge ($RetryAfterSeconds - 0.05)) ("every retry after a 429 came at least the Retry-After of {0}s later (shortest gap {1:N2}s over {2} retries)" -f $RetryAfterSeconds, $shortest, $gaps.Count)
Assert-Condition $failures (-not (Test-LogContains $result.Log 'still being rejected as too many requests')) 'no read ran out of retries'

$peak = Measure-ProxyConcurrency $journal
Assert-Condition $failures ($peak -le $MaxConcurrent) "in-flight source requests never exceeded the cap of $MaxConcurrent (journal peak $peak over $($journal.Count) requests)"

$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "counts match ($($counts.Mismatches.Count) mismatch(es))"

Save-ProxyJournal -Entries $journal -Path (Join-Path $run 'proxy-journal.json')

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes "cap $MaxConcurrent, journal peak $peak; retry budget $retryAttempts; $throttled x 429 with Retry-After $RetryAfterSeconds; $($waits.Count) waits logged, shortest retry gap $(if ($null -ne $shortest) { '{0:N2}s' -f $shortest } else { 'n/a' })")
