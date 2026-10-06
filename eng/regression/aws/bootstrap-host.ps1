# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Staged, idempotent bootstrap of the Windows regression host (regression-host.yaml). Runs as SYSTEM from the
    instance's user data on every boot until it writes 'done' into <Root>\stage.txt; a stage that needs a reboot
    records the next stage first, so an interrupted stage is retried on the next boot. Log: <Root>\bootstrap.log.

    Stages
      features  Hyper-V, Containers, WSL and Virtual Machine Platform -> reboot
      tools     Chocolatey; PowerShell 7, Git, GitHub CLI, 7-Zip, AWS CLI, sqlcmd; .NET 10 SDK; SQL Server 2022
                Developer (SQLENGINE, mixed mode, TCP on; sa password from the SSM parameter)
      docker    WSL 2 kernel; Docker Desktop (WSL 2 backend, unattended); .wslconfig for the VM size -> reboot
      repos     Ed-Fi-API-Publisher and Data-Management-Service under C:\GIT\Ed-Fi; tool versions -> done

    Docker Desktop is not supported by Docker on Windows Server. It installs and runs on Server 2022/2025 with the WSL 2
    backend, which is what this host relies on; if the 'docker' stage cannot start it, the log says so and the README's
    fallback applies (Docker Engine inside a WSL distribution, harness run from pwsh in WSL).
.NOTES
    Windows PowerShell 5.1 syntax only: this is what the user-data runner has before pwsh is installed.
#>
[CmdletBinding()]
param(
    [string] $Root = 'C:\regression-bootstrap',
    [Parameter(Mandatory)] [string] $Region,
    # Empty when the instance has no profile: the password is then generated on the host (see Get-SaPassword).
    [string] $SaPasswordParameter = '/apipub-regression/sql-sa-password',
    [string] $RepositoryBranch = 'main',
    [string] $LoginUser = 'Administrator',
    [string] $RepositoryRoot = 'C:\GIT\Ed-Fi',
    # Docker Desktop's WSL 2 VM: half of a 64 GB host; the other half serves the host SQL Server and Windows.
    [string] $WslMemory = '32GB',
    [int] $WslProcessors = 6
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
New-Item -ItemType Directory -Force -Path $Root | Out-Null
Start-Transcript -Path (Join-Path $Root 'bootstrap.log') -Append | Out-Null

$stageFile = Join-Path $Root 'stage.txt'
function Get-Stage { if (Test-Path $stageFile) { (Get-Content $stageFile -Raw).Trim() } else { 'features' } }
function Set-Stage([string] $Stage) { Set-Content -Path $stageFile -Value $Stage; Write-Host "[$(Get-Date -Format s)] stage -> $Stage" }
function Write-Step([string] $Text) { Write-Host "[$(Get-Date -Format s)] $Text" }

function Add-MachinePath([string] $Folder)
{
    $current = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if (($current -split ';') -notcontains $Folder)
    {
        [Environment]::SetEnvironmentVariable('Path', "$current;$Folder", 'Machine')
    }
    if (($env:Path -split ';') -notcontains $Folder) { $env:Path = "$env:Path;$Folder" }
}

function Invoke-Download([string] $Url, [string] $Target)
{
    if (Test-Path $Target) { return $Target }
    Write-Step "Downloading $Url"
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Target
    return $Target
}

function Get-SaPassword
{
    if (-not $SaPasswordParameter)
    {
        # No instance profile (InstanceProfileMode=None): generate the password here and keep it for the Administrators
        # group only. The Northridge script uses Windows authentication, so nothing in the runbook needs it.
        $file = Join-Path $Root 'sa-password.txt'
        if (-not (Test-Path $file))
        {
            $bytes = New-Object byte[] 24
            [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
            $generated = (([Convert]::ToBase64String($bytes)) -replace '[+/=]', 'x') + 'Aa1!'
            Set-Content -Path $file -Value $generated -NoNewline
            & icacls $file /inheritance:r /grant:r 'BUILTIN\Administrators:F' 'SYSTEM:F' | Out-Null
            Write-Step "SQL Server sa password generated and stored in $file (Administrators only)"
        }
        return (Get-Content $file -Raw).Trim()
    }

    $aws = Join-Path $env:ProgramFiles 'Amazon\AWSCLIV2\aws.exe'
    $value = & $aws ssm get-parameter --name $SaPasswordParameter --with-decryption --query 'Parameter.Value' --output text --region $Region
    if ($LASTEXITCODE -ne 0 -or -not $value) { throw "Could not read the sa password from SSM parameter '$SaPasswordParameter' (aws exit $LASTEXITCODE)." }
    return $value.Trim()
}

function Install-Features
{
    Write-Step 'Installing Hyper-V, Containers, WSL and Virtual Machine Platform'
    Install-WindowsFeature -Name Hyper-V, Containers -IncludeManagementTools | Out-Null
    Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux, VirtualMachinePlatform -All -NoRestart | Out-Null
    Set-Stage 'tools'
    Write-Step 'Rebooting for the virtualization features'
    Restart-Computer -Force
}

function Install-Tools
{
    if (-not (Get-CimInstance Win32_ComputerSystem).HypervisorPresent)
    {
        throw 'The hypervisor is not running after the feature install: nested virtualization is not enabled on this instance (Invoke-RegressionHost.ps1 -Deploy enables it; check CpuOptions with -Status). Nothing else is installed until it is.'
    }

    if (-not (Get-Command choco -ErrorAction SilentlyContinue))
    {
        Write-Step 'Installing Chocolatey'
        Invoke-Expression ((New-Object Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
        Add-MachinePath (Join-Path $env:ProgramData 'chocolatey\bin')
    }

    Write-Step 'Installing PowerShell 7, Git, GitHub CLI, 7-Zip, AWS CLI, sqlcmd'
    & choco install -y --no-progress powershell-core git gh 7zip awscli sqlcmd
    if ($LASTEXITCODE -notin @(0, 1641, 3010)) { throw "choco install failed ($LASTEXITCODE)." }
    Add-MachinePath (Join-Path $env:ProgramFiles 'PowerShell\7')
    Add-MachinePath (Join-Path $env:ProgramFiles 'Git\cmd')
    Add-MachinePath (Join-Path $env:ProgramFiles '7-Zip')
    Add-MachinePath (Join-Path $env:ProgramFiles 'Amazon\AWSCLIV2')

    $dotnetRoot = Join-Path $env:ProgramFiles 'dotnet'
    $dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
    $hasSdk10 = (Test-Path $dotnetExe) -and ((& $dotnetExe --list-sdks) -match '^10\.')
    if (-not $hasSdk10)
    {
        Write-Step 'Installing the .NET 10 SDK'
        $installer = Invoke-Download 'https://dot.net/v1/dotnet-install.ps1' (Join-Path $Root 'dotnet-install.ps1')
        & $installer -Channel 10.0 -InstallDir $dotnetRoot -NoPath
        Add-MachinePath $dotnetRoot
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetRoot, 'Machine')
    }

    if (-not (Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue))
    {
        Write-Step 'Installing SQL Server 2022 Developer (database engine, mixed mode, TCP on)'
        # SQL Server 2022 Developer edition installer (the "SSEI" bootstrapper from the SQL Server downloads page).
        $ssei = Invoke-Download 'https://go.microsoft.com/fwlink/?linkid=2215158' (Join-Path $Root 'SQL2022-SSEI-Dev.exe')
        $media = Join-Path $Root 'sql-media'
        New-Item -ItemType Directory -Force -Path $media | Out-Null
        if (-not (Get-ChildItem -Path $media -Filter '*.iso' -ErrorAction SilentlyContinue))
        {
            $download = Start-Process -FilePath $ssei -ArgumentList '/ACTION=Download', "/MEDIAPATH=$media", '/MEDIATYPE=ISO', '/QUIET' -Wait -PassThru
            if ($download.ExitCode -ne 0) { throw "SQL Server media download failed ($($download.ExitCode))." }
        }
        $iso = Get-ChildItem -Path $media -Filter '*.iso' | Select-Object -First 1
        $image = Mount-DiskImage -ImagePath $iso.FullName -PassThru
        try
        {
            $drive = ($image | Get-Volume).DriveLetter
            $saPassword = Get-SaPassword
            $setupArgs = @('/Q', '/ACTION=Install', '/FEATURES=SQLENGINE', '/INSTANCENAME=MSSQLSERVER', '/SQLSYSADMINACCOUNTS=BUILTIN\Administrators',
                '/SECURITYMODE=SQL', "/SAPWD=$saPassword", '/TCPENABLED=1', '/SQLSVCSTARTUPTYPE=Automatic', '/UPDATEENABLED=False',
                '/IACCEPTSQLSERVERLICENSETERMS', '/SUPPRESSPRIVACYSTATEMENTNOTICE')
            $setup = Start-Process -FilePath "${drive}:\setup.exe" -ArgumentList $setupArgs -Wait -PassThru
            if ($setup.ExitCode -notin @(0, 3010)) { throw "SQL Server setup failed ($($setup.ExitCode)); see $env:ProgramFiles\Microsoft SQL Server\160\Setup Bootstrap\Log\Summary.txt." }
        }
        finally { Dismount-DiskImage -ImagePath $iso.FullName | Out-Null }
    }

    Set-Stage 'docker'
}

function Install-Docker
{
    Write-Step 'Updating the WSL 2 kernel'
    & wsl --install --no-distribution --no-launch 2>&1 | Out-Null
    & wsl --update 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        # No Store access on Server: install the WSL MSI from the GitHub release instead.
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/microsoft/WSL/releases/latest' -UseBasicParsing
        $asset = $release.assets | Where-Object { $_.name -like 'wsl.*.x64.msi' } | Select-Object -First 1
        if (-not $asset) { throw 'No WSL x64 MSI found in the latest microsoft/WSL release.' }
        $msi = Invoke-Download $asset.browser_download_url (Join-Path $Root $asset.name)
        $install = Start-Process msiexec.exe -ArgumentList '/i', $msi, '/qn', '/norestart' -Wait -PassThru
        if ($install.ExitCode -notin @(0, 3010)) { throw "WSL MSI install failed ($($install.ExitCode))." }
    }
    & wsl --set-default-version 2 2>&1 | Out-Null

    # One .wslconfig for every profile that will run Docker Desktop (the default profile seeds new users).
    $wslConfig = "[wsl2]`nmemory=$WslMemory`nprocessors=$WslProcessors`nswap=0`n"
    foreach ($profile in @((Join-Path $env:SystemDrive 'Users\Default')) + @(Get-ChildItem -Path (Join-Path $env:SystemDrive 'Users') -Directory | Where-Object { $_.Name -notin @('Public', 'Default User', 'All Users') } | ForEach-Object { $_.FullName }))
    {
        if (Test-Path $profile) { Set-Content -Path (Join-Path $profile '.wslconfig') -Value $wslConfig -Encoding ascii }
    }

    $dockerExe = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (-not (Test-Path $dockerExe))
    {
        Write-Step 'Installing Docker Desktop (WSL 2 backend, unattended)'
        $installer = Invoke-Download 'https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe' (Join-Path $Root 'DockerDesktopInstaller.exe')
        $install = Start-Process -FilePath $installer -ArgumentList 'install', '--quiet', '--accept-license', '--backend=wsl-2', '--always-run-service' -Wait -PassThru
        if ($install.ExitCode -notin @(0, 3010)) { throw "Docker Desktop install failed ($($install.ExitCode)). Docker does not support Windows Server; see the README fallback." }
    }
    Add-MachinePath (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin')
    if (Get-LocalGroup -Name 'docker-users' -ErrorAction SilentlyContinue)
    {
        if (-not (Get-LocalGroupMember -Group 'docker-users' -Member $LoginUser -ErrorAction SilentlyContinue)) { Add-LocalGroupMember -Group 'docker-users' -Member $LoginUser }
    }

    Set-Stage 'repos'
    Write-Step 'Rebooting after the Docker Desktop install'
    Restart-Computer -Force
}

function Install-Repositories
{
    New-Item -ItemType Directory -Force -Path $RepositoryRoot | Out-Null
    $git = Join-Path $env:ProgramFiles 'Git\cmd\git.exe'
    $repositories = @(
        @{ Name = 'Ed-Fi-API-Publisher'; Url = 'https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher.git'; Branch = $RepositoryBranch },
        @{ Name = 'Data-Management-Service'; Url = 'https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service.git'; Branch = 'main' }
    )
    foreach ($repository in $repositories)
    {
        $target = Join-Path $RepositoryRoot $repository.Name
        if (Test-Path (Join-Path $target '.git')) { Write-Step "$($repository.Name) already cloned"; continue }
        Write-Step "Cloning $($repository.Name) ($($repository.Branch))"
        & $git clone --branch $repository.Branch $repository.Url $target
        if ($LASTEXITCODE -ne 0) { throw "git clone of $($repository.Name) failed ($LASTEXITCODE)." }
    }
    # The person signing in works in these clones with their own account.
    & icacls $RepositoryRoot /grant 'Users:(OI)(CI)M' /T /Q | Out-Null

    Write-Step 'Tool versions'
    foreach ($command in @(
            @{ Label = 'pwsh'; Exe = (Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'); Args = @('--version') },
            @{ Label = 'dotnet'; Exe = (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'); Args = @('--list-sdks') },
            @{ Label = 'git'; Exe = $git; Args = @('--version') },
            @{ Label = 'docker'; Exe = (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe'); Args = @('--version') },
            @{ Label = 'sqlcmd'; Exe = 'sqlcmd'; Args = @('--version') }))
    {
        try { Write-Host ("  {0,-8} {1}" -f $command.Label, ((& $command.Exe @($command.Args) 2>&1) -join ' ')) } catch { Write-Host ("  {0,-8} not found" -f $command.Label) }
    }
    Write-Host "  SQL Server: $((Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue).Status)"
    Set-Stage 'done'
}

try
{
    $stage = Get-Stage
    Write-Step "Bootstrap stage: $stage"
    switch ($stage)
    {
        'features' { Install-Features }
        'tools' { Install-Tools; Install-Docker }
        'docker' { Install-Docker }
        'repos' { Install-Repositories }
        'done' { Write-Step 'Nothing to do.' }
        default { throw "Unknown stage '$stage' in $stageFile." }
    }
}
catch
{
    Write-Step "FAILED in stage '$(Get-Stage)': $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}
finally
{
    Stop-Transcript | Out-Null
}
