# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 7: a publish from a Data Standard 5.x source with the shipped configuration completes, contacts included
    (APIPUB-132). Arm B. The shipped configuration is lib/publisher-config/apiPublisherSettings.json, a copy of the CLI
    defaults, so no option other than --includeDescriptors is passed.
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

$item = '07'
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item Data Standard 5.x source with shipped configuration on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

if ($armDef.Name -ne 'B') { Write-Warning "Item 7 is written for arm B (Data Standard 5.2.0); arm $($armDef.Name) may not have the contacts resource." }

Reset-RegressionTarget $armDef
$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -Arguments @('--includeDescriptors=true')

Assert-Condition $failures ($result.ExitCode -eq 0) "the publish exited with 0 (was $($result.ExitCode))"
$resources = Get-StreamedResources $result.Log
Assert-Condition $failures ('/ed-fi/contacts' -in $resources) 'the contacts resource was streamed'

$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "counts match on every streamed resource ($($counts.Mismatches.Count) mismatch(es))"
$contacts = $counts.Rows | Where-Object { $_.Resource -eq '/ed-fi/contacts' } | Select-Object -First 1
Assert-Condition $failures ($null -ne $contacts -and $contacts.Target -is [int] -and $contacts.Target -gt 0) "contacts reached the target ($(if ($contacts) { $contacts.Target } else { 'none' }))"

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes 'shipped configuration, cursor paging default')
