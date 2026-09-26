# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item 10: PostgreSQL configuration store smoke (the Delaware deployment). The store database and table from
    docs/ConfigurationStore/PostgreSql.md are created in the arm's db-admin container, the two connections are
    inserted with pgcrypto-encrypted key and secret, and the publisher runs by connection name. Afterwards the store
    must hold the last change version the run recorded.
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
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item PostgreSQL configuration store on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$storeDb = 'edfi_api_publisher_configuration'
$sourceName = 'Regression_Source'
$targetName = 'Regression_Target'

$exists = (Invoke-ArmPsql $armDef -Service db-admin -TuplesOnly -Sql "select 1 from pg_database where datname = '$storeDb'") -join ''
if ($exists.Trim() -ne '1') { Invoke-ArmPsql $armDef -Service db-admin -Sql "create database $storeDb" | Out-Null }

$setup = @"
create schema if not exists dbo;
create table if not exists dbo.configuration_value (
    configuration_key varchar(450) not null constraint configuration_value_pk primary key,
    configuration_value text,
    configuration_value_encrypted bytea);
create extension if not exists pgcrypto;
delete from dbo.configuration_value where configuration_key like '/ed-fi/apiPublisher/connections/Regression_%';
insert into dbo.configuration_value (configuration_key, configuration_value) values
    ('/ed-fi/apiPublisher/connections/$sourceName/url', '$($armDef.SourceUrl)'),
    ('/ed-fi/apiPublisher/connections/$targetName/url', '$($armDef.TargetUrl)');
insert into dbo.configuration_value (configuration_key, configuration_value_encrypted) values
    ('/ed-fi/apiPublisher/connections/$sourceName/key', pgp_sym_encrypt('$($armDef.SourceKey)', '$EncryptionPassword')),
    ('/ed-fi/apiPublisher/connections/$sourceName/secret', pgp_sym_encrypt('$($armDef.SourceSecret)', '$EncryptionPassword')),
    ('/ed-fi/apiPublisher/connections/$targetName/key', pgp_sym_encrypt('$($armDef.TargetKey)', '$EncryptionPassword')),
    ('/ed-fi/apiPublisher/connections/$targetName/secret', pgp_sym_encrypt('$($armDef.TargetSecret)', '$EncryptionPassword'));
"@
$setupFile = Join-Path $run 'store-setup.sql'
Set-Content -Path $setupFile -Value $setup

if ($publisher.Mode -eq 'docker')
{
    # The container reaches the store on the arm's network, and the connection URLs must be in-network too.
    $setup.Replace($armDef.SourceUrl, $armDef.SourceUrlInNetwork).Replace($armDef.TargetUrl, $armDef.TargetUrlInNetwork) | Set-Content -Path $setupFile
    $connectionString = "Host=db-admin;Port=5432;Database=$storeDb;Username=$($armDef.Env['POSTGRES_USER']);Password=$($armDef.Env['POSTGRES_PASSWORD'])"
}
else
{
    $connectionString = "Host=localhost;Port=$($armDef.Env['ADMIN_DB_PORT']);Database=$storeDb;Username=$($armDef.Env['POSTGRES_USER']);Password=$($armDef.Env['POSTGRES_PASSWORD'])"
}
Invoke-ArmPsql $armDef -Service db-admin -Database $storeDb -File $setupFile | Out-Null
Write-Host "  store ready in db-admin/$storeDb ($sourceName, $targetName)"

Reset-RegressionTarget $armDef
$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -NoConnectionArguments -ConfigurationStoreConnectionString $connectionString `
    -Arguments @('--configurationStoreProvider=postgreSql', "--postgreSqlEncryptionPassword=$EncryptionPassword", "--sourceName=$sourceName", "--targetName=$targetName", '--disableCursorPaging=true', '--includeDescriptors=true')

Assert-Condition $failures ($result.ExitCode -eq 0) "the publish by connection name exited with 0 (was $($result.ExitCode))"
$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "counts match ($($counts.Mismatches.Count) mismatch(es))"

$recorded = (Invoke-ArmPsql $armDef -Service db-admin -Database $storeDb -TuplesOnly -Sql "select count(*) from dbo.configuration_value where configuration_key ilike '%lastChangeVersion%'") -join ''
Assert-Condition $failures ([int] $recorded.Trim() -ge 1) "the store holds the last change version the run recorded ($($recorded.Trim()) row(s))"

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes "store in db-admin/$storeDb; --configurationStoreProvider=postgreSql")
