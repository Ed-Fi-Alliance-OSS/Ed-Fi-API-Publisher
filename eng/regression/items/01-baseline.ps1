# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 1: baseline full and incremental publish with offset/limit paging (APIPUB-125).
    Leg 1 publishes the whole source into an empty target and compares Total-Count per resource.
    Leg 2 changes one student on the source and publishes from the change version recorded before the change;
    the target must carry the change afterwards, the counts must still match, and the run summary must show that only
    the changed documents were attempted (at most -MaxIncrementalDocuments).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [int] $MaxIncrementalDocuments = 10
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '01'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
$seconds = 0.0
Write-Host "Item $item baseline publish on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

Reset-RegressionTarget $armDef

# Leg 1: full publish with offset/limit paging, the mode every arm supports.
$full = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'full.log' -Arguments @('--disableCursorPaging=true', '--includeDescriptors=true')
$seconds += $full.Seconds
Assert-Condition $failures ($full.ExitCode -eq 0) "full publish exited with 0 (was $($full.ExitCode))"

$counts = Compare-Counts -Arm $armDef -Log $full.Log -ReportCsv (Join-Path $run 'full-counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "every streamed resource has the same Total-Count on source and target after the full publish ($($counts.Mismatches.Count) mismatch(es))"
Assert-Condition $failures ($counts.TargetItems -gt 0) "the target received items ($($counts.TargetItems))"

# Leg 2: incremental publish from the change version recorded before one student is edited on the source.
$sourceToken = Get-BearerToken $armDef.SourceUrl $armDef.SourceKey $armDef.SourceSecret
$before = Get-NewestChangeVersion $armDef.SourceUrl $sourceToken
$notes = 'offset/limit; full publish'

if ($null -eq $before)
{
    $failures.Add('the source does not expose change queries (no changeQueries URL in Discovery); the incremental leg needs Change Queries on')
}
else
{
    $student = @(Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource '/ed-fi/students' -Query '?limit=1')[0]
    foreach ($name in '_etag', '_lastModifiedDate') { if ($student.PSObject.Properties[$name]) { $student.PSObject.Properties.Remove($name) } }
    $marker = 'Reg' + (Get-Date -Format 'HHmmss')
    $student | Add-Member -NotePropertyName 'middleName' -NotePropertyValue $marker -Force   # absent on the JSON object when null
    $put = Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource "/ed-fi/students/$($student.id)" -Method PUT -Body $student
    Assert-Condition $failures ([int] $put.StatusCode -in 200, 204) "source student $($student.studentUniqueId) updated with middleName '$marker' (HTTP $($put.StatusCode))"

    $incremental = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'incremental.log' -Arguments @('--disableCursorPaging=true', "--lastChangeVersionProcessed=$before")
    $seconds += $incremental.Seconds
    Assert-Condition $failures ($incremental.ExitCode -eq 0) "incremental publish from change version $before exited with 0 (was $($incremental.ExitCode))"
    # The window must limit what is read: an incremental publish that ignored it would republish the whole source,
    # and every check below would still hold (the first arm B runs of 2026-09-25 did exactly that, 108,285 documents).
    $total = Get-RunSummaryTotal $incremental.Log
    Assert-Condition $failures ($null -ne $total -and $total.Attempted -ge 1 -and $total.Attempted -le $MaxIncrementalDocuments) "the incremental publish attempted only the changed documents ($(if ($total) { $total.Attempted } else { 'no run summary' }); at most $MaxIncrementalDocuments expected for one edited student)"

    $targetToken = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret
    $published = @(Invoke-Api -BaseUrl $armDef.TargetUrl -Token $targetToken -Resource '/ed-fi/students' -Query "?studentUniqueId=$($student.studentUniqueId)")
    Assert-Condition $failures ($published.Count -eq 1 -and $published[0].middleName -eq $marker) "target student $($student.studentUniqueId) carries middleName '$marker' after the incremental publish"

    $after = Compare-Counts -Arm $armDef -Resources $counts.Rows.Resource -ReportCsv (Join-Path $run 'incremental-counts.csv')
    Assert-Condition $failures ($after.Mismatches.Count -eq 0) "counts still match after the incremental publish ($($after.Mismatches.Count) mismatch(es))"
    $notes = "offset/limit; full publish then incremental from change version $before"
}

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $seconds -Log $full.Log -Notes $notes)
