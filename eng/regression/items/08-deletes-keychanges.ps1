# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 8: deletes and key changes, one scripted smoke per path (APIPUB-113; the unit tests carry the rest).
    The item first creates two calendar dates and two class periods of its own on the source (copies of existing ones),
    so it never consumes the template's data and can run again. After a full publish into an empty target, two rounds
    of source edits each delete one of those calendar dates and re-key one of those class periods:
      round 1  a FULL publish (change window from version 1) with --processDeletesAndKeyChangesOnFullPublish=true, the
               v1.4 behavior of APIPUB-113. Without the flag a full publish skips delete and key change processing, so
               the calendar date would stay on the target and the old key would remain next to the new one.
      round 2  an incremental publish from the change version recorded before the round's edits (the v1.3 path).
    Each round must remove its calendar date on the target and leave the class period only under its new key.
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
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
$seconds = 0.0
Write-Host "Item $item deletes and key changes on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$sourceToken = Get-BearerToken $armDef.SourceUrl $armDef.SourceKey $armDef.SourceSecret
$targetToken = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret
if ($null -eq (Get-NewestChangeVersion $armDef.SourceUrl $sourceToken)) { $failures.Add('the source does not expose change queries; deletes and key changes need Change Queries on'); exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Seconds $seconds) }

function New-SourceCopy
{
    # POSTs a copy of $Template to the source with the changes $Change makes and returns the new document's id (from
    # the Location header). The item edits only documents it created, so the template's own data stays intact and the
    # item can run again: Grand Bend has two calendar dates, and one run would otherwise delete both.
    param([string] $Resource, $Template, [scriptblock] $Change)

    $copy = $Template | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    foreach ($name in 'id', '_etag', '_lastModifiedDate') { if ($copy.PSObject.Properties[$name]) { $copy.PSObject.Properties.Remove($name) } }
    & $Change $copy
    $response = Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource $Resource -Method POST -Body $copy
    $location = "$($response.Headers['Location'] | Select-Object -First 1)"

    return [pscustomobject]@{ Id = ($location -split '/')[-1]; Body = $copy; Status = [int] $response.StatusCode }
}

# Two calendar dates (far-future dates, unique per run) and two class periods, one of each per round. Calendar dates
# are what nothing references; class periods allow identity updates and are in resourcesWithUpdatableKeys.
$stamp = Get-Date -Format 'MMddHHmmss'
$dateTemplate = @(Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource '/ed-fi/calendarDates' -Query '?limit=1') | Select-Object -First 1
$periodTemplate = @(Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource '/ed-fi/classPeriods' -Query '?limit=1') | Select-Object -First 1
if (-not $dateTemplate -or -not $periodTemplate) { $failures.Add('the source has no calendar date or class period to copy (Start-Arm.ps1 -ResetSource restores the template)'); exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures) }
$futureDay = [datetime]::new(2090, 1, 1).AddDays([int] ((Get-Date) - [datetime]::new(2026, 1, 1)).TotalMinutes % 36000)
$created = foreach ($round in 1, 2)
{
    $date = New-SourceCopy '/ed-fi/calendarDates' $dateTemplate { param($c) $c.date = $futureDay.AddDays($round).ToString('yyyy-MM-dd') }
    $period = New-SourceCopy '/ed-fi/classPeriods' $periodTemplate { param($c) $c.classPeriodName = "Regression KC $round $stamp" }
    Assert-Condition $failures ($date.Id -and $period.Id) "round ${Round}: calendar date $($date.Body.date) and class period '$($period.Body.classPeriodName)' created on the source (HTTP $($date.Status), $($period.Status))"
    [pscustomobject]@{ Date = $date; Period = $period }
}

Reset-RegressionTarget $armDef
$initial = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'initial.log' -Arguments @('--disableCursorPaging=true', '--includeDescriptors=true')
$seconds += $initial.Seconds
Assert-Condition $failures ($initial.ExitCode -eq 0) "initial full publish exited with 0 (was $($initial.ExitCode))"

function Invoke-SourceEdit
{
    # Deletes this round's calendar date and renames this round's class period on the source.
    param([int] $Round, $Documents)

    $edits = [pscustomobject]@{ CalendarDatesBefore = (Get-ResourceCount $armDef.TargetUrl $targetToken '/ed-fi/calendarDates'); OldName = $Documents.Period.Body.classPeriodName; NewName = "$($Documents.Period.Body.classPeriodName) renamed"; SchoolId = $Documents.Period.Body.schoolReference.schoolId }

    $delete = Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource "/ed-fi/calendarDates/$($Documents.Date.Id)" -Method DELETE
    Assert-Condition $failures ([int] $delete.StatusCode -in 200, 204) "round ${Round}: calendar date $($Documents.Date.Body.date) deleted on the source (HTTP $($delete.StatusCode))"

    $classPeriod = $Documents.Period.Body | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $classPeriod.classPeriodName = $edits.NewName
    $put = Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource "/ed-fi/classPeriods/$($Documents.Period.Id)" -Method PUT -Body $classPeriod
    Assert-Condition $failures ([int] $put.StatusCode -in 200, 204) "round ${Round}: class period '$($edits.OldName)' re-keyed to '$($edits.NewName)' on the source (HTTP $($put.StatusCode))"

    return $edits
}

function Assert-TargetEdit
{
    param([int] $Round, [string] $Leg, $Edits)

    $after = Get-ResourceCount $armDef.TargetUrl $targetToken '/ed-fi/calendarDates'
    Assert-Condition $failures ($after -is [int] -and $Edits.CalendarDatesBefore -is [int] -and $after -eq ($Edits.CalendarDatesBefore - 1)) "round ${Round} (${Leg}): the target lost exactly one calendar date ($($Edits.CalendarDatesBefore) -> $after)"

    $renamed = @(Invoke-Api -BaseUrl $armDef.TargetUrl -Token $targetToken -Resource '/ed-fi/classPeriods' -Query "?classPeriodName=$([uri]::EscapeDataString($Edits.NewName))&schoolId=$($Edits.SchoolId)")
    $stale = @(Invoke-Api -BaseUrl $armDef.TargetUrl -Token $targetToken -Resource '/ed-fi/classPeriods' -Query "?classPeriodName=$([uri]::EscapeDataString($Edits.OldName))&schoolId=$($Edits.SchoolId)")
    Assert-Condition $failures ($renamed.Count -eq 1) "round ${Round} (${Leg}): the target has the class period under its new key '$($Edits.NewName)'"
    Assert-Condition $failures ($stale.Count -eq 0) "round ${Round} (${Leg}): the target no longer has the class period under its old key '$($Edits.OldName)'"

    return "calendarDates $($Edits.CalendarDatesBefore) -> $after"
}

# Round 1: full publish with deletes and key changes (APIPUB-113). --lastChangeVersionProcessed=0 makes the change
# window start at 1 whatever the configuration store recorded, which is what the publisher treats as a full publish.
$round1 = Invoke-SourceEdit -Round 1 -Documents $created[0]
$fullWithFlag = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'full-with-deletes.log' `
    -Arguments @('--disableCursorPaging=true', '--includeDescriptors=true', '--lastChangeVersionProcessed=0', '--processDeletesAndKeyChangesOnFullPublish=true')
$seconds += $fullWithFlag.Seconds
Assert-Condition $failures ($fullWithFlag.ExitCode -eq 0) "round 1: full publish with --processDeletesAndKeyChangesOnFullPublish exited with 0 (was $($fullWithFlag.ExitCode))"
Assert-Condition $failures (-not (Test-LogContains $fullWithFlag.Log 'all values are being published, and so there is no need to perform (delete|key change) processing')) 'round 1: the full publish did not skip delete and key change processing'
$round1Counts = Assert-TargetEdit -Round 1 -Leg 'full publish' -Edits $round1

# Round 2: incremental publish from the change version recorded before this round's edits.
$before = Get-NewestChangeVersion $armDef.SourceUrl $sourceToken
$round2 = Invoke-SourceEdit -Round 2 -Documents $created[1]
$incremental = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'incremental.log' -Arguments @('--disableCursorPaging=true', "--lastChangeVersionProcessed=$before")
$seconds += $incremental.Seconds
Assert-Condition $failures ($incremental.ExitCode -eq 0) "round 2: incremental publish from change version $before exited with 0 (was $($incremental.ExitCode))"
$round2Counts = Assert-TargetEdit -Round 2 -Leg 'incremental' -Edits $round2

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts "full: $round1Counts; incremental: $round2Counts; two class periods re-keyed" -Seconds $seconds -Log $fullWithFlag.Log -Notes "round 1 full publish with --processDeletesAndKeyChangesOnFullPublish; round 2 incremental from change version $before")
