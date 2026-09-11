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

    if ($PublisherPath -like '*.dll') { & dotnet $PublisherPath @args *>&1 | Tee-Object -FilePath $log | Out-Null }
    else { & $PublisherPath @args *>&1 | Tee-Object -FilePath $log | Out-Null }

    if ($LASTEXITCODE -ne 0) { throw "Publisher exited with $LASTEXITCODE (see $log)" }
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

$cursorTime = Measure-Command { Invoke-Publisher $cursorDb $false 'cursor.log' }
$offsetTime = Measure-Command { Invoke-Publisher $offsetDb $true  'offset.log' }

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
Write-Host ("Wall clock: cursor {0:N1}s, offset {1:N1}s" -f $cursorTime.TotalSeconds, $offsetTime.TotalSeconds)
Write-Host "Report: $(Join-Path $OutputFolder 'parity-report.csv')"

if ($mismatches -gt 0) { Write-Error "$mismatches resource(s) differ between cursor and offset paging."; exit 1 }
Write-Host 'PARITY OK: every resource published the same item set and row count under both paging modes.'
