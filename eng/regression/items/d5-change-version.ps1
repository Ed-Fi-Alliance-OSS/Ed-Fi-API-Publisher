# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item D5: change-version (incremental) publish into the DMS, or the documented limitation (APIPUB-146).
    One student is edited on the ODS source after a full publish; an incremental publish from the change version
    recorded before the edit must land the change in the DMS. If the DMS rejects something the incremental path
    needs, the failure text is what goes into the release notes as the limitation.
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

$item = 'D5'
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
$seconds = 0.0
Write-Host "Item $item change-version publish into the DMS on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$full = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'full.log' -Arguments @('--includeDescriptors=true')
$seconds += $full.Seconds
Assert-Condition $failures ($full.ExitCode -eq 0) "the full publish exited with 0 (was $($full.ExitCode))"

$sourceToken = Get-BearerToken $armDef.SourceUrl $armDef.SourceKey $armDef.SourceSecret
$before = Get-NewestChangeVersion $armDef.SourceUrl $sourceToken
if ($null -eq $before) { $failures.Add('the ODS source does not expose change queries'); exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Seconds $seconds -Log $full.Log) }

$student = @(Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource '/ed-fi/students' -Query '?limit=1')[0]
foreach ($name in '_etag', '_lastModifiedDate') { if ($student.PSObject.Properties[$name]) { $student.PSObject.Properties.Remove($name) } }
$marker = 'Dms' + (Get-Date -Format 'HHmmss')
$student | Add-Member -NotePropertyName 'middleName' -NotePropertyValue $marker -Force   # absent on the JSON object when null
$put = Invoke-Api -BaseUrl $armDef.SourceUrl -Token $sourceToken -Resource "/ed-fi/students/$($student.id)" -Method PUT -Body $student
Assert-Condition $failures ([int] $put.StatusCode -in 200, 204) "source student $($student.studentUniqueId) updated with middleName '$marker' (HTTP $($put.StatusCode))"

$incremental = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -LogName 'incremental.log' -Arguments @("--lastChangeVersionProcessed=$before")
$seconds += $incremental.Seconds
Assert-Condition $failures ($incremental.ExitCode -eq 0) "the incremental publish from change version $before exited with 0 (was $($incremental.ExitCode))"

$targetToken = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret
$published = @(Invoke-Api -BaseUrl $armDef.TargetUrl -Token $targetToken -Resource '/ed-fi/students' -Query "?studentUniqueId=$($student.studentUniqueId)")
Assert-Condition $failures ($published.Count -eq 1 -and $published[0].middleName -eq $marker) "the DMS student $($student.studentUniqueId) carries middleName '$marker' after the incremental publish"

$limitation = if ($failures.Count -gt 0) { 'record the failing step as the documented limitation for the release notes' } else { 'incremental publish into the DMS works' }
exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts "incremental from change version $before" -Seconds $seconds -Log $incremental.Log -Notes $limitation)
