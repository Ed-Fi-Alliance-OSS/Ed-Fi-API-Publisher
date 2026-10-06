# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7.4
<#
.SYNOPSIS
    Creates, starts, stops, inspects and connects to the AWS regression host (regression-host.yaml) from a workstation,
    through the AWS CLI. Everything goes through Systems Manager: the host has no inbound ports.
.DESCRIPTION
    -Deploy     creates or updates the CloudFormation stack (default VPC and a public subnet of the region unless
                -VpcId/-SubnetId are given), stores a generated SQL Server sa password in SSM if none exists, then
                switches nested virtualization on (stop, modify-instance-cpu-options, start) because CloudFormation
                cannot set it yet. Idempotent.
    -Start / -Stop   start or stop the instance (stopped = only the volume is billed).
    -Status     instance state, CpuOptions, bootstrap stage and the tail of the bootstrap log (via Run Command).
    -Password   the Administrator password, decrypted with -KeyFile (the key pair's .pem).
    -Rdp        port-forwards 3389 to localhost:13389 through Session Manager and opens mstsc. Without an instance
                profile it connects to the public IP directly, after pointing the RDP rule at this workstation's current
                public IP (or -RdpAllowedCidr).
    -PortForward <port>   forwards one host port (Swagger 8202, WireMock admin 8280, ...) to the same local port.
    -Destroy    deletes the stack (and, with -DeleteSaPassword, the SSM parameter). The instance and its volume are gone.
.EXAMPLE
    .\Invoke-RegressionHost.ps1 -Deploy -KeyPairName apipub-regression -OwnerTag antonio.diaz@simpat.tech
    .\Invoke-RegressionHost.ps1 -Status
    .\Invoke-RegressionHost.ps1 -Password -KeyFile ~\.ssh\apipub-regression.pem
    .\Invoke-RegressionHost.ps1 -Rdp
    .\Invoke-RegressionHost.ps1 -Stop
.NOTES
    Needs the AWS CLI v2, a signed-in session (aws login / aws sso login / credentials), and for -Rdp/-PortForward the
    Session Manager plugin (https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html).
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Deploy', Mandatory)] [switch] $Deploy,
    [Parameter(ParameterSetName = 'Start', Mandatory)] [switch] $Start,
    [Parameter(ParameterSetName = 'Stop', Mandatory)] [switch] $Stop,
    [Parameter(ParameterSetName = 'Status')] [switch] $Status,
    [Parameter(ParameterSetName = 'Password', Mandatory)] [switch] $Password,
    [Parameter(ParameterSetName = 'Rdp', Mandatory)] [switch] $Rdp,
    [Parameter(ParameterSetName = 'PortForward', Mandatory)] [int] $PortForward,
    [Parameter(ParameterSetName = 'Destroy', Mandatory)] [switch] $Destroy,

    [string] $StackName = 'apipub-regression-host',
    [string] $Region,
    [string] $Profile,

    # -Deploy
    [string] $KeyPairName,
    [string] $InstanceType = 'r7i.2xlarge',
    [int] $VolumeSizeGiB = 300,
    [string] $VpcId,
    [string] $SubnetId,
    [string] $AmiParameter = '/aws/service/ami-windows-latest/Windows_Server-2025-English-Full-Base',
    [string] $BootstrapUrl,
    # Uploads the local bootstrap-host.ps1 to s3://<bucket>/apipub-regression/<content hash>/ and points the instance at
    # it, so an unpushed branch can be deployed; the instance role gets read access to the bucket.
    [string] $BootstrapBucket,
    [string] $RepositoryBranch,
    # An instance profile an administrator created (AmazonSSMManagedInstanceCore + the SSM parameter + the bootstrap bucket)
    # when the deployer may not create IAM roles. The stack then creates none; the deployer still needs iam:PassRole.
    [string] $ExistingInstanceProfileName = '',
    # No instance profile at all (a deployer without iam:PassRole, e.g. the PowerUserAccess permission set): no Systems
    # Manager, so RDP is opened to -RdpAllowedCidr (default: this workstation's public IP/32), the bootstrap is fetched
    # through a presigned S3 URL (-BootstrapBucket) or any https URL, and the sa password is generated on the host.
    [switch] $NoInstanceProfile,
    [string] $RdpAllowedCidr = '',
    [string] $OwnerTag = '',
    [int] $IdleStopHours = 2,
    [string] $SaPasswordParameter = '/apipub-regression/sql-sa-password',

    # -Password
    [string] $KeyFile,
    # -Rdp
    [int] $LocalRdpPort = 13389,
    # -Destroy
    [switch] $DeleteSaPassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Command aws -ErrorAction SilentlyContinue)) { throw 'The AWS CLI v2 is required (https://aws.amazon.com/cli/).' }
# The Session Manager plugin installer adds its folder to the machine PATH; a shell opened before the install does not
# see it yet, so the default install location is added for this process.
$pluginFolder = Join-Path $env:ProgramFiles 'Amazon\SessionManagerPlugin\bin'
if (-not (Get-Command session-manager-plugin -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $pluginFolder 'session-manager-plugin.exe'))) { $env:Path = "$env:Path;$pluginFolder" }

$common = @()
if ($Region) { $common += @('--region', $Region) }
if ($Profile) { $common += @('--profile', $Profile) }

function Invoke-Aws
{
    <# Runs one AWS CLI call and returns the parsed JSON (or the raw text with -Text). Throws on a non-zero exit. #>
    param([Parameter(Mandatory)] [string[]] $Arguments, [switch] $Text)

    # stdout lines arrive as strings, stderr lines as ErrorRecords: keep CLI warnings out of the parsed result.
    $output = & aws @Arguments @common 2>&1
    $stdout = @($output | Where-Object { $_ -is [string] })
    $stderr = @($output | Where-Object { $_ -isnot [string] } | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0)
    {
        $message = ($stderr + $stdout) -join "`n"
        if ($message -match 'expired|Unable to locate credentials|Token has expired|aws login') { throw "The AWS session is not signed in: $message" }
        throw "aws $($Arguments -join ' ') failed ($LASTEXITCODE): $message"
    }
    if ($Text) { return ($stdout -join "`n").Trim() }

    $json = $stdout -join "`n"
    return $(if ($json.Trim()) { $json | ConvertFrom-Json } else { $null })
}

function Get-InstanceId
{
    $outputs = Invoke-Aws @('cloudformation', 'describe-stacks', '--stack-name', $StackName, '--query', 'Stacks[0].Outputs', '--output', 'json')
    $id = ($outputs | Where-Object { $_.OutputKey -eq 'InstanceId' }).OutputValue
    if (-not $id) { throw "Stack '$StackName' has no InstanceId output; is it deployed?" }

    return $id
}

function Get-Instance([string] $InstanceId)
{
    return Invoke-Aws @('ec2', 'describe-instances', '--instance-ids', $InstanceId, '--query', 'Reservations[0].Instances[0]', '--output', 'json')
}

function Test-InstanceProfile($Instance)
{
    return [bool] ($Instance.PSObject.Properties['IamInstanceProfile'] -and $Instance.IamInstanceProfile)
}

function Get-BucketRegion([string] $Bucket)
{
    $location = Invoke-Aws @('s3api', 'get-bucket-location', '--bucket', $Bucket, '--query', 'LocationConstraint', '--output', 'text') -Text
    return $(if (-not $location -or $location -eq 'None') { 'us-east-1' } else { $location })
}

function Wait-InstanceState([string] $InstanceId, [string] $State)
{
    Write-Host "  Waiting for the instance to be $State ..."
    Invoke-Aws @('ec2', "wait", "instance-$State", '--instance-ids', $InstanceId) -Text | Out-Null
}

function Enable-NestedVirtualization([string] $InstanceId)
{
    $instance = Get-Instance $InstanceId
    $cpu = $instance.CpuOptions
    if ($cpu.PSObject.Properties['NestedVirtualization'] -and $cpu.NestedVirtualization -eq 'enabled')
    {
        Write-Host '  Nested virtualization is already enabled.'
        return
    }

    Write-Host '  Enabling nested virtualization (the instance must be stopped for that) ...'
    if ($instance.State.Name -ne 'stopped')
    {
        Invoke-Aws @('ec2', 'stop-instances', '--instance-ids', $InstanceId) | Out-Null
        Wait-InstanceState $InstanceId 'stopped'
    }
    Invoke-Aws @('ec2', 'modify-instance-cpu-options', '--instance-id', $InstanceId, '--core-count', "$($cpu.CoreCount)", '--threads-per-core', "$($cpu.ThreadsPerCore)", '--nested-virtualization', 'enabled') | Out-Null
    Invoke-Aws @('ec2', 'start-instances', '--instance-ids', $InstanceId) | Out-Null
    Wait-InstanceState $InstanceId 'running'

    $after = (Get-Instance $InstanceId).CpuOptions
    if (-not ($after.PSObject.Properties['NestedVirtualization'] -and $after.NestedVirtualization -eq 'enabled'))
    {
        throw "CpuOptions still report '$($after | ConvertTo-Json -Compress)'; the instance type may not support nested virtualization in this region (aws ec2 describe-instance-types --instance-types $InstanceType --query 'InstanceTypes[].ProcessorInfo.SupportedFeatures')."
    }
    Write-Host '  Nested virtualization enabled.'
}

function Sync-RdpRule($Instance)
{
    <# Without an instance profile RDP is open to one address only (RdpAllowedCidr at deploy time). Workstation public
       IPs change (ISP DHCP, another network), which shows up as an RDP connection that times out while the instance is
       running, so -Rdp makes the port 3389 rule follow the current address: -RdpAllowedCidr, or this workstation's
       public IP/32. The stack parameter keeps the deploy-time value; the next -Deploy detects the address again. #>
    $cidr = $RdpAllowedCidr
    if (-not $cidr) { $cidr = "$((Invoke-RestMethod -Uri 'https://checkip.amazonaws.com' -TimeoutSec 30).Trim())/32" }
    $groupId = $Instance.SecurityGroups[0].GroupId
    $rules = @(Invoke-Aws @('ec2', 'describe-security-group-rules', '--filters', "Name=group-id,Values=$groupId", '--query', 'SecurityGroupRules[?IsEgress==`false` && FromPort==`3389`].{Id:SecurityGroupRuleId,Cidr:CidrIpv4}', '--output', 'json'))
    if ($rules.Cidr -contains $cidr) { return $cidr }

    foreach ($rule in $rules | Where-Object { $_.Cidr })
    {
        Write-Host "  RDP rule $($rule.Cidr) no longer matches this workstation; replacing it with $cidr."
        Invoke-Aws @('ec2', 'revoke-security-group-ingress', '--group-id', $groupId, '--security-group-rule-ids', $rule.Id) | Out-Null
    }
    $permission = @{ IpProtocol = 'tcp'; FromPort = 3389; ToPort = 3389; IpRanges = @(@{ CidrIp = $cidr; Description = 'RDP from the deployer address (no Systems Manager without an instance profile)' }) } | ConvertTo-Json -Depth 4 -Compress
    Invoke-Aws @('ec2', 'authorize-security-group-ingress', '--group-id', $groupId, '--ip-permissions', $permission) | Out-Null

    return $cidr
}

function Invoke-Deploy
{
    if (-not $KeyPairName) { throw '-KeyPairName is required for -Deploy (an EC2 key pair in the region; it decrypts the Administrator password).' }

    if (-not $VpcId)
    {
        $VpcId = Invoke-Aws @('ec2', 'describe-vpcs', '--filters', 'Name=is-default,Values=true', '--query', 'Vpcs[0].VpcId', '--output', 'text') -Text
        if (-not $VpcId -or $VpcId -eq 'None') { throw 'No default VPC in this region; pass -VpcId and -SubnetId.' }
    }
    if (-not $SubnetId)
    {
        $SubnetId = Invoke-Aws @('ec2', 'describe-subnets', '--filters', "Name=vpc-id,Values=$VpcId", 'Name=map-public-ip-on-launch,Values=true', '--query', 'Subnets[0].SubnetId', '--output', 'text') -Text
        if (-not $SubnetId -or $SubnetId -eq 'None') { throw "No subnet with public IPs in $VpcId; pass -SubnetId (it needs a route to the internet for Systems Manager and the downloads)." }
    }

    $repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
    if (-not $RepositoryBranch) { $RepositoryBranch = $(if ($repoRoot) { (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim() } else { 'main' }) }
    $profileMode = $(if ($NoInstanceProfile) { 'None' } elseif ($ExistingInstanceProfileName) { 'Existing' } else { 'Create' })
    if ($profileMode -eq 'None' -and -not $RdpAllowedCidr)
    {
        $RdpAllowedCidr = "$((Invoke-RestMethod -Uri 'https://checkip.amazonaws.com' -TimeoutSec 30).Trim())/32"
        Write-Host "  No instance profile: RDP will be allowed from this workstation only ($RdpAllowedCidr)."
    }

    if ($BootstrapBucket)
    {
        $local = Join-Path $PSScriptRoot 'bootstrap-host.ps1'
        $hash = (Get-FileHash -Algorithm SHA256 $local).Hash.Substring(0, 12).ToLowerInvariant()
        $s3Url = "s3://$BootstrapBucket/apipub-regression/$hash/bootstrap-host.ps1"
        Write-Host "  Uploading bootstrap-host.ps1 to $s3Url ..."
        Invoke-Aws @('s3', 'cp', $local, $s3Url, '--only-show-errors') -Text | Out-Null
        if ($profileMode -eq 'None')
        {
            # The instance cannot read S3 itself: a presigned URL, valid for 12 hours (or until the signing credentials expire,
            # whichever is first; the bootstrap downloads it on three boots within the first hour).
            $bucketRegion = Get-BucketRegion $BootstrapBucket
            $BootstrapUrl = Invoke-Aws @('s3', 'presign', $s3Url, '--expires-in', '43200', '--region', $bucketRegion) -Text
            $BootstrapBucket = ''
        }
        else { $BootstrapUrl = $s3Url }
    }
    elseif (-not $BootstrapUrl)
    {
        $commit = $(if ($repoRoot) { (& git -C $repoRoot rev-parse HEAD).Trim() } else { $RepositoryBranch })
        $BootstrapUrl = "https://raw.githubusercontent.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/$commit/eng/regression/aws/bootstrap-host.ps1"
        Write-Host "  Bootstrap URL: $BootstrapUrl"
        Write-Host '  (the commit must be pushed; for an unpushed branch pass -BootstrapBucket <bucket> or -BootstrapUrl <reachable copy>)'
    }

    # A direct lookup: describe-parameters with a Name filter answers 'None' for a missing parameter only in some CLI
    # versions, and an empty string in others. Without an instance profile the host generates the password itself.
    $existing = $true
    if ($profileMode -ne 'None')
    {
        try { Invoke-Aws @('ssm', 'get-parameter', '--name', $SaPasswordParameter, '--query', 'Parameter.Name', '--output', 'text') -Text | Out-Null }
        catch { if ("$_" -match 'ParameterNotFound') { $existing = $false } else { throw } }
    }
    if (-not $existing)
    {
        Write-Host "  Creating the SQL Server sa password in SSM ($SaPasswordParameter) ..."
        $bytes = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $generated = ([Convert]::ToBase64String($bytes) -replace '[+/=]', 'x') + 'Aa1!'
        Invoke-Aws @('ssm', 'put-parameter', '--name', $SaPasswordParameter, '--type', 'SecureString', '--value', $generated, '--description', 'API Publisher regression host: SQL Server sa') | Out-Null
    }

    $template = Join-Path $PSScriptRoot 'regression-host.yaml'
    Write-Host "Deploying stack $StackName ($InstanceType, $VolumeSizeGiB GiB, subnet $SubnetId) ..."
    $overrides = @(
        "InstanceType=$InstanceType", "VolumeSizeGiB=$VolumeSizeGiB", "KeyPairName=$KeyPairName", "VpcId=$VpcId", "SubnetId=$SubnetId",
        "AmiId=$AmiParameter", "BootstrapUrl=$BootstrapUrl", "BootstrapBucket=$BootstrapBucket", "RepositoryBranch=$RepositoryBranch", "SaPasswordParameterName=$SaPasswordParameter",
        "IdleStopHours=$IdleStopHours", "OwnerTag=$OwnerTag", "InstanceProfileMode=$profileMode", "ExistingInstanceProfileName=$ExistingInstanceProfileName",
        "RdpAllowedCidr=$RdpAllowedCidr"
    )
    $deployArgs = @('cloudformation', 'deploy', '--template-file', $template, '--stack-name', $StackName, '--capabilities', 'CAPABILITY_IAM', '--no-fail-on-empty-changeset', '--parameter-overrides') + $overrides
    Invoke-Aws $deployArgs -Text | Write-Host

    $instanceId = Get-InstanceId
    Wait-InstanceState $instanceId 'running'
    Enable-NestedVirtualization $instanceId

    Write-Host ''
    Write-Host "Instance $instanceId is up. The bootstrap takes 30 to 45 minutes and reboots twice; follow it with:"
    Write-Host "  .\Invoke-RegressionHost.ps1 -Status"
    if ($profileMode -eq 'None')
    {
        $publicIp = (Get-Instance $instanceId).PublicIpAddress
        Write-Host "No instance profile: no Systems Manager. RDP directly to $publicIp (allowed from $RdpAllowedCidr); -Rdp opens mstsc on it."
        Write-Host "The bootstrap log is C:\regression-bootstrap\bootstrap.log on the host; the SQL Server sa password is C:\regression-bootstrap\sa-password.txt."
    }
    else { Write-Host "Then sign in with -Rdp (password: -Password -KeyFile <the key pair's .pem>)." }
}

function Invoke-Status
{
    $instanceId = Get-InstanceId
    $instance = Get-Instance $instanceId
    $cpu = $instance.CpuOptions
    $nested = $(if ($cpu.PSObject.Properties['NestedVirtualization']) { $cpu.NestedVirtualization } else { 'unknown' })
    Write-Host "Stack      $StackName"
    Write-Host "Instance   $instanceId  $($instance.InstanceType)  $($instance.State.Name)  launched $($instance.LaunchTime)"
    Write-Host "CPU        $($cpu.CoreCount) cores x $($cpu.ThreadsPerCore) threads; nested virtualization: $nested"
    if ($instance.State.Name -ne 'running') { return }
    if (-not (Test-InstanceProfile $instance))
    {
        Write-Host "Access     no instance profile, so no Systems Manager: RDP to $($instance.PublicIpAddress) and read C:\regression-bootstrap\bootstrap.log there."
        return
    }

    $ssm = Invoke-Aws @('ssm', 'describe-instance-information', '--filters', "Key=InstanceIds,Values=$instanceId", '--query', 'InstanceInformationList[0].PingStatus', '--output', 'text') -Text
    Write-Host "SSM agent  $ssm"
    if ($ssm -ne 'Online') { return }

    $script = 'if (Test-Path C:\regression-bootstrap\stage.txt) { "stage: " + (Get-Content C:\regression-bootstrap\stage.txt -Raw).Trim() } else { "stage: (not started)" }; if (Test-Path C:\regression-bootstrap\bootstrap.log) { Get-Content C:\regression-bootstrap\bootstrap.log -Tail 15 }'
    $commandId = Invoke-Aws @('ssm', 'send-command', '--instance-ids', $instanceId, '--document-name', 'AWS-RunPowerShellScript', '--parameters', "commands=[$($script | ConvertTo-Json)]", '--query', 'Command.CommandId', '--output', 'text') -Text
    $deadline = (Get-Date).AddSeconds(90)
    do
    {
        Start-Sleep -Seconds 3
        $invocation = Invoke-Aws @('ssm', 'get-command-invocation', '--command-id', $commandId, '--instance-id', $instanceId, '--output', 'json')
    } while ($invocation.Status -in @('Pending', 'InProgress', 'Delayed') -and (Get-Date) -lt $deadline)
    Write-Host "Bootstrap  ($($invocation.Status))"
    ($invocation.StandardOutputContent -split "`n") | ForEach-Object { if ($_.Trim()) { Write-Host "  $_" } }
    if ($invocation.StandardErrorContent) { Write-Host "  stderr: $($invocation.StandardErrorContent)" }
}

function Invoke-SessionManagerPortForward([string] $InstanceId, [int] $RemotePort, [int] $LocalPort)
{
    if (-not (Get-Command session-manager-plugin -ErrorAction SilentlyContinue)) { throw 'The Session Manager plugin is required for port forwarding: https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html' }
    $parameters = "portNumber=$RemotePort,localPortNumber=$LocalPort"
    Write-Host "Forwarding localhost:$LocalPort -> $InstanceId`:$RemotePort through Session Manager (Ctrl+C ends it) ..."
    & aws ssm start-session --target $InstanceId --document-name AWS-StartPortForwardingSession --parameters $parameters @common
}

switch ($PSCmdlet.ParameterSetName)
{
    'Deploy' { Invoke-Deploy }
    'Start'
    {
        $id = Get-InstanceId
        Invoke-Aws @('ec2', 'start-instances', '--instance-ids', $id) | Out-Null
        Wait-InstanceState $id 'running'
        Write-Host "Instance $id is running (Docker Desktop starts when someone signs in over RDP)."
    }
    'Stop'
    {
        $id = Get-InstanceId
        Invoke-Aws @('ec2', 'stop-instances', '--instance-ids', $id) | Out-Null
        Wait-InstanceState $id 'stopped'
        Write-Host "Instance $id is stopped; only the volume is billed now."
    }
    'Status' { Invoke-Status }
    'Password'
    {
        if (-not $KeyFile) { throw "-KeyFile is required: the private key (.pem) of the key pair the stack was deployed with." }
        $id = Get-InstanceId
        $value = Invoke-Aws @('ec2', 'get-password-data', '--instance-id', $id, '--priv-launch-key', (Resolve-Path $KeyFile).Path, '--query', 'PasswordData', '--output', 'text') -Text
        if (-not $value) { throw 'The password is not available yet (Windows takes a few minutes after the first boot to publish it).' }
        Write-Host "Administrator password: $value"
    }
    'Rdp'
    {
        $id = Get-InstanceId
        $instance = Get-Instance $id
        if (-not (Test-InstanceProfile $instance))
        {
            if ($instance.State.Name -ne 'running') { throw "The instance is $($instance.State.Name); start it with -Start first." }
            $cidr = Sync-RdpRule $instance
            Write-Host "No instance profile: connecting straight to $($instance.PublicIpAddress), allowed from $cidr (user Administrator; password from -Password). Disconnect, do not sign out, while a run is in progress."
            if ($IsWindows) { Start-Process mstsc.exe -ArgumentList "/v:$($instance.PublicIpAddress)" }
            return
        }
        $job = Start-Job -ScriptBlock {
            param($InstanceId, $LocalPort, $Common)
            & aws ssm start-session --target $InstanceId --document-name AWS-StartPortForwardingSession --parameters "portNumber=3389,localPortNumber=$LocalPort" @Common
        } -ArgumentList $id, $LocalRdpPort, $common
        Start-Sleep -Seconds 4
        if ($job.State -eq 'Failed') { Receive-Job $job; throw 'Session Manager port forwarding failed (is the Session Manager plugin installed and the instance Online in SSM?).' }
        Write-Host "RDP tunnel on localhost:$LocalRdpPort (user Administrator; password from -Password). Disconnect, do not sign out, while a run is in progress."
        if ($IsWindows) { Start-Process mstsc.exe -ArgumentList "/v:localhost:$LocalRdpPort" }
        Write-Host 'Press Ctrl+C to close the tunnel.'
        Receive-Job $job -Wait
    }
    'PortForward'
    {
        $id = Get-InstanceId
        if (-not (Test-InstanceProfile (Get-Instance $id))) { throw 'Port forwarding needs Systems Manager, which needs an instance profile; this host has none. Use the RDP session and a browser on the host instead.' }
        Invoke-SessionManagerPortForward $id $PortForward $PortForward
    }
    'Destroy'
    {
        Write-Host "Deleting stack $StackName (instance and volume included) ..."
        Invoke-Aws @('cloudformation', 'delete-stack', '--stack-name', $StackName) | Out-Null
        Invoke-Aws @('cloudformation', 'wait', 'stack-delete-complete', '--stack-name', $StackName) -Text | Out-Null
        if ($DeleteSaPassword) { Invoke-Aws @('ssm', 'delete-parameter', '--name', $SaPasswordParameter) | Out-Null }
        Write-Host 'Done.'
    }
}
