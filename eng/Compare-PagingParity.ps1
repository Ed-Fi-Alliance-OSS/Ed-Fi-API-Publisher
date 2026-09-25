# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Runs the API Publisher twice against the same source (cursor paging on, then off) into two SQLite
    targets and diffs the published item ids per resource (APIPUB-139 / APIPUB-141 parity validation).
.PARAMETER PublisherPath
    Path to EdFiApiPublisher.exe (or the dll to run with dotnet).
.PARAMETER SourceUrl / SourceKey / SourceSecret
    Source ODS/API 7.3+ connection.
.PARAMETER OutputFolder
    Where the two SQLite files and the report land (default: .\parity-<timestamp>).
.PARAMETER ExtraArgs
    Additional publisher arguments applied to both runs (e.g. '--include=/ed-fi/students', '--ignoreIsolation=true').
    Run the script from a PowerShell session; when launched with "pwsh -File" from another shell, array arguments
    are not parsed as arrays and the publisher receives them as one comma-joined value.
.EXAMPLE
    .\eng\Compare-PagingParity.ps1 -PublisherPath .\src\EdFi.Tools.ApiPublisher.Cli\bin\Debug\net10.0\EdFiApiPublisher.exe `
        -SourceUrl http://localhost:8001 -SourceKey minimalKey -SourceSecret minimalSecret -ExtraArgs '--ignoreIsolation=true','--includeDescriptors=true'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublisherPath,
    [Parameter(Mandatory)] [string] $SourceUrl,
    [Parameter(Mandatory)] [string] $SourceKey,
    [Parameter(Mandatory)] [string] $SourceSecret,
    [string] $OutputFolder = (Join-Path (Get-Location) ("parity-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string[]] $ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'

# SQLite access uses the Microsoft.Data.Sqlite assemblies that ship next to the publisher binaries,
# so no external sqlite3 CLI is required.
$publisherBin = Split-Path -Parent (Resolve-Path $PublisherPath).Path

function Initialize-SqliteReader([string] $BinFolder) {
    foreach ($name in 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll') {
        $path = Join-Path $BinFolder $name
        if (-not (Test-Path $path)) { throw "Expected '$name' next to the publisher binaries at '$BinFolder'." }
        Add-Type -Path $path
    }

    # The native library lives under runtimes\<rid>\native in a framework-dependent build (or next to the exe in a
    # self-contained one); preload it by full path so the managed provider resolves 'e_sqlite3' from this host.
    $native = Get-ChildItem -Path $BinFolder -Filter 'e_sqlite3.dll' -Recurse -File |
        Where-Object { $_.FullName -match 'win-x64' -or $_.DirectoryName -eq $BinFolder } |
        Select-Object -First 1
    if (-not $native) { throw "e_sqlite3.dll was not found under '$BinFolder'." }
    [void][System.Runtime.InteropServices.NativeLibrary]::Load($native.FullName)
    [SQLitePCL.Batteries_V2]::Init()
}

function Invoke-SqliteScalarQuery([string] $DbFile, [string] $Sql) {
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DbFile;Mode=ReadOnly")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Sql
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                if (-not $reader.IsDBNull(0)) { $reader.GetValue(0).ToString() }
            }
        }
        finally { $reader.Dispose() }
    }
    finally { $connection.Dispose() }
}

Initialize-SqliteReader $publisherBin

New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null

function Invoke-Publisher([string] $TargetFile, [bool] $DisableCursorPaging, [string] $LogName) {
    # '--targetFile' is a real switch: the SQLite plugin maps it to Connections:Target:File
    # (see src/EdFi.Tools.ApiPublisher.Connections.Sqlite/Plugin.cs). A SQLite target has no
    # dependency metadata, so the publisher must take it from the source.
    $args = @(
        "--sourceUrl=$SourceUrl", "--sourceKey=$SourceKey", "--sourceSecret=$SourceSecret",
        '--targetType=sqlite', "--targetFile=$TargetFile",
        '--useSourceDependencyMetadata=true',
        "--disableCursorPaging=$($DisableCursorPaging.ToString().ToLower())"
    ) + $ExtraArgs

    Write-Host "Running publisher (disableCursorPaging=$DisableCursorPaging) -> $TargetFile"
    $log = Join-Path $OutputFolder $LogName
    $errorLog = [IO.Path]::ChangeExtension($log, '.err.log')
    $memoryCsv = [IO.Path]::ChangeExtension($log, '.memory.csv')

    # The publisher runs as a child process so its working set can be sampled while it works: the peak is
    # what the paging-mode comparison needs (APIPUB-141). The process reports its own peak on exit; the
    # one-second samples next to the log show how the working set moved during the run.
    if ($PublisherPath -like '*.dll') { $exe = 'dotnet'; $argumentList = @($PublisherPath) + $args }
    else { $exe = $PublisherPath; $argumentList = $args }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $exe -ArgumentList $argumentList -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError $errorLog
    'elapsed_s,working_set_mb' | Set-Content $memoryCsv
    $sampledPeakBytes = 0L
    while (-not $process.HasExited) {
        $process.Refresh()
        $workingSet = [long] $process.WorkingSet64
        if ($workingSet -gt $sampledPeakBytes) { $sampledPeakBytes = $workingSet }
        Add-Content $memoryCsv ('{0:F0},{1:F1}' -f $stopwatch.Elapsed.TotalSeconds, ($workingSet / 1MB))
        Start-Sleep -Milliseconds 1000
    }
    $process.WaitForExit()
    $stopwatch.Stop()
    $peakBytes = [math]::Max([long] $process.PeakWorkingSet64, $sampledPeakBytes)

    # A non-zero exit code means the run lost or skipped something (APIPUB-120); the items that did land are still
    # compared, because a resource the source refuses to both runs (e.g. a 403) is not a paging difference, while
    # a page lost to one run shows up in the report as a mismatch.
    if ($process.ExitCode -ne 0) { Write-Warning "Publisher exited with $($process.ExitCode) (see $log); the diff below covers what was published." }

    return [pscustomobject]@{
        Seconds          = $stopwatch.Elapsed.TotalSeconds
        PeakWorkingSetMB = [math]::Round($peakBytes / 1MB, 1)
        ExitCode         = $process.ExitCode
        Log              = $log
    }
}

function Get-ItemIds([string] $DbFile) {
    $tables = @(Invoke-SqliteScalarQuery $DbFile "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '%\_\_%' ESCAPE '\';")
    $result = @{}
    foreach ($table in $tables) {
        # Each row holds a WHOLE page as a JSON array (see SqlLiteProcessingBlocksFactoryBase.CreateProcessDataMessages),
        # so the ids have to be unnested with json_each rather than read off the row itself.
        $ids = @(Invoke-SqliteScalarQuery $DbFile "SELECT json_extract(value, '$.id') FROM [$table], json_each([$table].json);")
        $result[$table] = @($ids | Where-Object { $_ })
    }
    return $result
}

$cursorDb = Join-Path $OutputFolder 'cursor.sqlite'
$offsetDb = Join-Path $OutputFolder 'offset.sqlite'

$cursorRun = Invoke-Publisher $cursorDb $false 'cursor.log'
$offsetRun = Invoke-Publisher $offsetDb $true  'offset.log'

$cursor = Get-ItemIds $cursorDb
$offset = Get-ItemIds $offsetDb

$cursorTotal = ($cursor.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
$offsetTotal = ($offset.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum

if (-not $cursorTotal) { $cursorTotal = 0 }
if (-not $offsetTotal) { $offsetTotal = 0 }

if ($cursorTotal -eq 0 -and $offsetTotal -eq 0) {
    Write-Error "No items found in either target ($cursorDb, $offsetDb); check the run logs in $OutputFolder. Parity was NOT verified."
    exit 1
}

$report = New-Object System.Collections.Generic.List[object]
$allTables = @($cursor.Keys + $offset.Keys | Sort-Object -Unique)
$mismatches = 0

foreach ($table in $allTables) {
    $c = @($cursor[$table]); $o = @($offset[$table])
    $cursorSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $c)
    $offsetSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $o)
    $onlyCursor = @($cursorSet | Where-Object { -not $offsetSet.Contains($_) })
    $onlyOffset = @($offsetSet | Where-Object { -not $cursorSet.Contains($_) })

    # Parity = the same distinct ids AND the same number of rows. Duplicate rows are not a paging defect by
    # themselves: a stage that re-publishes a whole resource (e.g. the authorization-failure retry pipeline for
    # students) writes every item twice under BOTH paging modes, so only a difference between the two sides counts.
    $dupCursor = $c.Count - $cursorSet.Count
    $dupOffset = $o.Count - $offsetSet.Count
    $ok = ($onlyCursor.Count -eq 0 -and $onlyOffset.Count -eq 0 -and $c.Count -eq $o.Count)
    if (-not $ok) { $mismatches++ }

    $report.Add([pscustomobject]@{
        Resource            = $table
        CursorRows          = $c.Count
        OffsetRows          = $o.Count
        OnlyInCursor        = $onlyCursor.Count
        OnlyInOffset        = $onlyOffset.Count
        DuplicateRowsCursor = $dupCursor
        DuplicateRowsOffset = $dupOffset
        Match               = $ok
    })
}

$report | Format-Table -AutoSize | Out-String | Write-Host
$report | Export-Csv -NoTypeInformation -Path (Join-Path $OutputFolder 'parity-report.csv')

Write-Host ("Items compared: cursor {0:N0}, offset {1:N0}" -f $cursorTotal, $offsetTotal)
Write-Host ("Wall clock: cursor {0:N1}s, offset {1:N1}s" -f $cursorRun.Seconds, $offsetRun.Seconds)
Write-Host ("Peak working set: cursor {0:N1} MB, offset {1:N1} MB (one-second samples next to each log as *.memory.csv)" -f $cursorRun.PeakWorkingSetMB, $offsetRun.PeakWorkingSetMB)
Write-Host ("Publisher exit codes: cursor {0}, offset {1}" -f $cursorRun.ExitCode, $offsetRun.ExitCode)
Write-Host "Report: $(Join-Path $OutputFolder 'parity-report.csv')"

if ($mismatches -gt 0) { Write-Error "$mismatches resource(s) differ between cursor and offset paging."; exit 1 }
Write-Host 'PARITY OK: every resource published the same item set and row count under both paging modes.'
