# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 2: cursor paging parity on ODS/API 7.3+ and the offset/limit fallback on 7.1 (APIPUB-131, APIPUB-141).
    Reuses eng/Compare-PagingParity.ps1: two runs into SQLite targets (cursor on, then off) and a diff of the
    published ids per resource. On arm A the cursor run must fall back with the documented log entry.
    Needs a local build (-PublisherPath); the parity script reads the SQLite files with the publisher's own assemblies.
    The parity script runs the publisher itself, so --ignoreIsolation=true is passed here (a SQLite target without it
    fails with a NullReferenceException, the side bug recorded on APIPUB-141).
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

$item = '02'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item cursor paging parity on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

if ($publisher.Mode -ne 'exe')
{
    $failures.Add('item 2 reuses eng/Compare-PagingParity.ps1, which needs a local build (-PublisherPath); container runs are not supported')
    exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures)
}

$parityScript = Join-Path (Get-RegressionRoot) '../Compare-PagingParity.ps1'
$outputFolder = Join-Path $run 'parity'
$consoleLog = Join-Path $run 'parity-console.log'

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
# The parity script reports a mismatch with Write-Error, which is terminating here ($ErrorActionPreference = 'Stop');
# it is caught so the assertions below still run against the console log and the report.
$parityError = $null
try
{
    & $parityScript -PublisherPath $publisher.Path -SourceUrl $armDef.SourceUrl -SourceKey $armDef.SourceKey -SourceSecret $armDef.SourceSecret -OutputFolder $outputFolder -ExtraArgs '--ignoreIsolation=true', '--includeDescriptors=true' *>&1 | Tee-Object -FilePath $consoleLog
}
catch
{
    $parityError = $_.Exception.Message
    Add-Content -Path $consoleLog -Value "[regression harness] Compare-PagingParity.ps1 stopped: $parityError"
}
$stopwatch.Stop()
Assert-Condition $failures ($null -eq $parityError) "Compare-PagingParity.ps1 finished without an error$(if ($parityError) { " ($parityError)" })"

# The parity script exits 1 on a mismatch but ends without an explicit exit on success, so $LASTEXITCODE is not a
# reliable verdict; its console output is.
$parityOk = (Test-LogContains $consoleLog 'PARITY OK') -and -not (Test-LogContains $consoleLog 'differ between cursor and offset|Parity was NOT verified')
Assert-Condition $failures $parityOk 'Compare-PagingParity.ps1 reported PARITY OK'
Assert-Condition $failures (-not (Test-LogContains $consoleLog 'Publisher exited with')) 'both parity runs exited with 0'

$cursorLog = Join-Path $outputFolder 'cursor.log'
if ($armDef.Name -eq 'A')
{
    Assert-Condition $failures (Test-LogContains $cursorLog 'does not expose .*partitions.*offset/limit paging will be used') 'arm A: the cursor run fell back to offset/limit with the documented log entry'
    Assert-Condition $failures (-not (Test-LogContains $cursorLog 'using Cursor paging')) 'arm A: no resource used cursor paging'
}
else
{
    # Every resource, not just one: the resolver logs one "using <strategy> paging" line per resource, and on a 7.3
    # source only the /deletes and /keyChanges streams (not part of a full publish) are offset-paged by design.
    $strategies = @(Select-String -Path $cursorLog -Pattern '"(/[^"]+)": using (\w+) paging' | ForEach-Object { [pscustomobject]@{ Resource = $_.Matches[0].Groups[1].Value; Strategy = $_.Matches[0].Groups[2].Value } })
    $offsetPaged = @($strategies | Where-Object { $_.Strategy -ne 'Cursor' -and $_.Resource -notmatch '/(deletes|keyChanges)$' } | ForEach-Object { $_.Resource })
    Assert-Condition $failures ($strategies.Count -gt 0 -and $offsetPaged.Count -eq 0) "every resource of the cursor run used cursor paging ($($strategies.Count) resources, $($offsetPaged.Count) not: $($offsetPaged | Select-Object -First 3))"
    Assert-Condition $failures (-not (Test-LogContains $cursorLog 'offset/limit paging will be used')) 'no fallback to offset/limit on a 7.3 source'
}

$counts = ''
$reportFile = Join-Path $outputFolder 'parity-report.csv'
if (Test-Path $reportFile)
{
    $report = Import-Csv $reportFile
    $cursorRows = ($report | Measure-Object -Property CursorRows -Sum).Sum
    $offsetRows = ($report | Measure-Object -Property OffsetRows -Sum).Sum
    $counts = "$($report.Count) resources, cursor $cursorRows rows, offset $offsetRows rows"
}

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts -Seconds $stopwatch.Elapsed.TotalSeconds -Log $consoleLog -Notes "SQLite targets under $outputFolder")
