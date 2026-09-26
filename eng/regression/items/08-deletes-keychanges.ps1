# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 8: deletes and key changes, one scripted smoke (APIPUB-113; the unit tests carry the rest).
    After a full publish, one calendar date is deleted on the source and one class period is re-keyed; an incremental
    publish from the change version recorded before the edits must remove the calendar date on the target and rename
    the class period there.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '08'
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
$seconds = 0.0
Write-Host "Item $item deletes and key changes on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

Reset-RegressionTarget $armDef
$full = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'full.log' -Arguments @('--disableCursorPaging=true', '--includeDescriptors=true')
$seconds += $full.Seconds
Assert-Condition $failures ($full.ExitCode -eq 0) "full publish exited with 0 (was $($full.ExitCode))"

$sourceToken = Get-BearerToken $armDef.SourceUrl $armDef.SourceKey $armDef.SourceSecret
$targetToken = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret
$before = Get-NewestChangeVersion $armDef.SourceUrl $sourceToken
if ($null -eq $before) { $failures.Add('the source does not expose change queries; deletes and key changes need Change Queries on'); exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Seconds $seconds -Log $full.Log) }

# Delete: the first calendar date the source will let go of (nothing references calendar dates).
$calendarDatesBefore = Get-ResourceCount $armDef.TargetUrl $targetToken '/ed-fi/calendarDates'
$deleted = $null
foreach ($candidate in @(Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource '/ed-fi/calendarDates' -Query '?limit=10'))
{
    try
    {
        $response = Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource "/ed-fi/calendarDates/$($candidate.id)" -Method DELETE
        if ([int] $response.StatusCode -in 200, 204) { $deleted = $candidate; break }
    }
    catch { Write-Host "  calendar date $($candidate.id) could not be deleted ($($_.Exception.Message)); trying the next one" }
}
Assert-Condition $failures ($null -ne $deleted) 'one calendar date was deleted on the source'

# Key change: rename one class period (classPeriods allow identity updates and are in resourcesWithUpdatableKeys).
$classPeriod = @(Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource '/ed-fi/classPeriods' -Query '?limit=1')[0]
foreach ($name in '_etag', '_lastModifiedDate') { if ($classPeriod.PSObject.Properties[$name]) { $classPeriod.PSObject.Properties.Remove($name) } }
$oldName = $classPeriod.classPeriodName
$newName = "$oldName-KC$(Get-Date -Format 'HHmm')"
$classPeriod.classPeriodName = $newName
$put = Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource "/ed-fi/classPeriods/$($classPeriod.id)" -Method PUT -Body $classPeriod
Assert-Condition $failures ([int] $put.StatusCode -in 200, 204) "class period '$oldName' re-keyed to '$newName' on the source (HTTP $($put.StatusCode))"
$schoolId = $classPeriod.schoolReference.schoolId

$incremental = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'incremental.log' -Arguments @('--disableCursorPaging=true', "--lastChangeVersionProcessed=$before")
$seconds += $incremental.Seconds
Assert-Condition $failures ($incremental.ExitCode -eq 0) "incremental publish from change version $before exited with 0 (was $($incremental.ExitCode))"

$calendarDatesAfter = Get-ResourceCount $armDef.TargetUrl $targetToken '/ed-fi/calendarDates'
Assert-Condition $failures ($calendarDatesAfter -is [int] -and $calendarDatesAfter -eq ($calendarDatesBefore - 1)) "the target lost exactly one calendar date ($calendarDatesBefore -> $calendarDatesAfter)"

$renamed = @(Invoke-Api -BaseUrl $armDef.TargetUrl -Token $targetToken -Resource '/ed-fi/classPeriods' -Query "?classPeriodName=$([uri]::EscapeDataString($newName))&schoolId=$schoolId")
$stale = @(Invoke-Api -BaseUrl $armDef.TargetUrl -Token $targetToken -Resource '/ed-fi/classPeriods' -Query "?classPeriodName=$([uri]::EscapeDataString($oldName))&schoolId=$schoolId")
Assert-Condition $failures ($renamed.Count -eq 1) "the target has the class period under its new key '$newName'"
Assert-Condition $failures ($stale.Count -eq 0) "the target no longer has the class period under its old key '$oldName'"

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts "calendarDates $calendarDatesBefore -> $calendarDatesAfter; classPeriod re-keyed" -Seconds $seconds -Log $incremental.Log -Notes "incremental from change version $before")
