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
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item full publish into the DMS on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

# The counts below only say something about this run if the DMS starts empty: earlier D items publish the same
# source, and matching counts on their data would pass without this run having published anything.
Reset-RegressionTarget $armDef
$sourceToken = Get-BearerToken $armDef.SourceUrl $armDef.SourceKey $armDef.SourceSecret
$targetToken = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret
$resources = Get-ApiResources $armDef.SourceUrl $sourceToken -ExcludeDescriptors
$notEmpty = @(foreach ($resource in $resources)
{
    $count = Get-ResourceCount $armDef.TargetUrl $targetToken $resource
    if ($count -is [int] -and $count -gt 0) { "$resource ($count)" }
})
$notEmpty | Set-Content (Join-Path $run 'target-before.txt')
Assert-Condition $failures ($notEmpty.Count -eq 0) "the DMS target started empty for the $($resources.Count) non-descriptor source resources ($($notEmpty.Count) not empty, first: $($notEmpty | Select-Object -First 3); set TARGET_RESET_COMMAND in arm-d.env, see arm-d-dms.md)"

$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -Arguments @('--includeDescriptors=true') -RunFolder $run
Assert-Condition $failures ($result.ExitCode -eq 0) "the publish exited with 0 (was $($result.ExitCode))"
Assert-Condition $failures (Test-LogContains $result.Log 'Publishing run summary') 'the log ends with the run summary'

$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "every streamed resource has the same count on the ODS source and the DMS ($($counts.Mismatches.Count) mismatch(es))"
Assert-Condition $failures ($counts.TargetItems -gt 0) "the DMS received items ($($counts.TargetItems))"

$errors = Get-LogMatchCount $result.Log '\[(EROR|ERR)\]'
exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes "DMS target empty before the run for $($resources.Count - $notEmpty.Count) of $($resources.Count) resources; $errors error line(s) in the log")
