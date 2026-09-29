# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 9: Discovery-based routing leaves ODS/API behaviour identical to v1.3 (APIPUB-108, APIPUB-109).
    Runs the v1.3 image and the release candidate against the same arm at Debug level through the proxy, then diffs
    the per-resource counts, the URL shapes in the logs, and the source request shapes in the proxy journal (every
    request, with the count per shape). All must be equal.
    Container runs only: the baseline is the published edfialliance/ods-api-publisher:v1.3.0 image.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [string] $BaselineImage = 'edfialliance/ods-api-publisher:v1.3.0'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '09'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item Discovery routing regression on arm $($armDef.Name) ($($armDef.Description)); baseline $BaselineImage vs $($publisher.Label); run folder $run"

if ($publisher.Mode -ne 'docker')
{
    $failures.Add('item 9 compares two container images; give the release candidate with -PublisherImage')
    exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures)
}

$baseline = Resolve-Publisher -Image $BaselineImage -Baseline
$arguments = @('--disableCursorPaging=true', '--includeDescriptors=true')

# Both runs go through the proxy so its journal holds every source request; the journal's URL shapes (path plus
# query parameter names) and the count of requests per shape are the strongest evidence that routing did not change.
function Get-JournalShapes
{
    param([string] $SaveAs)

    $journal = Get-ProxyJournal $armDef
    if ($SaveAs) { Save-ProxyJournal -Entries $journal -Path (Join-Path $run $SaveAs) -Compress }

    $shapes = @{}
    foreach ($entry in $journal)
    {
        $parts = $entry.request.url.Split('?', 2)
        $names = @()
        if ($parts.Count -eq 2) { $names = @($parts[1].Split('&') | ForEach-Object { $_.Split('=')[0] } | Sort-Object -Unique) }

        # The total-count request changed shape on purpose in v1.4 (APIPUB-139, PR #166: limit=0&totalCount=true
        # instead of offset=0&limit=1&totalCount=true), so count requests are compared as one shape without their
        # paging parameters. Anything else that differs is a routing regression.
        if ($names -contains 'totalCount') { $names = @($names | Where-Object { $_ -notin 'offset', 'limit', 'pageSize', 'pageToken' }); $shape = "COUNT $($parts[0])" }
        else { $shape = "$($entry.request.method) $($parts[0])" }
        if ($names.Count -gt 0) { $shape += '?' + ($names -join '&') }
        # Page and item ids vary between runs; only the resource path matters.
        $shape = $shape -replace '/[0-9a-f]{32}(\?|$)', '/{id}$1'
        $shapes[$shape] = 1 + $(if ($shapes.ContainsKey($shape)) { $shapes[$shape] } else { 0 })
    }
    return $shapes
}

Reset-RegressionTarget $armDef
Reset-ProxyMappings $armDef
Reset-ProxyJournal $armDef
$v13 = Invoke-Publisher -Publisher $baseline -Arm $armDef -RunFolder $run -LogName 'v1.3.log' -SourceUrl $armDef.ProxyUrl -Arguments $arguments -LogLevel Debug
$v13Counts = Compare-Counts -Arm $armDef -Log $v13.Log -ReportCsv (Join-Path $run 'v1.3-counts.csv')
$v13Shapes = Get-JournalShapes -SaveAs 'v1.3-proxy-journal.json'

Reset-RegressionTarget $armDef
Reset-ProxyJournal $armDef
$rc = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'rc.log' -SourceUrl $armDef.ProxyUrl -Arguments $arguments -LogLevel Debug
$rcCounts = Compare-Counts -Arm $armDef -Log $rc.Log -ReportCsv (Join-Path $run 'rc-counts.csv')
$rcShapes = Get-JournalShapes -SaveAs 'rc-proxy-journal.json'

# An empty journal (runs that bypassed the proxy) would make every shape comparison below trivially equal.
Assert-Condition $failures ($v13Shapes.Count -gt 0 -and $rcShapes.Count -gt 0) "the proxy journal holds the source requests of both runs ($($v13Shapes.Count) and $($rcShapes.Count) shapes)"
Assert-Condition $failures ($v13.ExitCode -eq 0) "v1.3 run exited with 0 (was $($v13.ExitCode))"
Assert-Condition $failures ($rc.ExitCode -eq 0) "release candidate run exited with 0 (was $($rc.ExitCode))"
Assert-Condition $failures ($v13Counts.Mismatches.Count -eq 0) "v1.3 counts match ($($v13Counts.Mismatches.Count) mismatch(es))"
Assert-Condition $failures ($rcCounts.Mismatches.Count -eq 0) "release candidate counts match ($($rcCounts.Mismatches.Count) mismatch(es))"

$rowsDiffer = @()
foreach ($row in $v13Counts.Rows)
{
    $other = $rcCounts.Rows | Where-Object { $_.Resource -eq $row.Resource } | Select-Object -First 1
    if (-not $other -or "$($other.Target)" -ne "$($row.Target)") { $rowsDiffer += $row.Resource }
}
Assert-Condition $failures ($rowsDiffer.Count -eq 0) "both versions published the same per-resource counts ($($rowsDiffer.Count) differ: $($rowsDiffer -join ', '))"

$urls = Compare-RequestUrls -LogA $v13.Log -LogB $rc.Log
@("Only in v1.3:") + $urls.OnlyInA + @("", "Only in the release candidate:") + $urls.OnlyInB | Set-Content (Join-Path $run 'url-diff.txt')
Assert-Condition $failures ($urls.OnlyInA.Count -eq 0 -and $urls.OnlyInB.Count -eq 0) "the request URL shapes are identical ($($urls.Common) common, $($urls.OnlyInA.Count) only in v1.3, $($urls.OnlyInB.Count) only in the RC; see url-diff.txt)"

$shapeReport = New-Object System.Collections.Generic.List[string]
$shapesDiffer = 0
foreach ($shape in (@($v13Shapes.Keys) + @($rcShapes.Keys) | Sort-Object -Unique))
{
    $a = $(if ($v13Shapes.ContainsKey($shape)) { $v13Shapes[$shape] } else { 0 })
    $b = $(if ($rcShapes.ContainsKey($shape)) { $rcShapes[$shape] } else { 0 })
    if ($a -ne $b) { $shapesDiffer++ }
    $shapeReport.Add(('{0,7} {1,7}  {2}  {3}' -f $a, $b, $(if ($a -eq $b) { '==' } else { '!=' }), $shape))
}
@('   v1.3      RC') + $shapeReport | Set-Content (Join-Path $run 'source-request-shapes.txt')
Assert-Condition $failures ($shapesDiffer -eq 0) "every source request shape was made the same number of times by both versions ($($v13Shapes.Count) shapes in v1.3, $($rcShapes.Count) in the RC, $shapesDiffer differ; see source-request-shapes.txt)"

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $rcCounts.Summary -Seconds ($v13.Seconds + $rc.Seconds) -Log $rc.Log -Notes "baseline $BaselineImage $(Format-Duration $v13.Seconds), RC $(Format-Duration $rc.Seconds); $($rcShapes.Count) source request shapes via the proxy journal; known difference: count requests use limit=0 since APIPUB-139 (compared as one shape)")
