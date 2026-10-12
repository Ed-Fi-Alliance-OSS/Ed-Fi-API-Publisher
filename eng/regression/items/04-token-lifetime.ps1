# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 4: token lifetime and 401 handling (APIPUB-119), the four scenarios from Ana's comment on APIPUB-125.
      LongRun           a run crossing at least two refresh intervals of the source client; interval = half the token
                        lifetime (capped at the configured one), no auth errors, no drops
      Unauthorized401   the current token is invalidated mid-run; the 401 is replayed with a fresh token, nothing dropped
      TokenEndpointDown token invalidated while the token endpoint is unreachable; token requests during the outage,
                        then a Fatal entry and a non-zero exit
      BadCredentials    wrong secret from the start; immediate non-zero exit (code 3), nothing dropped silently
    The long run needs real time: with a 30-minute token the derived refresh interval is 15 minutes, so run it on
    Northridge overnight, or set SOURCE_TOKEN_TIMEOUT_MINUTES=2 in the arm's .env for a dry run in minutes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [string] $Scenarios = 'LongRun,Unauthorized401,TokenEndpointDown,BadCredentials',
    [int] $FaultAfterSeconds = 45,
    [int] $TimeoutMinutes = 240
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '04'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
$seconds = 0.0
$notes = @()
$counts = ''
Write-Host "Item $item token lifetime on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); scenarios $Scenarios; run folder $run"

$tokenMinutes = [int] $(if ($armDef.Env.Contains('SOURCE_TOKEN_TIMEOUT_MINUTES')) { $armDef.Env['SOURCE_TOKEN_TIMEOUT_MINUTES'] } else { 30 })

function Start-Scenario([string] $Name)
{
    Write-Host ''
    Write-Host "--- scenario: $Name ---"
    Reset-RegressionTarget $armDef
    Reset-ProxyMappings $armDef
    Reset-ProxyJournal $armDef
}

foreach ($scenario in ($Scenarios.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }))
{
    switch ($scenario)
    {
        'LongRun'
        {
            Start-Scenario $scenario
            $result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'long-run.log' -Arguments @('--includeDescriptors=true') -TimeoutMinutes $TimeoutMinutes
            $seconds += $result.Seconds
            Assert-Condition $failures ($result.ExitCode -eq 0) "long run exited with 0 (was $($result.ExitCode))"
            # The interval is min(configured, lifetime / 2) (BearerTokenRefreshPolicy); both inputs are on the line.
            $intervalMatch = Select-String -Path $result.Log -Pattern 'Bearer token refresh interval for "source" API client set to ([\d.]+) minutes \(configured interval: ([\d.]+) minutes, token lifetime reported by the API: ([\d.]+) minutes\)' | Select-Object -First 1
            $intervalOk = $false
            if ($intervalMatch)
            {
                $interval, $configured, $lifetime = $intervalMatch.Matches[0].Groups[1..3] | ForEach-Object { [double] $_.Value }
                $expected = [math]::Min($configured, $lifetime / 2)
                $intervalOk = [math]::Abs($interval - $expected) -le 0.05
            }
            Assert-Condition $failures $intervalOk "the source refresh interval is half the token lifetime, capped at the configured interval ($(if ($intervalMatch) { "$interval min for a $lifetime min token, configured $configured" } else { 'no interval line for the source client' }))"
            # Source refreshes only: the target client refreshes on its own schedule, and one interval crossed by both
            # would otherwise count as two.
            $refreshes = Get-LogMatchCount $result.Log 'Bearer token refreshed successfully for "source" API client'
            Assert-Condition $failures ($refreshes -ge 2) "the source token was refreshed at least twice during the run ($refreshes; token lifetime $tokenMinutes min, run $(Format-Duration $result.Seconds))"
            Assert-Condition $failures (-not (Test-LogContains $result.Log 'rejected as unauthorized')) 'no request was rejected as unauthorized during the long run'
            $long = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'long-run-counts.csv')
            Assert-Condition $failures ($long.Mismatches.Count -eq 0) "long run counts match ($($long.Mismatches.Count) mismatch(es))"
            $counts = $long.Summary
            $intervalLine = (Select-String -Path $result.Log -Pattern 'Bearer token refresh interval for.*' | Select-Object -First 1).Matches.Value
            if ($intervalLine) { $notes += ($intervalLine -replace '^.*Bearer token', 'Bearer token') }
        }
        'Unauthorized401'
        {
            # The token the publisher is using is read from the proxy journal and the proxy then answers 401 to that
            # token only, for the rest of the run: the request is replayed with a fresh token, which passes through.
            # (Deleting the token row in EdFi_Admin does not work: the ODS caches API client details.)
            Start-Scenario $scenario
            $script:faultId = $null
            $tick = {
                param($state)
                if (-not $script:faultId -and $state.Seconds -ge $FaultAfterSeconds)
                {
                    $authorization = Get-ProxyCurrentAuthorization $armDef
                    if ($authorization) { $script:faultId = Enable-ProxyFault $armDef '401-for-token' -Replace @{ AUTHORIZATION = $authorization } }
                }
            }
            $result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'unauthorized-401.log' -SourceUrl $armDef.ProxyUrl -Arguments @('--includeDescriptors=true') -Tick $tick -TimeoutMinutes $TimeoutMinutes
            if ($script:faultId) { Disable-ProxyFault $armDef $script:faultId }
            $seconds += $result.Seconds
            Assert-Condition $failures ($null -ne $script:faultId) "the run lasted long enough for the token to be invalidated at ${FaultAfterSeconds}s (ran $($result.Seconds)s)"
            $rejected = @(Get-ProxyJournal $armDef '^/data/v3/' | Where-Object { $_.responseDefinition.status -eq 401 }).Count
            Assert-Condition $failures ($rejected -gt 0) "the source rejected the invalidated token ($rejected x 401)"
            Assert-Condition $failures (Test-LogContains $result.Log 'rejected as unauthorized by the .* API\. Re-acquiring the bearer token and replaying') 'a 401 was reported and the request replayed with a fresh token'
            Assert-Condition $failures (Test-LogContains $result.Log 'Re-acquiring bearer token for .* API client after an unauthorized response') 'a new token was acquired after the 401'
            Assert-Condition $failures ($result.ExitCode -eq 0) "run with the invalidated token exited with 0 (was $($result.ExitCode))"
            $window = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'unauthorized-401-counts.csv')
            Assert-Condition $failures ($window.Mismatches.Count -eq 0) "no documents were dropped across the invalidation ($($window.Mismatches.Count) mismatch(es))"
            if (-not $counts) { $counts = $window.Summary }
        }
        'TokenEndpointDown'
        {
            # The current token is invalidated while the token endpoint is unreachable (proxy resets the connection),
            # so the publisher cannot re-acquire one: it must give up with a Fatal entry and a non-zero exit.
            Start-Scenario $scenario
            $script:faultIds = @()
            $script:outageStartedAt = $null
            $tick = {
                param($state)
                if ($script:faultIds.Count -eq 0 -and $state.Seconds -ge $FaultAfterSeconds)
                {
                    $authorization = Get-ProxyCurrentAuthorization $armDef
                    if ($authorization)
                    {
                        $script:outageStartedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                        $script:faultIds += Enable-ProxyFault $armDef 'token-unreachable'
                        $script:faultIds += Enable-ProxyFault $armDef '401-for-token' -Replace @{ AUTHORIZATION = $authorization }
                    }
                }
            }
            $result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'token-endpoint-down.log' -SourceUrl $armDef.ProxyUrl -Arguments @('--includeDescriptors=true') -Tick $tick -TimeoutMinutes ($tokenMinutes + 15)
            foreach ($id in $script:faultIds) { Disable-ProxyFault $armDef $id }
            $seconds += $result.Seconds
            Assert-Condition $failures ($script:faultIds.Count -eq 2) "the run lasted long enough for the outage to start at ${FaultAfterSeconds}s (ran $($result.Seconds)s)"
            Assert-Condition $failures (-not $result.Killed) "the publisher gave up on its own before the $($tokenMinutes + 15) minute timeout"
            Assert-Condition $failures ($result.ExitCode -ne 0) "run with the token endpoint down exited non-zero (was $($result.ExitCode))"
            Assert-Condition $failures (Test-LogContains $result.Log '\[FATL\]') 'a Fatal log entry explains the exit'
            # The exit must follow failed re-acquisition attempts, not an early give-up: the journal holds the token
            # requests the proxy reset after the outage started.
            $attempts = if ($script:outageStartedAt) { @(Get-ProxyJournal $armDef '/oauth/token' | Where-Object { $_.request.method -eq 'POST' -and [long] $_.request.loggedDate -ge $script:outageStartedAt }).Count } else { 0 }
            Assert-Condition $failures ($attempts -gt 0) "the publisher tried to re-acquire the token during the outage ($attempts token request(s) after it started)"
        }
        'BadCredentials'
        {
            Start-Scenario $scenario
            $result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'bad-credentials.log' -SourceSecret 'not-the-secret' -Arguments @('--includeDescriptors=true') -TimeoutMinutes 10
            $seconds += $result.Seconds
            Assert-Condition $failures ($result.ExitCode -eq 3) "run with rejected credentials exited with 3 (AuthenticationFailure; was $($result.ExitCode))"
            Assert-Condition $failures ($result.Seconds -lt 120) "the run ended immediately ($($result.Seconds)s) instead of completing while dropping documents"
            Assert-Condition $failures (-not (Test-LogContains $result.Log "Streaming of '")) 'no resource was streamed with rejected credentials'
        }
        default { $failures.Add("unknown scenario '$scenario'") }
    }
}

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts -Seconds $seconds -Log (Join-Path $run 'long-run.log') -Notes (@("scenarios $Scenarios; token lifetime $tokenMinutes min") + $notes -join '; '))
