# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Measures ODS/API 7.3+ page read latency for offset/limit paging versus partitioned cursor paging, directly
    against the API (no API Publisher involved), so the two can be compared without any client-side effects
    (APIPUB-139 / APIPUB-141).
.DESCRIPTION
    Each round reads the whole resource once per mode with the same number of parallel workers:
      - offset : workers take every Nth page (offset/limit), the way the publisher's page block does
      - cursor : one worker per partition follows Next-Page-Token until the empty page ends the partition,
                 processed <Workers> at a time, the way the publisher walks partitions
    Reports wall clock, request count (cursor includes the empty tail request per partition), failed requests
    (a failed page is counted and not retried; under cursor paging it also ends that partition's chain, so a run
    with failures is incomplete and its wall clock is a lower bound), average latency per request and how many partitions
    the API actually returned for each requested count (the API sizes
    partitions itself: at least 5 x its default page size limit per partition unless allowSmallPartitions=true).
    Requires PowerShell 7+ (ForEach-Object -Parallel). Run it from a PowerShell session; when launched with
    "pwsh -File" from another shell, array arguments are not parsed as arrays.
.PARAMETER BaseUrl
    Source ODS/API base URL (e.g. http://localhost:8001).
.PARAMETER Key
    Client key (client credentials).
.PARAMETER Secret
    Client secret (client credentials).
.PARAMETER Resource
    Resource path, default /ed-fi/students.
.PARAMETER PageSize
    Page size for both modes (limit / pageSize), default 500.
.PARAMETER Workers
    Parallel requests in flight, default 5 (the publisher's MaxDegreeOfParallelismForStreamResourcePages default).
.PARAMETER PartitionCounts
    Partition counts to request for the cursor runs, default 5.
.PARAMETER Rounds
    Rounds of offset-then-cursor runs, default 3. The first round doubles as the cache warm-up; compare the rest.
.PARAMETER RequestTimeoutSec
    Per-request timeout, default 120 s (Invoke-WebRequest waits forever by default; a request that times out
    counts as failed).
.EXAMPLE
    .\eng\Measure-PagingLatency.ps1 -BaseUrl http://localhost:8001 -Key northridgeKey -Secret northridgeSecret
.EXAMPLE
    .\eng\Measure-PagingLatency.ps1 -BaseUrl http://localhost:8001 -Key k -Secret s -Resource /ed-fi/studentSectionAssociations -PartitionCounts 1,5,10 -Rounds 4
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [string] $Key,
    [Parameter(Mandatory)] [string] $Secret,
    [string] $Resource = '/ed-fi/students',
    [ValidateRange(1, 500)] [int] $PageSize = 500,
    [ValidateRange(1, 50)] [int] $Workers = 5,
    [ValidateRange(1, 200)] [int[]] $PartitionCounts = @(5),
    [ValidateRange(1, 20)] [int] $Rounds = 3,
    [ValidateRange(1, 3600)] [int] $RequestTimeoutSec = 120
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')

function Get-AccessToken {
    (Invoke-RestMethod -Method Post -Uri "$BaseUrl/oauth/token" -Body @{
        grant_type = 'client_credentials'; client_id = $Key; client_secret = $Secret }).access_token
}

function Get-HeaderValue([object] $Headers, [string] $Name) {
    # PowerShell 7 exposes response headers as string arrays
    $value = $Headers[$Name]
    if ($value -is [array]) { $value = $value | Select-Object -First 1 }
    return [string] $value
}

$token = Get-AccessToken
$headers = @{ Authorization = "Bearer $token" }
$dataUrl = "$BaseUrl/data/v3$Resource"

$countResponse = Invoke-WebRequest -Uri "$dataUrl`?offset=0&limit=1&totalCount=true" -Headers $headers -UseBasicParsing -TimeoutSec $RequestTimeoutSec
$total = [int] (Get-HeaderValue $countResponse.Headers 'Total-Count')
if ($total -le 0) { throw "Resource '$Resource' reports no items (Total-Count header missing or zero); nothing to measure." }

$pageCount = [math]::Ceiling($total / $PageSize)
Write-Host "$Resource`: $total items, pageSize $PageSize ($pageCount offset pages), $Workers workers, rounds $Rounds"

# Warm-up so the first measured round is not paying for a cold cache alone
1..3 | ForEach-Object { $null = Invoke-WebRequest -Uri "$dataUrl`?offset=0&limit=$PageSize" -Headers $headers -UseBasicParsing -TimeoutSec $RequestTimeoutSec }

$results = New-Object System.Collections.Generic.List[object]

foreach ($round in 1..$Rounds) {
    # ---- offset/limit: workers take every Nth page ----
    $offsets = 0..($pageCount - 1) | ForEach-Object { $_ * $PageSize }
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $offsetWorkers = 0..($Workers - 1) | ForEach-Object -Parallel {
        $worker = $_
        $requestHeaders = @{ Authorization = "Bearer $using:token" }
        $latencies = @(); $failed = 0
        foreach ($offset in ($using:offsets | Where-Object { ($_ / $using:PageSize) % $using:Workers -eq $worker })) {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            try { $null = Invoke-WebRequest -Uri "$using:dataUrl`?offset=$offset&limit=$using:PageSize" -Headers $requestHeaders -UseBasicParsing -TimeoutSec $using:RequestTimeoutSec }
            catch { $failed++ }   # a failed page (e.g. HTTP 500 from a query timeout) is counted, not retried
            $sw.Stop()
            $latencies += $sw.ElapsedMilliseconds
        }
        [pscustomobject]@{ Requests = $latencies.Count; TotalMs = ($latencies | Measure-Object -Sum).Sum; Failed = $failed }
    } -ThrottleLimit $Workers
    $stopwatch.Stop()

    $requests = [int] ($offsetWorkers.Requests | Measure-Object -Sum).Sum
    $results.Add([pscustomobject]@{
        Round = $round; Mode = 'offset'; PartitionsRequested = ''; PartitionsReturned = ''
        Requests = $requests; EmptyTailRequests = 0; FailedRequests = [int] ($offsetWorkers.Failed | Measure-Object -Sum).Sum
        AvgRequestMs = [int] ((($offsetWorkers.TotalMs | Measure-Object -Sum).Sum) / [math]::Max(1, $requests))
        WallMs = [int] $stopwatch.ElapsedMilliseconds
    })
    Write-Host ("round {0} offset          : wall {1,6} ms, {2} requests, {3} failed" -f $round, $stopwatch.ElapsedMilliseconds, $requests, [int] ($offsetWorkers.Failed | Measure-Object -Sum).Sum)

    # ---- cursor: one chain per partition, <Workers> chains at a time ----
    foreach ($partitionCount in $PartitionCounts) {
        $partitionsWatch = [Diagnostics.Stopwatch]::StartNew()
        $tokens = @((Invoke-RestMethod -Uri "$dataUrl/partitions?number=$partitionCount" -Headers $headers -TimeoutSec $RequestTimeoutSec).pageTokens)
        $partitionsWatch.Stop()

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $chains = $tokens | ForEach-Object -Parallel {
            $pageToken = $_
            $requestHeaders = @{ Authorization = "Bearer $using:token" }
            $latencies = @(); $empty = 0; $failed = 0
            while ($pageToken) {
                $sw = [Diagnostics.Stopwatch]::StartNew()
                try { $response = Invoke-WebRequest -Uri "$using:dataUrl`?pageToken=$pageToken&pageSize=$using:PageSize" -Headers $requestHeaders -UseBasicParsing -TimeoutSec $using:RequestTimeoutSec }
                catch { $sw.Stop(); $latencies += $sw.ElapsedMilliseconds; $failed++; break }   # no token to continue with: the chain ends here
                $sw.Stop()
                $latencies += $sw.ElapsedMilliseconds
                if ($response.RawContentLength -le 2) { $empty++ }   # "[]" -- the page that ends the partition
                $next = $response.Headers['Next-Page-Token']
                if ($next -is [array]) { $next = $next | Select-Object -First 1 }
                $pageToken = [string] $next
            }
            [pscustomobject]@{ Requests = $latencies.Count; TotalMs = ($latencies | Measure-Object -Sum).Sum; Empty = $empty; Failed = $failed }
        } -ThrottleLimit $Workers
        $stopwatch.Stop()

        $requests = [int] ($chains.Requests | Measure-Object -Sum).Sum
        $emptyTails = [int] ($chains.Empty | Measure-Object -Sum).Sum
        $wall = [int] ($stopwatch.ElapsedMilliseconds + $partitionsWatch.ElapsedMilliseconds)
        $results.Add([pscustomobject]@{
            Round = $round; Mode = 'cursor'; PartitionsRequested = $partitionCount; PartitionsReturned = $tokens.Count
            Requests = $requests; EmptyTailRequests = $emptyTails; FailedRequests = [int] ($chains.Failed | Measure-Object -Sum).Sum
            AvgRequestMs = [int] ((($chains.TotalMs | Measure-Object -Sum).Sum) / [math]::Max(1, $requests))
            WallMs = $wall
        })
        Write-Host ("round {0} cursor n={1,-4}    : wall {2,6} ms (partitions call {3} ms, {4} returned), {5} requests incl. {6} empty tails, {7} failed" -f `
            $round, $partitionCount, $wall, $partitionsWatch.ElapsedMilliseconds, $tokens.Count, $requests, $emptyTails, [int] ($chains.Failed | Measure-Object -Sum).Sum)
    }
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host

# Summary over rounds 2..N (round 1 is the warm-up)
$measured = if ($Rounds -gt 1) { $results | Where-Object Round -gt 1 } else { $results }
$summary = $measured | Group-Object Mode, PartitionsRequested | ForEach-Object {
    $group = $_.Group
    [pscustomobject]@{
        Mode                = $group[0].Mode
        PartitionsRequested = $group[0].PartitionsRequested
        PartitionsReturned  = $group[0].PartitionsReturned
        Requests            = $group[0].Requests
        AvgWallMs           = [int] ($group.WallMs | Measure-Object -Average).Average
        MinWallMs           = ($group.WallMs | Measure-Object -Minimum).Minimum
        AvgRequestMs        = [int] ($group.AvgRequestMs | Measure-Object -Average).Average
        FailedRequests      = [int] ($group.FailedRequests | Measure-Object -Sum).Sum
    }
}
Write-Host ("Summary over rounds {0}..{1}:" -f [math]::Min(2, $Rounds), $Rounds)
$summary | Format-Table -AutoSize | Out-String | Write-Host

$offsetAvg = ($summary | Where-Object Mode -eq 'offset').AvgWallMs
foreach ($cursor in ($summary | Where-Object Mode -eq 'cursor')) {
    $delta = if ($offsetAvg) { [math]::Round(100.0 * ($cursor.AvgWallMs - $offsetAvg) / $offsetAvg, 1) } else { 0 }
    Write-Host ("cursor with {0} partition(s) requested ({1} returned): {2:+0.0;-0.0}% wall clock versus offset" -f `
        $cursor.PartitionsRequested, $cursor.PartitionsReturned, $delta)
}
