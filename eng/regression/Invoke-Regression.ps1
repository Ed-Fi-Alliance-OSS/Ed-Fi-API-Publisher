# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Driver for the API Publisher regression (APIPUB-125 items 1 to 13, APIPUB-146 items D1 to D5).

.DESCRIPTION
    Brings each requested arm up, runs each requested item script as a child PowerShell process, and appends one row
    per item and arm to the results file. Item scripts are self-contained (see items/README section in README.md):
    the driver is the only place that knows about arms and their ports, so the v1.5 workflow_dispatch job
    (APIPUB-149) wraps this script instead of the items.

    Exit code: 0 when every selected item passed, 1 when one failed or when no selected item applied to any arm.

.PARAMETER Arms
    Comma-separated arm names (A, B, C, D). Default: B.
.PARAMETER Items
    Comma-separated item ids (1..13, d1..d5, or 'all'). Items that do not apply to an arm, or that need the other kind
    of publisher (item 2 a local build, items 3 and 9 an image), are skipped with the reason.
.PARAMETER PublisherPath / PublisherImage / PublisherPackage
    The publisher under test: a local EdFiApiPublisher.exe/.dll, a Docker image tag, or the release candidate nupkg
    (extracted under results/runs/.packages and run as a local build). Every result row names it (image digest, or
    product version and package).
.PARAMETER ResultsFile
    Markdown file the rows are appended to. Default: results/results-<Version>.md.
.PARAMETER Version
    Label for the results file name (e.g. 1.4.0). Default: 'local'.
.PARAMETER SkipArmStart
    Assume the arms are already up (faster when iterating on one item).
.PARAMETER StopArms
    Bring the arms down after the run (volumes kept).
.PARAMETER ItemArguments
    Extra arguments for the item scripts, e.g. '-FaultAfterSeconds', '30'. Each -Name (with its values) goes only to
    the items whose parameter block declares it; the others run without it and the driver says so.

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
    [string] $PublisherPackage,
    [string] $ResultsFile,
    [string] $Version = 'local',
    [switch] $SkipArmStart,
    [switch] $StopArms,
    [string[]] $ItemArguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/Regression.psm1') -Force

if (@($PublisherPath, $PublisherImage, $PublisherPackage | Where-Object { $_ }).Count -ne 1) { throw 'Give exactly one publisher under test: -PublisherPath <exe|dll>, -PublisherImage <tag> or -PublisherPackage <nupkg>.' }
if ($PublisherPackage) { $PublisherPath = Expand-PublisherPackage -Package $PublisherPackage }
if (-not $ResultsFile) { $ResultsFile = Join-Path $PSScriptRoot "results/results-$Version.md" }
$ResultsFile = [IO.Path]::GetFullPath($ResultsFile)
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage

# Which arms each item applies to. Items without an entry run on every ODS arm (A, B, C and any arm added later);
# the D items only on DMS arms.
$applicability = @{
    '03' = @('B'); '04' = @('B'); '07' = @('B'); '08' = @('B'); '09' = @('B'); '11' = @('B')
}
$dmsItems = @('d1', 'd2', 'd3', 'd4', 'd5')

# Items that need one kind of publisher: a mismatch is a SKIP with the reason, not a FAIL.
$requiredMode = @{
    '02' = @{ Mode = 'exe'; Reason = 'reuses eng/Compare-PagingParity.ps1, which needs a local build (-PublisherPath or -PublisherPackage)' }
    '03' = @{ Mode = 'docker'; Reason = 'needs the publisher as a container (-PublisherImage) for the memory limit' }
    '09' = @{ Mode = 'docker'; Reason = 'compares container runs against the v1.3.0 image (-PublisherImage)' }
}

# -ItemArguments as (name, tokens) pairs, so each item receives only the parameters it declares. Called through
# 'pwsh -File' the array arrives as one string, so every element is split on blanks and commas (values cannot
# contain either).
$argumentPairs = New-Object System.Collections.Generic.List[object]
$itemTokens = @($ItemArguments | ForEach-Object { $_ -split '[\s,]+' } | ForEach-Object { $_.Trim([char[]] "'`"") } | Where-Object { $_ })
foreach ($token in $itemTokens)
{
    if ($token -match '^-[A-Za-z]\w*$')
    {
        $argumentPairs.Add([pscustomobject]@{ Name = $token.Substring(1); Tokens = [System.Collections.Generic.List[string]] @($token) })
    }
    elseif ($argumentPairs.Count -gt 0) { $argumentPairs[$argumentPairs.Count - 1].Tokens.Add($token) }
    else { throw "-ItemArguments must start with a parameter name (e.g. '-FaultAfterSeconds', '30'), not '$token'." }
}

# The results file names the release candidate once, in the line its template leaves for it.
$rcPlaceholder = '^Release candidate under test: _to be filled in.*$'
if ((Test-Path $ResultsFile) -and ((Get-Content $ResultsFile) -match $rcPlaceholder))
{
    $rcLine = "Release candidate under test: $($publisher.Identity) (recorded $(Get-Date -Format 'yyyy-MM-dd'); each row names the publisher it ran)."
    (Get-Content $ResultsFile) -replace $rcPlaceholder, $rcLine | Set-Content $ResultsFile
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

Write-Host "Regression run: arms $($armNames -join ', '); items $($requested -join ', '); publisher $($publisher.Identity); results -> $ResultsFile"

foreach ($armName in $armNames)
{
    $arm = Get-Arm $armName

    if (-not $SkipArmStart) { Start-RegressionArm $arm }

    foreach ($id in $requested)
    {
        $script = $itemScripts | Where-Object { ($_.BaseName -split '-')[0].ToLowerInvariant() -eq $id } | Select-Object -First 1
        $applies = if ($id -in $dmsItems) { $arm.Type -eq 'dms' } elseif ($arm.Type -eq 'dms') { $false } elseif ($applicability.ContainsKey($id)) { $arm.Name -in $applicability[$id] } else { $true }

        if (-not $applies)
        {
            Write-Host "SKIP  item $id does not apply to arm $($arm.Name)." -ForegroundColor DarkGray
            $results.Add([pscustomobject]@{ Arm = $arm.Name; Item = $id; Result = 'SKIP'; Seconds = 0 })
            continue
        }

        if ($requiredMode.ContainsKey($id) -and $requiredMode[$id].Mode -ne $publisher.Mode)
        {
            Write-Host "SKIP  item $id $($requiredMode[$id].Reason)." -ForegroundColor DarkGray
            $results.Add([pscustomobject]@{ Arm = $arm.Name; Item = $id; Result = 'SKIP (publisher)'; Seconds = 0 })
            continue
        }

        $declared = @((Get-Command $script.FullName).Parameters.Keys)
        $forwarded = @($argumentPairs | Where-Object { $_.Name -in $declared } | ForEach-Object { $_.Tokens })
        $dropped = @($argumentPairs | Where-Object { $_.Name -notin $declared } | ForEach-Object { "-$($_.Name)" })
        if ($dropped.Count -gt 0) { Write-Host "  item $id does not take $($dropped -join ', '); running it without." -ForegroundColor DarkGray }

        Write-Host ''
        Write-Host "===== item $id ($($script.BaseName)) on arm $($arm.Name) =====" -ForegroundColor Cyan
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()

        # Each item runs in its own process: a failure (or a kill trigger) in one item cannot take the driver down,
        # and the exit code is the same one the CI wrapper will read.
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script.FullName, '-Arm', $arm.Name, '-ResultsFile', $ResultsFile) + $publisherArguments + $forwarded
        $rowsBefore = if (Test-Path $ResultsFile) { @(Get-Content $ResultsFile).Count } else { 0 }
        & pwsh @arguments
        $exitCode = $LASTEXITCODE
        $stopwatch.Stop()

        # Items write their own row, also when they throw (their trap); one that could not even start, on a parse
        # error for example, is recorded here so it does not drop out of the results file.
        $rowsAfter = if (Test-Path $ResultsFile) { @(Get-Content $ResultsFile).Count } else { 0 }
        if ($rowsAfter -eq $rowsBefore)
        {
            Write-ResultRow -ResultsFile $ResultsFile -Item $id -ArmName $arm.Name -Passed $false -Seconds $stopwatch.Elapsed.TotalSeconds -Notes "item script exited with $exitCode without writing a result row; see the console output"
            if ($exitCode -eq 0) { $exitCode = 1 }
        }

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

# A run in which nothing applied (a new arm missing from the applicability table, or the wrong kind of publisher for
# every selected item) has proved nothing, so it does not pass.
if (@($results | Where-Object { $_.Result -eq 'PASS' }).Count -eq 0) { Write-Host 'No selected item applied to the selected arms and publisher; nothing ran.' -ForegroundColor Red; exit 1 }

Write-Host 'Every selected item passed.' -ForegroundColor Green
exit 0
