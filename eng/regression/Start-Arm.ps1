# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Starts, resets or stops one regression arm (A, B, C from Docker Hub images; D is external, see arms/arm-d-dms.md).
.EXAMPLE
    .\Start-Arm.ps1 -Arm B                 # up, clone the templates, bootstrap the Admin database
    .\Start-Arm.ps1 -Arm B -ResetTarget    # put the target back to the empty minimal template
    .\Start-Arm.ps1 -Arm B -ResetSource    # undo the source edits of items 1 and 8 (fresh populated template)
    .\Start-Arm.ps1 -Arm B -Down           # stop and remove the containers (volumes kept)
    .\Start-Arm.ps1 -Arm B -Down -Purge    # also remove the volumes
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Arm,
    [switch] $Down,
    [switch] $Purge,
    [switch] $ResetTarget,
    [switch] $ResetSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/Regression.psm1') -Force

$definition = Get-Arm $Arm

if ($Down) { Stop-RegressionArm $definition -Purge:$Purge; return }
if ($ResetTarget -or $ResetSource)
{
    if ($ResetSource) { Reset-RegressionSource $definition }
    if ($ResetTarget) { Reset-RegressionTarget $definition }
    return
}

Start-RegressionArm $definition
