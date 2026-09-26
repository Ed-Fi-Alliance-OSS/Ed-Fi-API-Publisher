# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 5: non-zero exit code and per-resource error summary on induced errors (APIPUB-120).
    The proxy answers 500 for one resource for the whole run. The run must end non-zero (1 = completed with item
    errors, 2 = processing incomplete), name the resource in its summary, and still publish everything else.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    # A leaf resource (nothing references it): a failing parent makes every dependent retry its unresolved references,
    # which turns a five-minute item into an hour and a million-line log.
    [string] $FailingResource = '/ed-fi/studentGradebookEntries'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '05'
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item exit code and error summary on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); failing resource $FailingResource; run folder $run"

Reset-RegressionTarget $armDef
Reset-ProxyMappings $armDef
Reset-ProxyJournal $armDef

$faultId = Enable-ProxyFault $armDef '500-window' -Replace @{ URL_PATTERN = "/data/v3$([regex]::Escape($FailingResource)).*" }
try
{
    $result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -SourceUrl $armDef.ProxyUrl -Arguments @('--disableCursorPaging=true', '--includeDescriptors=true', '--maxRetryAttempts=2')
}
finally
{
    Disable-ProxyFault $armDef $faultId
}

Assert-Condition $failures ($result.ExitCode -in 1, 2) "the run exited non-zero with a documented code (was $($result.ExitCode); 1 = item errors, 2 = incomplete)"
$injected = @(Get-ProxyJournal $armDef "^/data/v3$([regex]::Escape($FailingResource))" | Where-Object { $_.responseDefinition.status -eq 500 }).Count
Assert-Condition $failures ($injected -gt 0) "the source answered 500 for $FailingResource ($injected responses)"
Assert-Condition $failures (Test-LogContains $result.Log "\[(EROR|ERR|FATL)\].*$([regex]::Escape($FailingResource))") "an Error line names $FailingResource"
Assert-Condition $failures (Test-LogContains $result.Log '(?i)summary') 'the log ends with a run summary'

$others = @(Get-StreamedResources $result.Log | Where-Object { $_ -ne $FailingResource })
$counts = ''
if ($others.Count -gt 0)
{
    $comparison = Compare-Counts -Arm $armDef -Resources $others -ReportCsv (Join-Path $run 'counts.csv')
    Assert-Condition $failures ($comparison.Mismatches.Count -eq 0) "every other streamed resource still published in full ($($comparison.Mismatches.Count) mismatch(es))"
    $counts = $comparison.Summary
}
else
{
    $failures.Add('no other resource finished streaming; the induced failure stopped the whole run')
}

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts -Seconds $result.Seconds -Log $result.Log -Notes "persistent 500 on $FailingResource; exit code $($result.ExitCode)")
