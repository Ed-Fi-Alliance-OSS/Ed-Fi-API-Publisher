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
    [string] $ThrottledPattern = '/data/v3/ed-fi/.*'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '12'
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item source throttling on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); cap $MaxConcurrent; Retry-After ${RetryAfterSeconds}s; run folder $run"

Reset-RegressionTarget $armDef
Reset-ProxyMappings $armDef
Reset-ProxyJournal $armDef

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

$journal = Get-ProxyJournal $armDef '^/data/v3/'
$throttled = @($journal | Where-Object { $_.responseDefinition.status -eq 429 }).Count
Assert-Condition $failures ($throttled -gt 0) "the source answered 429 during the window ($throttled responses)"

$waits = @(Select-String -Path $result.Log -Pattern 'rejected as too many requests by the .* API\. Waiting (\d+(?:\.\d+)?)s' | ForEach-Object { [double] $_.Matches[0].Groups[1].Value })
Assert-Condition $failures ($waits.Count -gt 0) "the publisher logged the Retry-After waits ($($waits.Count) wait(s))"
if ($waits.Count -gt 0)
{
    $shortest = ($waits | Measure-Object -Minimum).Minimum
    Assert-Condition $failures ($shortest -ge $RetryAfterSeconds) "every wait honoured the Retry-After of ${RetryAfterSeconds}s (shortest $shortest s)"
}
Assert-Condition $failures (-not (Test-LogContains $result.Log 'still being rejected as too many requests')) 'no read ran out of retries'

$peak = Measure-ProxyConcurrency $journal
Assert-Condition $failures ($peak -le $MaxConcurrent) "in-flight source requests never exceeded the cap of $MaxConcurrent (journal peak $peak over $($journal.Count) requests)"

$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "counts match ($($counts.Mismatches.Count) mismatch(es))"

$journal | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'proxy-journal.json')

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes "cap $MaxConcurrent, journal peak $peak; retry budget $retryAttempts; $throttled x 429 with Retry-After $RetryAfterSeconds; $($waits.Count) waits")
