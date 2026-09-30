# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Item D4: DMS self-contained authentication smoke (Keycloak disabled, the Config Service issues tokens)
    (APIPUB-146). Restart the DMS without -EnableKeycloak first (see arms/arm-d-dms.md). The oauth URL the DMS
    advertises must then issue a token whose issuer is not a Keycloak realm, and a publish must complete.
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

$item = 'D4'
# An exception anywhere below still ends in a result row (Complete-Item is not reached when a step throws).
trap { exit (Complete-ItemAfterError -Item $item -ArmName $Arm -ResultsFile $ResultsFile -Failures $failures -ErrorRecord $_) }
$armDef = Get-Arm $Arm   # not $arm: it would inherit the [string] constraint of the -Arm parameter
$publisher = Resolve-Publisher -Path $PublisherPath -Image $PublisherImage
$run = New-RunFolder -Item $item -ArmName $armDef.Name -RunRoot $RunRoot
$failures = New-FailureList
Write-Host "Item $item DMS self-contained authentication on arm $($armDef.Name) ($($armDef.Description)); publisher $($publisher.Label); run folder $run"

$target = Get-ApiUrls $armDef.TargetUrl
Write-Host "  DMS oauth endpoint: $($target.Oauth)"

# The advertised oauth host cannot tell the modes apart: the DMS fronts the token endpoint and advertises its own
# address also when Keycloak issues the tokens (see D2). The token's issuer can: a Keycloak issuer is a realm URL. An
# opaque token or one without iss fails here too, since then nothing shows which provider issued it.
$token = $null
try { $token = Get-BearerToken $armDef.TargetUrl $armDef.TargetKey $armDef.TargetSecret } catch { }
Assert-Condition $failures ($null -ne $token) 'a bearer token can be obtained from the self-contained token endpoint with the arm D target credentials'
$issuer = Get-JwtIssuer $token
Assert-Condition $failures ($issuer -and "$issuer" -notmatch '/realms/') "the token was issued by the DMS itself, not Keycloak (iss '$issuer')"

$result = Invoke-Publisher -Publisher $publisher -Arm $armDef -RunFolder $run -Arguments @('--includeDescriptors=true')
Assert-Condition $failures ($result.ExitCode -eq 0) "the publish exited with 0 (was $($result.ExitCode))"
Assert-Condition $failures (-not (Test-LogContains $result.Log 'rejected as unauthorized')) 'no request was rejected as unauthorized'

$counts = Compare-Counts -Arm $armDef -Log $result.Log -ReportCsv (Join-Path $run 'counts.csv')
Assert-Condition $failures ($counts.Mismatches.Count -eq 0) "counts match between the ODS source and the DMS ($($counts.Mismatches.Count) mismatch(es))"

exit (Complete-Item -Item $item -Arm $armDef -ResultsFile $ResultsFile -Failures $failures -Counts $counts.Summary -Seconds $result.Seconds -Log $result.Log -Notes "oauth $($target.Oauth)")
