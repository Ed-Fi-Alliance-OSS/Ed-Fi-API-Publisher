# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 6: a source document with an invalid id is logged as a controlled error and the run continues (APIPUB-102).
    A conformant ODS never returns one, so the proxy serves one fixed educationContents page (count and page request
    alike; the resource has no dependencies) whose second element carries
    each of the five invalid shapes in turn (absent, null, empty, object, array). For each shape, on a freshly reset
    target: one Error line with the page locator and item index, the two sibling documents published, non-zero exit,
    the locator's offset/limit matching the request the proxy actually saw, one error record without the document's
    body or id contents, and the last change version in the PostgreSQL configuration store not advanced (comment 98862).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [string] $Shapes = 'absent,null,empty,object,array',
    [string] $EncryptionPassword = 'regression-store-password'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '06'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
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

Reset-ProxyMappings $armDef
# /data/v3 for an ODS/API source, /api/data for a DMS source.
$dataPath = (Get-ProxySourcePaths $armDef).Data

# The runs go through the PostgreSQL configuration store (as in item 10) because the plainText store never records a
# change version: only a store that does can show that a run with an unpublishable document did not advance it.
$sourceName = 'Regression_InvalidId_Source'
$targetName = 'Regression_InvalidId_Target'
$seededVersion = 0L
$storeConnection = Initialize-PostgreSqlConfigurationStore -Arm $armDef -Publisher $publisher -RunFolder $run -SourceName $sourceName -TargetName $targetName `
    -EncryptionPassword $EncryptionPassword -SourceUrl $armDef.ProxyUrl -LastChangeVersion $seededVersion
$storeArguments = @('--configurationStoreProvider=postgreSql', "--postgreSqlEncryptionPassword=$EncryptionPassword", "--sourceName=$sourceName", "--targetName=$targetName")

foreach ($shape in ($Shapes.Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ }))
{
    if (-not $literals.ContainsKey($shape)) { $failures.Add("unknown shape '$shape'"); continue }

    Write-Host ''
    Write-Host "--- invalid id shape: $shape ---"
    # Per shape: the sibling checks below must find what THIS run published, not what an earlier shape left behind.
    Reset-RegressionTarget $armDef
    $targetToken = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret
    Reset-ProxyJournal $armDef
    $versionBefore = Get-StoredLastChangeVersion $armDef $sourceName $targetName
    $faultId = Enable-ProxyFault $armDef 'invalid-id-page' -Literal $literals[$shape]
    try
    {
        $result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName "$shape.log" -NoConnectionArguments -ConfigurationStoreConnectionString $storeConnection `
            -Arguments ($storeArguments + @('--disableCursorPaging=true', '--includeOnly=/ed-fi/educationContents'))
    }
    finally
    {
        Disable-ProxyFault $armDef $faultId
    }
    $seconds += $result.Seconds
    $exitCodes += "$shape=$($result.ExitCode)"

    Assert-Condition $failures ($result.ExitCode -ne 0) "[$shape] the run exited non-zero (was $($result.ExitCode))"
    $versionAfter = Get-StoredLastChangeVersion $armDef $sourceName $targetName
    Assert-Condition $failures ($null -ne $versionBefore -and $versionAfter -eq $versionBefore) "[$shape] the stored last change version was not advanced ($versionBefore -> $versionAfter)"

    # The error record must locate the document without carrying it: no body, and an id that is at most the scalar
    # found or the token type ("<invalid id: Object>"), never the nested value of the stub ("unexpected").
    $records = @(Get-PublishedErrorRecords $result.Log | Where-Object { $_.ResourceUrl -match 'educationContents' -and "$($_.SourceItemIndex)" -eq '1' })
    Assert-Condition $failures ($records.Count -eq 1) "[$shape] one error record was published for the document ($($records.Count))"
    if ($records.Count -eq 1)
    {
        Assert-Condition $failures ($null -eq $records[0].Body) "[$shape] the error record carries no document body"
        Assert-Condition $failures ("$($records[0].Id)" -notmatch 'unexpected') "[$shape] the error record's id carries no document content ('$($records[0].Id)')"
    }

    # Serilog renders the locator and the problem as quoted strings, e.g.
    #   "/ed-fi/educationContents": Source item at "offset 0, limit 500", index 1 "has a null 'id'" and will not be published.
    # A named connection (the store runs) adds its change window to the locator: "offset 0, limit 500, change
    # versions 1 to 113007".
    $errorLine = Select-String -Path $result.Log -Pattern 'Source item at "?offset (\d+), limit (\d+)[^"]*"?, index 1 "?.*will not be published' | Select-Object -First 1
    Assert-Condition $failures ($null -ne $errorLine) "[$shape] one Error line names the page locator, index 1 and the shape found"
    if ($errorLine)
    {
        $problems += "$shape -> " + (($errorLine.Line -replace '^.*index 1 ', '') -replace ' and will not be published.*$', '').Trim('"')
        $offset = $errorLine.Matches[0].Groups[1].Value
        $limit = $errorLine.Matches[0].Groups[2].Value
        $served = @(Get-ProxyJournal $armDef "^$([regex]::Escape($dataPath))/ed-fi/educationContents\?.*offset=$offset&limit=$limit")
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
