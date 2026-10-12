# Regression host on AWS

One Windows Server EC2 instance in the Ed-Fi Alliance account that runs the regression harness the same way a workstation does: Docker Desktop for the arms, the host SQL Server for Northridge, PowerShell 7 and the publisher's Release build. It exists so the long runs (items 3 and 4 on Northridge, the arm A/C/D passes) no longer depend on anyone's laptop, and so a second person can run the runbook independently (APIPUB-156). It is **not** the CI job of APIPUB-149; that job would reuse this host as a self-hosted runner later.

## What gets created

`regression-host.yaml` (CloudFormation):

| Resource | Purpose |
| --- | --- |
| EC2 instance, `r7i.2xlarge` (8 vCPU, 64 GB), 300 GB gp3, Windows Server 2025 | the host; the family must support nested virtualization, or Hyper-V/WSL2 (hence Docker Desktop) cannot start |
| IAM role + instance profile | `AmazonSSMManagedInstanceCore` (Session Manager, Run Command) and read access to one SSM parameter (the SQL Server `sa` password) |
| Security group | **no inbound rules**; everything (RDP, Swagger, WireMock admin) is tunnelled through Session Manager |
| CloudWatch alarm | stops the instance after 2 h of average CPU below 5 % (a forgotten box); long runs stay far above it |

The instance bootstraps itself from `bootstrap-host.ps1` (downloaded by the user data on every boot until it reports `done`): Hyper-V + WSL 2, Chocolatey, PowerShell 7, Git, GitHub CLI, 7-Zip, AWS CLI, `sqlcmd`, the .NET 10 SDK, SQL Server 2022 Developer (database engine only, mixed mode, TCP on), Docker Desktop (WSL 2 backend, unattended), a `.wslconfig` that caps Docker's VM at 32 GB and 6 processors, and clones of `Ed-Fi-API-Publisher` and `Data-Management-Service` under `C:\GIT\Ed-Fi\` (the same layout as the laptop, so every path in the runbook holds). It reboots twice and takes 30 to 45 minutes.

**Nested virtualization** is what makes a Windows EC2 instance able to run Hyper-V and WSL 2. AWS supports it on the M7i/M8i/C7i/C8i/R7i/R8i families at no extra cost, but CloudFormation cannot switch it on yet, so `Invoke-RegressionHost.ps1 -Deploy` does it after the stack is created: stop, `modify-instance-cpu-options --nested-virtualization enabled`, start. The bootstrap refuses to install anything past the Windows features while the hypervisor is not running.

**Docker Desktop on Windows Server** is not supported by Docker (its requirements name Windows 10 and 11). It installs and runs on Server 2022/2025 with the WSL 2 backend, which is what this host relies on. If a Docker Desktop release stops working there, the fallback is Docker Engine inside a WSL distribution with the harness run from `pwsh` inside WSL; that path needs two harness changes (a Linux native-library lookup in `eng/Compare-PagingParity.ps1` for item 2, and a `0.0.0.0` bind for the Northridge port) that are deliberately not made today.

## Cost (us-east-1 on-demand, approximate; check the region you deploy in)

| State | Cost |
| --- | --- |
| Running | about $0.90 per hour (`r7i.2xlarge` Linux price plus the Windows licence) |
| Stopped | about $24 per month for the 300 GB volume; nothing else |
| 8 h a day, 22 days | about $160 per month |
| Left running all month | about $650 per month, which is what the idle alarm prevents |

Stop the instance when a run is over (`-Stop`). Starting it again takes about two minutes; nothing on it is lost.

## Who needs which permissions

- **Deploying** (once, and for template changes): CloudFormation, EC2 (instances, security groups, CPU options), IAM (create the instance role and profile), CloudWatch alarms, SSM `PutParameter`.
- **Running the regression** (you, Ana, anyone with the runbook): `ec2:DescribeInstances`, `ec2:StartInstances`/`ec2:StopInstances` on the instance, `ec2:GetPasswordData`, `ssm:StartSession` on the instance with the `AWS-StartPortForwardingSession` document, `ssm:SendCommand` with `AWS-RunPowerShellScript` for `-Status`, `cloudformation:DescribeStacks`. On the host itself each person gets a Windows local user in `Administrators` and `docker-users` (`New-LocalUser`, `Add-LocalGroupMember`), created by whoever deployed it; Docker Desktop runs per signed-in user.

## Without IAM permissions (no instance profile)

Both accounts tried on 2026-10-01 gave the deployer a permission set that cannot create IAM roles and cannot pass one to an instance (`PowerUserAccess` in the Alliance account denies `iam:PassRole`). Until an administrator provides an instance profile, deploy with `-NoInstanceProfile`:

- no Systems Manager: the security group opens **RDP from your current public IP only** (`-RdpAllowedCidr` to override). `-Rdp` connects straight to the public IP and first points that rule at the public IP you have now, so a changed workstation address (the usual reason an RDP connection times out while the instance is running) fixes itself. `-Status` shows only the instance state and `-PortForward` is unavailable (use a browser on the host);
- the bootstrap script is fetched through a **presigned S3 URL** (`-BootstrapBucket`, 12 hours, also bounded by your session's credential lifetime) or any https URL;
- the SQL Server `sa` password is **generated on the host** into `C:\regression-bootstrap\sa-password.txt` (Administrators only); the runbook never needs it.

```powershell
.\Invoke-RegressionHost.ps1 -Deploy -NoInstanceProfile -KeyPairName apipub-regression -BootstrapBucket <bucket> -OwnerTag you@example.org
```

When an administrator later creates the role (`AmazonSSMManagedInstanceCore`, `ssm:GetParameter` on `/apipub-regression/sql-sa-password`, `s3:GetObject` on the bucket) and grants you `iam:PassRole` on it, redeploy with `-ExistingInstanceProfileName <profile>`; CloudFormation replaces the instance, so copy the evidence off first.

## Day-to-day

```powershell
cd eng/regression/aws
aws login                                    # or aws sso login / credentials; the scripts use the default profile and region unless -Profile/-Region are given

# Once: a key pair in the region (it decrypts the Administrator password). Write the key with WriteAllText: piping the
# CLI output through Set-Content -NoNewline joins the PEM onto one line and the decryption fails.
[IO.File]::WriteAllText("$HOME\.ssh\apipub-regression.pem", (aws ec2 create-key-pair --key-name apipub-regression --key-type rsa --key-format pem --query KeyMaterial --output text | Out-String))

# Once: create the host. The commit must be pushed, because the instance downloads bootstrap-host.ps1 from GitHub
# (or pass -BootstrapBucket / -BootstrapUrl with another reachable copy).
.\Invoke-RegressionHost.ps1 -Deploy -KeyPairName apipub-regression -OwnerTag you@example.org

.\Invoke-RegressionHost.ps1 -Status          # state, nested virtualization, bootstrap stage and log tail
.\Invoke-RegressionHost.ps1 -Password -KeyFile ~\.ssh\apipub-regression.pem
.\Invoke-RegressionHost.ps1 -Rdp             # tunnel 3389 -> localhost:13389 and open mstsc (needs the Session Manager plugin)
.\Invoke-RegressionHost.ps1 -PortForward 8202   # arm B Swagger (or 8280 WireMock admin, 8002 Northridge Swagger) in your browser
.\Invoke-RegressionHost.ps1 -Stop
.\Invoke-RegressionHost.ps1 -Start
.\Invoke-RegressionHost.ps1 -Destroy [-DeleteSaPassword]
```

On the host, in the RDP session: start Docker Desktop once if it is not running (it autostarts at sign-in), open PowerShell 7, and follow the runbook in `../README.md` ("Running on the AWS host"). Rules that matter on a shared remote box:

- **Disconnect, do not sign out**, while a run is in progress: Docker Desktop and the running `pwsh` live in your session.
- **Stop the instance when you are done.** The idle alarm is a backstop, not the plan.
- Pull the branch you are testing before a run (`git fetch; git checkout <branch>; git pull`) and commit the result rows from the host with your own GitHub account (`gh auth login`), or copy the rows back by hand.
- The host's SQL Server `sa` password is in SSM (`/apipub-regression/sql-sa-password`); the Windows login is `sysadmin` on the instance, so the Northridge script needs neither.

## Operational notes

- `-Status` reads `C:\regression-bootstrap\stage.txt` and the bootstrap log through Run Command; the user-data transcript is `C:\regression-bootstrap\userdata.log`. A failed stage is retried on the next boot (`Restart-Computer` from an SSM session, or `-Stop`/`-Start`).
- The instance has a public IP (the default VPC's subnets need it to reach the internet) but no inbound rules; do not add any for RDP, use the tunnel.
- Everything on the host, including `results/runs` evidence and the Northridge database, lives on the single volume; `-Destroy` deletes it. Copy what you need first.
- Session Manager plugin for the workstation: <https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html>.
- Docker Desktop licence: free (Docker Personal) for organizations under 250 employees and under $10 million in annual revenue; otherwise a subscription is needed for the user who signs in.
