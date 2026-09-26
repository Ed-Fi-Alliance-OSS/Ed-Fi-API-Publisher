# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 6: a source document with an invalid id is logged as a controlled error and the run continues (APIPUB-102).
    A conformant ODS never returns one, so the proxy serves one fixed educationContents page (count and page request
    alike; the resource has no dependencies) whose second element carries
    each of the five invalid shapes in turn (absent, null, empty, object, array). For each shape: one Error line with the
    page locator and item index, the two sibling documents published, non-zero exit, and the locator's offset/limit
    matching the request the proxy actually saw (Ana's assumption to validate).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [string] $Shapes = 'absent,null,empty,object,array'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '06'
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
$seconds = 0.0
$exitCodes = @()
$problems = @()
Write-Host "Item $item invalid id handling on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); shapes $Shapes; run folder $run"

# How each shape is written into the stubbed page (the file carries "id": "__INVALID_ID__" so it stays valid JSON).
$literals = @{
    absent = @{ '"id": "__INVALID_ID__",' = '' }
    null   = @{ '"__INVALID_ID__"' = 'null' }
    empty  = @{ '"__INVALID_ID__"' = '""' }
    object = @{ '"__INVALID_ID__"' = '{ "unexpected": true }' }
    array  = @{ '"__INVALID_ID__"' = '[ "unexpected" ]' }
}

Reset-RegressionTarget $armDef
Reset-ProxyMappings $armDef
$targetToken = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret

foreach ($shape in ($Shapes.Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ }))
{
    if (-not $literals.ContainsKey($shape)) { $failures.Add("unknown shape '$shape'"); continue }

    Write-Host ''
    Write-Host "--- invalid id shape: $shape ---"
    Reset-ProxyJournal $armDef
    $faultId = Enable-ProxyFault $armDef 'invalid-id-page' -Literal $literals[$shape]
    try
    {
        $result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName "$shape.log" -SourceUrl $armDef.ProxyUrl -Arguments @('--disableCursorPaging=true', '--includeOnly=/ed-fi/educationContents')
    }
    finally
    {
        Disable-ProxyFault $armDef $faultId
    }
    $seconds += $result.Seconds
    $exitCodes += "$shape=$($result.ExitCode)"

    Assert-Condition $failures ($result.ExitCode -ne 0) "[$shape] the run exited non-zero (was $($result.ExitCode))"

    # Serilog renders the locator and the problem as quoted strings, e.g.
    #   "/ed-fi/educationContents": Source item at "offset 0, limit 500", index 1 "has a null 'id'" and will not be published.
    $errorLine = Select-String -Path $result.Log -Pattern 'Source item at "?offset (\d+), limit (\d+)"?, index 1 "?.*will not be published' | Select-Object -First 1
    Assert-Condition $failures ($null -ne $errorLine) "[$shape] one Error line names the page locator, index 1 and the shape found"
    if ($errorLine)
    {
        $problems += "$shape -> " + (($errorLine.Line -replace '^.*index 1 ', '') -replace ' and will not be published.*$', '').Trim('"')
        $offset = $errorLine.Matches[0].Groups[1].Value
        $limit = $errorLine.Matches[0].Groups[2].Value
        $served = @(Get-ProxyJournal $armDef "^/data/v3/ed-fi/educationContents\?.*offset=$offset&limit=$limit")
        Assert-Condition $failures ($served.Count -ge 1) "[$shape] the locator (offset $offset, limit $limit) matches a request the proxy served"
    }

    foreach ($sibling in 1, 3)
    {
        $found = @(Invoke-Api -BaseUrl $armDef.TargetUrl -Token $targetToken -Resource '/ed-fi/educationContents' -Query "?contentIdentifier=regression-invalid-id-$sibling")
        Assert-Condition $failures ($found.Count -eq 1) "[$shape] sibling document regression-invalid-id-$sibling was published to the target"
    }
    $rejected = @(Invoke-Api -BaseUrl $armDef.TargetUrl -Token $targetToken -Resource '/ed-fi/educationContents' -Query '?contentIdentifier=regression-invalid-id-2')
    Assert-Condition $failures ($rejected.Count -eq 0) "[$shape] the document with the invalid id (regression-invalid-id-2) was not published"
}

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts '3-item stubbed page per shape, 2 published' -Seconds $seconds -Log (Join-Path $run 'object.log') -Notes "exit codes $($exitCodes -join ', '); problems reported: $($problems -join ' | ')")
