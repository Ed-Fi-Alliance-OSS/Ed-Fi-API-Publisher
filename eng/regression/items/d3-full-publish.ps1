# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item D3: full publish from an ODS/API source into the DMS with matching counts; errors in the summary and the exit
    code (APIPUB-146). Grand Bend from arm B by default; point arm D's SOURCE_* at Northridge for the large run.
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

$item = 'D3'
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item full publish into the DMS on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$targetToken = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret
$studentsBefore = Get-ResourceCount $armDef.TargetUrl $targetToken '/ed-fi/students'
Write-Host "  DMS students before the run: $studentsBefore"

$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -Arguments @('--includeDescriptors=true') -RunFolder $run
Assert-Condition $failures ($result.ExitCode -eq 0) "the publish exited with 0 (was $($result.ExitCode))"
Assert-Condition $failures (Test-LogContains $result.Log '(?i)summary') 'the log ends with a run summary'

$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "every streamed resource has the same count on the ODS source and the DMS ($($counts.Mismatches.Count) mismatch(es))"
Assert-Condition $failures ($counts.TargetItems -gt 0) "the DMS received items ($($counts.TargetItems))"

$errors = Get-LogMatchCount $result.Log '\[(EROR|ERR)\]'
exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes "DMS students before $studentsBefore; $errors error line(s) in the log")
