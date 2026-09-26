# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Driver for the API Publisher regression (APIPUB-125 items 1 to 12, APIPUB-146 items D1 to D5).

.DESCRIPTION
    Brings each requested arm up, runs each requested item script as a child PowerShell process, and appends one row
    per item and arm to the results file. Item scripts are self-contained (see items/README section in README.md):
    the driver is the only place that knows about arms and their ports, so the v1.5 workflow_dispatch job
    (APIPUB-149) wraps this script instead of the items.

    Exit code: 0 when every selected item passed, 1 otherwise.

.PARAMETER Arms
    Comma-separated arm names (A, B, C, D). Default: B.
.PARAMETER Items
    Comma-separated item ids (1..12, d1..d5, or 'all'). Items that do not apply to an arm are skipped with a note.
.PARAMETER PublisherPath / PublisherImage
    The publisher under test: a local EdFiApiPublisher.exe/.dll, or a Docker image tag. Items 3 and 9 need the image.
.PARAMETER ResultsFile
    Markdown file the rows are appended to. Default: results/results-<Version>.md.
.PARAMETER Version
    Label for the results file name (e.g. 1.4.0). Default: 'local'.
.PARAMETER SkipArmStart
    Assume the arms are already up (faster when iterating on one item).
.PARAMETER StopArms
    Bring the arms down after the run (volumes kept).
.PARAMETER ItemArguments
    Extra arguments forwarded to every item script, e.g. '-FaultAfterSeconds 30'. Splatted as-is.

.EXAMPLE
    .\Invoke-Regression.ps1 -Arms B,C -Items 1,2,5,6,7,10,11,12 -PublisherPath ..\..\src\EdFi.Tools.ApiPublisher.Cli\bin\Release\net10.0\EdFiApiPublisher.exe -Version 1.4.0
    .\Invoke-Regression.ps1 -Arms B -Items 3,9 -PublisherImage edfialliance/ods-api-publisher:pre -Version 1.4.0
#>
[CmdletBinding()]
param(
    [string] $Arms = 'B',
    [string] $Items = 'all',
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile,
    [string] $Version = 'local',
    [switch] $SkipArmStart,
    [switch] $StopArms,
    [string[]] $ItemArguments = @()
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/Regression.psm1') -Force

if (-not $PublisherPath -and -not $PublisherImage) { throw 'Give the publisher under test: -PublisherPath <exe|dll> or -PublisherImage <tag>.' }
if (-not $ResultsFile) { $ResultsFile = Join-Path $PSScriptRoot "results/results-$Version.md" }
$ResultsFile = [IO.Path]::GetFullPath($ResultsFile)

# Which arms each item applies to. Items without an entry run on every ODS arm.
$applicability = @{
    '03' = @('B'); '04' = @('B'); '07' = @('B'); '08' = @('B'); '09' = @('B'); '11' = @('B')
    'd1' = @('D'); 'd2' = @('D'); 'd3' = @('D'); 'd4' = @('D'); 'd5' = @('D')
}

$itemScripts = Get-ChildItem (Join-Path $PSScriptRoot 'items') -Filter '*.ps1' | Sort-Object Name
$allIds = $itemScripts | ForEach-Object { ($_.BaseName -split '-')[0].ToLowerInvariant() }

$requested = if ($Items.Trim().ToLowerInvariant() -eq 'all') { $allIds } else {
    $Items.Split(',') | ForEach-Object {
        $id = $_.Trim().ToLowerInvariant()
        if ($id -match '^\d+$') { $id = ('{0:00}' -f [int] $id) }
        if ($id -notin $allIds) { throw "Unknown item '$_'. Known items: $($allIds -join ', ')." }
        $id
    }
}

$armNames = $Arms.Split(',') | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ }
$results = New-Object System.Collections.Generic.List[object]
$publisherArguments = if ($PublisherPath) { @('-PublisherPath', $PublisherPath) } else { @('-PublisherImage', $PublisherImage) }

Write-Host "Regression run: arms $($armNames -join ', '); items $($requested -join ', '); results -> $ResultsFile"

foreach ($armName in $armNames)
{
    $arm = Get-Arm $armName

    if (-not $SkipArmStart) { Start-RegressionArm $arm }

    foreach ($id in $requested)
    {
        $script = $itemScripts | Where-Object { ($_.BaseName -split '-')[0].ToLowerInvariant() -eq $id } | Select-Object -First 1
        $allowed = if ($applicability.ContainsKey($id)) { $applicability[$id] } elseif ($arm.Type -eq 'dms') { @() } else { @('A', 'B', 'C') }

        if ($arm.Name -notin $allowed)
        {
            Write-Host "SKIP  item $id does not apply to arm $($arm.Name)." -ForegroundColor DarkGray
            $results.Add([pscustomobject]@{ Arm = $arm.Name; Item = $id; Result = 'SKIP'; Seconds = 0 })
            continue
        }

        Write-Host ''
        Write-Host "===== item $id ($($script.BaseName)) on arm $($arm.Name) =====" -ForegroundColor Cyan
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()

        # Each item runs in its own process: a failure (or a kill trigger) in one item cannot take the driver down,
        # and the exit code is the same one the CI wrapper will read.
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script.FullName, '-Arm', $arm.Name, '-ResultsFile', $ResultsFile) + $publisherArguments + $ItemArguments
        & pwsh @arguments
        $exitCode = $LASTEXITCODE
        $stopwatch.Stop()

        $results.Add([pscustomobject]@{ Arm = $arm.Name; Item = $id; Result = ($(if ($exitCode -eq 0) { 'PASS' } else { "FAIL ($exitCode)" })); Seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds) })
    }

    if ($StopArms) { Stop-RegressionArm $arm }
}

Write-Host ''
Write-Host '===== summary =====' -ForegroundColor Cyan
$results | Format-Table Arm, Item, Result, @{ Name = 'Duration'; Expression = { Format-Duration $_.Seconds } } -AutoSize | Out-String | Write-Host
Write-Host "Results file: $ResultsFile"

$failed = @($results | Where-Object { $_.Result -like 'FAIL*' }).Count
if ($failed -gt 0) { Write-Host "$failed item run(s) failed." -ForegroundColor Red; exit 1 }

Write-Host 'Every selected item passed.' -ForegroundColor Green
exit 0
