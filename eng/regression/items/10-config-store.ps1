# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 10: PostgreSQL configuration store smoke (the Delaware deployment). The store database and table from
    docs/ConfigurationStore/PostgreSql.md are created in the arm's db-admin container, the two connections are
    inserted with pgcrypto-encrypted key and secret, and the publisher runs by connection name. Afterwards the store
    must hold, for the target, the source's newest change version at the start of the run (the end of its window).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [string] $PublisherPath,
    [string] $PublisherImage,
    [string] $ResultsFile = (Join-Path $PSScriptRoot '../results/results-local.md'),
    [string] $RunRoot,
    [string] $EncryptionPassword = 'regression-store-password'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../lib/Regression.psm1') -Force

$item = '10'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item PostgreSQL configuration store on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$storeDb = 'edfi_api_publisher_configuration'
$sourceName = 'Regression_Source'
$targetName = 'Regression_Target'
$connectionString = Initialize-PostgreSqlConfigurationStore -Arm $armDef -Publisher $publisher -RunFolder $run -SourceName $sourceName -TargetName $targetName -EncryptionPassword $EncryptionPassword -Database $storeDb

Reset-RegressionTarget $armDef
# The change window of the run ends at the source's newest change version, which is what the store must record.
$sourceToken = Get-BearerToken $armDef.SourceUrl $armDef.SourceKey $armDef.SourceSecret
$newest = Get-NewestChangeVersion $armDef.SourceUrl $sourceToken
$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -NoConnectionArguments -ConfigurationStoreConnectionString $connectionString `
    -Arguments @('--configurationStoreProvider=postgreSql', "--postgreSqlEncryptionPassword=$EncryptionPassword", "--sourceName=$sourceName", "--targetName=$targetName", '--disableCursorPaging=true', '--includeDescriptors=true')

Assert-Condition $failures ($result.ExitCode -eq 0) "the publish by connection name exited with 0 (was $($result.ExitCode))"
$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "counts match ($($counts.Mismatches.Count) mismatch(es))"

$recorded = Get-StoredLastChangeVersion $armDef $sourceName $targetName -Database $storeDb
Assert-Condition $failures ($null -ne $newest -and $recorded -eq $newest) "the store recorded the run's last change version for $targetName (recorded $recorded, source newest change version at the start $newest)"

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes "store in db-admin/$storeDb; --configurationStoreProvider=postgreSql; recorded change version $recorded")
