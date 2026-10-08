# API Publisher regression harness

Scripted, repeatable execution of the release regression: the twelve items of [APIPUB-125](https://edfi.atlassian.net/browse/APIPUB-125) across the supported ODS/API arms, item 13 for the README key scenarios the ticket's task names, plus the five DMS items of [APIPUB-146](https://edfi.atlassian.net/browse/APIPUB-146). Built for v1.4 as a workstation runbook (Phase 1 on the [Confluence plan](https://edfi.atlassian.net/wiki/x/BwDbpw)); the item scripts take every connection detail as a parameter and exit non-zero on failure, so the v1.5 GitHub Actions job ([APIPUB-149](https://edfi.atlassian.net/browse/APIPUB-149)) wraps them without rewriting.

## Prerequisites

- Windows or Linux workstation with **PowerShell 7.4+** (`pwsh`; the scripts refuse Windows PowerShell 5.1, which strips the quotes the SQL identifiers need) and **Docker Desktop** (Compose v2), or Docker Engine on Linux. Around 6 GB of RAM free per running ODS arm.
- Network access to Docker Hub (`edfialliance/*`, `wiremock/wiremock`).
- The publisher under test, one of:
  - a local build: the **.NET 10 SDK**, `dotnet build -c Release`, then `src/EdFi.Tools.ApiPublisher.Cli/bin/Release/net10.0/EdFiApiPublisher.exe` (`-PublisherPath`);
  - the release candidate nupkg from `build.ps1 -Command Package` (`-PublisherPackage`; extracted and run as a local build, the Win64 exe on Windows and the framework-dependent dll elsewhere, which needs the .NET 10 runtime);
  - a Docker image, `edfialliance/ods-api-publisher:<tag>` or one built from `src/Dockerfile` with the RC version (`-PublisherImage`).
- Item 9 compares against the published `edfialliance/ods-api-publisher:v1.3.0` image (pulled on first use).
- Free host ports, all bound to 127.0.0.1: 8101-8103, 8180, 5401 (arm A); 8201-8203, 8280, 5402 (arm B); 8301-8303, 8380, 5403 (arm C).
- For arm D, the [Data-Management-Service](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service) repository checked out next to this one (see `arms/arm-d-dms.md`).
- For the Northridge long runs (items 3 and 4), a SQL Server instance on the Windows host with about 50 GB free, `sqlcmd` and 7-Zip (see "Northridge source"). Not needed for the arms.
- No workstation at hand: `aws/` creates a Windows host in AWS with all of the above installed (see `aws/README.md` and "Running on the AWS host").

## Quick start

```powershell
cd eng/regression

# 1. Bring an arm up (pulls images, clones the ODS templates, creates the API clients). About 5 minutes the first time.
.\Start-Arm.ps1 -Arm B

# 2. Run the short items against the release candidate and append the rows to results/results-1.4.0.md.
#    Items that need the other kind of publisher (3 and 9 an image, 2 a local build) are skipped with the reason.
.\Invoke-Regression.ps1 -Arms B -Items 1,2,5,6,7,8,10,11,12,13 -Version 1.4.0 `
    -PublisherPackage ..\..\EdFi.ApiPublisher.1.4.0.nupkg -SkipArmStart

# 3. The container-only items (memory limit, v1.3 baseline comparison).
.\Invoke-Regression.ps1 -Arms B -Items 3,9 -Version 1.4.0 -PublisherImage edfialliance/ods-api-publisher:pre -SkipArmStart

# 4. Other arms; the driver starts them itself.
.\Invoke-Regression.ps1 -Arms A,C -Items 1,2,5,6,10,12,13 -Version 1.4.0 -PublisherPath <exe>

# One item by hand, with its own parameters:
.\items\12-throttling.ps1 -Arm B -PublisherPath <exe> -MaxConcurrent 2 -RetryAfterSeconds 5

# Parameters for some items only: each goes to the items that declare it.
.\Invoke-Regression.ps1 -Arms B -Items 3,4,12 -PublisherImage <tag> -ItemArguments '-FaultAfterSeconds', '30'

# Tear down.
.\Start-Arm.ps1 -Arm B -Down          # keeps the database volumes
.\Start-Arm.ps1 -Arm B -Down -Purge   # removes them too
```

The arm definitions (`arms/arm-*.env`) are committed, so a clean clone needs no copy step. Each item leaves its logs, memory samples, count reports and proxy journal (Authorization headers redacted) under `results/runs/<timestamp>-<item>-<arm>/` (git-ignored) and appends one row to the results file, which is committed for the release. Every row names the publisher it ran (image digest, or product version and nupkg), and the driver fills in the results file's "Release candidate under test" line on its first run. An item that stops on an error still writes a FAIL row with the error and where it was raised. Long runs print a heartbeat every five minutes.

## Layout

```text
eng/regression/
  README.md                      this file
  Invoke-Regression.ps1          driver: -Arms A,B,C,D -Items 1..13,d1..d5 -PublisherPath|-PublisherPackage|-PublisherImage -Version
  Start-Arm.ps1                  up / -ResetTarget / -ResetSource / -Down [-Purge] for one arm
  Start-NorthridgeSource.ps1     the Northridge source for the long runs: host database restore + the source API stack on 8001
  arms/
    ods-arm.yml                  one compose file for every ODS arm (source API, target API, two databases, proxy)
    arm-a.env, arm-b.env, arm-c.env   image tag, ports, credentials per arm; the only per-arm difference (committed)
    arm-d.env                    where the externally started DMS stack listens
    arm-<x>.local.env            optional, git-ignored: per-workstation overrides of single keys (Northridge source, DMS client)
    arm-d-dms.md                 how to start the DMS with Keycloak or self-contained auth
    bootstrap-pgsql.sql          Admin bootstrap: two ODS instances, two API clients (psql variables from the .env)
    northridge/                  Northridge source stack: northridge.yml + northridge.env (+ .local.env), the Admin DB SQL Server
                                 build context, the host restore/login SQL and the Admin bootstrap (sqlcmd variables)
  aws/
    regression-host.yaml, bootstrap-host.ps1, Invoke-RegressionHost.ps1, README.md   a Windows EC2 host that runs all of this
  proxy/
    README.md, mappings/         WireMock fault definitions loaded on demand (500, 401, token outage, 429, invalid id, pre-7 stub)
  items/
    01-baseline.ps1 .. 13-scenarios.ps1, d1-discovery.ps1 .. d5-change-version.ps1
  lib/
    Regression.psm1              arms, API helpers, publisher runner (exe or container), proxy control, comparisons, results
    publisher-config/            the settings files a container run mounts (shipped defaults, Debug logging template)
  results/
    results-1.4.0.md             one row per item and arm; runs/ holds the per-run evidence (git-ignored)
```

## Arms

| Arm | Platform | Image tag (`edfialliance/ods-api-web-api`, `-db-ods-sandbox`, `-db-admin`) | Ports (source / target / proxy / admin db) |
| --- | --- | --- | --- |
| A | ODS/API 7.1 on Data Standard 4.0.0 | `7.1-4.0.0` | 8101 / 8103 / 8180 / 5401 |
| B | ODS/API 7.3.2 on Data Standard 5.2.0 | `7.3.2-5.2.0` | 8201 / 8203 / 8280 / 5402 |
| C | ODS/API 7.3.2 on Data Standard 6.1.0 | `7.3.2_6447-6.1.0` (no plain `7.3.2-6.1.0` is published) | 8301 / 8303 / 8380 / 5403 |
| D | Ed-Fi DMS 8.x with Keycloak, ODS/API 7.3.2 source | DMS repo `eng/docker-compose` | see `arm-d.env` |

Every ODS arm is two API containers over one shared Admin database: the source serves the Grand Bend populated template, the target the minimal template, each through its own API client. Both clients are associated with every top-level education organization the source template contains (read from the ODS at bootstrap time), because the TPDM sample data hangs off organizations outside the Grand Bend LEA and a client limited to the LEA cannot read them. Isolation is off (`--ignoreIsolation=true`) on every run because the images have no snapshot connection; the runner adds the switch unless an item sets it. The WireMock proxy sits in front of the source API and is transparent until an item enables a fault. The ports leave the Northridge stack (8001/8003) and the 7.1 side stack (8011) untouched, so all can run side by side.

To point the functional items at another source (for example Northridge for the long runs), put the `SOURCE_*` values in `arms/arm-<x>.local.env`: `Get-Arm` layers it over the committed file (and hands docker compose a merged copy), so the committed definition stays as it is. Nothing else reads the machine's state.

### Northridge source

The memory and token-lifetime long runs (items 3 and 4) need the 10.6 M document Northridge dataset. It is not an arm: `Start-NorthridgeSource.ps1` serves it as a **source only** on `127.0.0.1:8001`, and the publisher's target stays arm B's. `arms/northridge/` holds one compose file (`northridge.yml`: the ODS/API 7.3.2 `-mssql` image for Data Standard 5.2.0 as `api-source`, an Admin/Security SQL Server Express container built from the upstream `ods-api-db-admin` mssql context because Docker Hub publishes no `-mssql` tag for it, optional Swagger) and `northridge.env` (+ git-ignored `northridge.local.env` overlay). The database itself lives on the **host SQL Server**: the script restores it from the public backup (`EdFi_Ods_Northridge_v73_20241218.7z`, 730 MB download, about 17 GB restored; downloaded into `arms/northridge/.backups`, git-ignored) when it does not exist, applies `fix-northridge-gradingperiodname.sql` once (the backup ships empty `GradingPeriodName` keys the target API rejects), creates or re-passwords the `edfi_docker` login the container uses, turns on mixed authentication and TCP 1433 if needed, adds a firewall rule for the local subnet, and sets `max server memory` when `-SqlMaxMemoryMB` is given. The backup has no `changes` or `tpdm` schema, so the API runs without TPDM and Change Queries and the publisher reads it with `--ignoreIsolation=true` (the harness default).

```powershell
cd eng/regression
.\Start-Arm.ps1 -Arm B                              # the target, first, and with no arm-b.local.env overlay in place
.\Start-NorthridgeSource.ps1                        # elevated PowerShell 7, Windows login that is sysadmin on the instance; ~20 min the first time
# paste the lines the script printed into arms/arm-b.local.env (never into arm-b.env itself):
#   SOURCE_PORT=8001  SOURCE_KEY=northridgeKey  SOURCE_SECRET=northridgeSecret  PROXY_FORWARD_URL=http://host.docker.internal:8001
.\Invoke-Regression.ps1 -Arms B -Items 3 -PublisherImage <tag> -SkipArmStart -Version 1.4.0
.\Invoke-Regression.ps1 -Arms B -Items 4 -PublisherPath <exe> -SkipArmStart -Version 1.4.0
# afterwards: delete arms/arm-b.local.env (or its SOURCE_* lines) and
.\Start-NorthridgeSource.ps1 -Down                  # containers off; the host database stays (-Purge also drops the Admin volumes)
```

Arm B goes first because `Start-NorthridgeSource.ps1` also associates arm B's target client with the Northridge education organizations (`NORTHRIDGE_ED_ORGS`, by `arms/northridge/grant-arm-target-pgsql.sql`) and restarts arm B's target API: arm B's own bootstrap associates its clients only with Grand Bend's organizations, and without the grant the target refuses every Northridge document with 403 "No relationships have been established". Run the Northridge script again after `Start-Arm.ps1 -Arm B -Purge`, which recreates arm B's Admin database. `-SkipArmStart` matters: starting arm B with the overlay in place would publish arm B's own source API on 8001 too, and Northridge could not bind its port. Items 3 and 4 read through arm B's proxy, which `PROXY_FORWARD_URL` points at the Northridge API; everything else in the items is unchanged. Keep the host quiet during the run: an earlier laptop run starved the host SQL Server when RAM was low (page reads hit the API's 30 s SQL timeout). The host SQL Server instance can be anything from 2019 up; the laptop used 2025 Developer, the AWS host 2022 Developer.

## Items

| # | Script | Arms | Publisher | Mechanism | Duration |
| --- | --- | --- | --- | --- | --- |
| 1 | `01-baseline` | A, B, C | exe or image | full publish, offset/limit; one student edited; incremental publish from the recorded change version | short |
| 2 | `02-cursor-parity` | A, B, C | exe | `eng/Compare-PagingParity.ps1`; arm A must fall back to offset/limit with the log entry | short |
| 3 | `03-memory-long-run` | B | image | container with `--memory`; proxy 500 window mid-run; offset then cursor; no steady memory growth under the limit; the proxy journal shows every page read exactly once | long |
| 4 | `04-token-lifetime` | B | exe or image | long run over two source refresh intervals (interval = half the token lifetime); current token invalidated mid-run (401 replayed with a fresh token); token endpoint outage with the token invalidated (re-acquisition attempts in the journal, then Fatal); bad credentials | long |
| 5 | `05-exit-code` | A, B, C | exe or image | persistent 500 on one leaf resource (`studentGradebookEntries`); non-zero exit, error line, source read error in the run summary and the error record, everything else published | short |
| 6 | `06-invalid-id` | A, B, C | exe or image | stubbed `educationContents` page, five invalid id shapes, target reset per shape, PostgreSQL store; error line with locator, siblings published, locator matches the proxy journal, error record without body or id contents, last change version not advanced | short |
| 7 | `07-ds5-contacts` | B | exe or image | shipped configuration, contacts streamed and counted | short |
| 8 | `08-deletes-keychanges` | B | exe or image | two rounds of one calendar date deleted and one class period re-keyed on the source: a full publish with `--processDeletesAndKeyChangesOnFullPublish` (APIPUB-113), then an incremental publish; each removes and renames on the target | short |
| 9 | `09-discovery-regression` | B | image | `ods-api-publisher:v1.3.0` vs the RC at Debug level; request URL shapes and counts must be identical | short |
| 10 | `10-config-store` | A, B, C | exe or image | PostgreSQL configuration store in the arm's Admin database; publish by connection name; the source's newest change version recorded for the target | short |
| 11 | `11-resume` | B | exe or image | run A killed after N pages of a resource; run B `--resumeLastRun=true` restarts at least one partition at a confirmed page and reads fewer pages than an uninterrupted reference run C; state removed | medium |
| 12 | `12-throttling` | A, B, C | exe or image | proxy 429 with Retry-After window under `--maxConcurrentSourceRequests`; every retry at least Retry-After after its 429 (journal); journal peak within the cap | short |
| 13 | `13-scenarios` | A, B, C | exe or image | README scenarios: `--exclude` (resource and dependents not published), change version paging (several windows in the journal), low and high parallelism; counts match in each | medium |
| D1 | `d1-discovery` | D | exe or image | Debug publish into the DMS; every target URL starts with a URL the Discovery document advertises | short |
| D2 | `d2-keycloak` | D | exe or image | token from the Discovery oauth URL is a Keycloak (realm) token; the publisher took its target token endpoint from Discovery; refresh interval line | short (long with item 4) |
| D3 | `d3-full-publish` | D | exe or image | target reset and required empty, full publish, ODS source vs DMS counts | short on Grand Bend |
| D4 | `d4-self-contained` | D | exe or image | DMS without Keycloak; publish completes | short |
| D5 | `d5-change-version` | D | exe or image | incremental publish into the DMS, or the failure text as the documented limitation | short |

`Invoke-Regression.ps1` skips an item on an arm it does not apply to, or when it needs the other kind of publisher, and says why (`SKIP`, `SKIP (publisher)`). Items without an entry in its `$applicability` table run on every ODS arm, a new one included. A run in which nothing ran exits 1. Item parameters (fault timing, page sizes, memory limit, scenarios) are documented in each script's header and can be forwarded from the driver with `-ItemArguments`; each parameter goes only to the items that declare it.

## Conventions that keep Phase 2 a wrapper

- **Parameters, not machine state.** An item receives the arm name and the publisher under test; URLs, keys and ports come from `arms/*.env`. No item reads `docker ps`, a developer profile or a hardcoded path.
- **One result row, one exit code.** Every item ends with `Complete-Item`, which appends the row and returns 0 or 1; the driver runs items as child processes so a kill trigger or an exception in one item cannot stop the run.
- **Evidence next to the log.** Whatever an assertion used (count CSV, proxy journal, URL diff, memory samples, run state) is written into the run folder so a reviewer can re-check a row without re-running.
- **The driver is the only thing that knows about arms.** The v1.5 `workflow_dispatch` job passes a version and an arm matrix to `Invoke-Regression.ps1`; nothing else changes.

## Adding an arm or an item

- **ODS arm:** copy `arm-b.env`, change `COMPOSE_PROJECT_NAME`, `ODS_IMAGE_TAG`, the ports and `TPDM_ENABLED`; `Start-Arm.ps1 -Arm <name>` does the rest. A SQL Server arm would need a `-mssql` image tag and a SQL Server variant of `bootstrap-pgsql.sql`.
- **Item:** copy the closest script; keep the parameter block (`-Arm`, `-PublisherPath`, `-PublisherImage`, `-ResultsFile`, `-RunRoot`) and the `trap` line after `$item`; use `Assert-Condition` for every check and end with `exit (Complete-Item ...)`. Add the arms it applies to in the `$applicability` table of the driver if it is not every ODS arm, and to `$requiredMode` if it needs one kind of publisher.
- **Fault:** add a mapping under `proxy/mappings/` with `__PLACEHOLDER__` tokens and enable it from the item with `Enable-ProxyFault`.

## Manual residue (what stays a workstation task)

- **Items 3 and 4 on Northridge.** The memory and token-lifetime long runs need hours; the source is scripted (`Start-NorthridgeSource.ps1`, see "Northridge source") but the run is still started by hand, overnight, with the `arm-b.local.env` overlay and `-SkipArmStart`, and the overlay removed afterwards. Item 3's memory growth check needs at least 40 samples after the warm-up, so on Grand Bend it only warns.
- **Item 4 dry run.** With the ODS default 30-minute token the refresh interval is 15 minutes; set `SOURCE_TOKEN_TIMEOUT_MINUTES=2` in `arms/arm-b.local.env` and restart the arm (`Start-Arm.ps1 -Arm B`) to exercise the refresh scenarios in minutes.
- **Remediations** (`--remediationsScriptFile`). Not scripted: they need Node.js next to the publisher (the image has none), and a healthy ODS target gives no failure that a remediation plan is needed for, because the publisher resolves missing references itself. The unit and integration tests (`RemediationTests`, `RemediationIntegrationTests`) cover the Node.js plumbing; a manual run against a target with a staged data defect is recorded in the results file and on APIPUB-125.
- **Item 2** needs the local build because the parity script reads the SQLite targets with the publisher's own assemblies.
- **Items 3 and 9** need the publisher as a container (memory limit; v1.3 baseline image).
- **Arm D** is started from the DMS repository; switching between Keycloak and self-contained auth (D2 vs D4) is a restart of that stack.
- **Pre-7 isolation branch.** No arm has a source below ODS/API 7. `proxy/mappings/pre7-discovery.json` and `pre7-snapshots.json` stub a 6.2 Discovery document over a 7.x source so the snapshot branch can be exercised by hand: enable them with `-Replace @{ BASE_URL = $arm.ProxyUrl }`, run a publish through the proxy without `--ignoreIsolation`, and check for the `Snapshot-Identifier` header in the proxy journal. Not part of the scripted items.

## Running on the AWS host

`aws/` defines a Windows Server EC2 instance (`r7i.2xlarge`, 64 GB, Docker Desktop over WSL 2, SQL Server 2022 Developer, PowerShell 7, .NET 10 SDK, both repositories under `C:\GIT\Ed-Fi`) that runs the runbook exactly as a workstation does, reachable only through Systems Manager; `aws/README.md` has the permissions, the cost (about $0.90 per hour running, $24 per month stopped) and the operational rules. Deploy once with `aws\Invoke-RegressionHost.ps1 -Deploy`, then per run:

```powershell
# workstation
cd eng/regression/aws
.\Invoke-RegressionHost.ps1 -Start
.\Invoke-RegressionHost.ps1 -Rdp                     # Administrator; -Password -KeyFile <pem> prints the password

# host (RDP session; `pwsh` as administrator, not the "Windows PowerShell" shortcut)
cd C:\GIT\Ed-Fi\Ed-Fi-API-Publisher
git fetch; git checkout <release branch>; git pull
dotnet build -c Release src\EdFi.Tools.ApiPublisher.Cli       # or build.ps1 -Command Package and -PublisherPackage
cd eng\regression
.\Start-Arm.ps1 -Arm B
.\Invoke-Regression.ps1 -Arms B -Items 1,2,5,6,7,8,10,11,12,13 -Version 1.4.0 -PublisherPath ..\..\src\EdFi.Tools.ApiPublisher.Cli\bin\Release\net10.0\EdFiApiPublisher.exe -SkipArmStart
.\Start-NorthridgeSource.ps1 -SqlMaxMemoryMB 20480            # first time: downloads and restores the backup (about 20 minutes)
#   paste the printed lines into arms\arm-b.local.env, then items 3 and 4 as in "Northridge source"
gh auth login; git add results; git commit                    # the result rows, with your own GitHub account

# workstation, when the run is over
.\Invoke-RegressionHost.ps1 -Stop
```

Disconnect the RDP session (do not sign out) while a long run is in progress: Docker Desktop and the running `pwsh` belong to the session. The arms, the proxy and the publisher behave as on a laptop; the only host-specific setting is `-SqlMaxMemoryMB 20480`, which leaves the other half of the RAM to Docker Desktop's VM (`.wslconfig` caps it at 32 GB).

## Recovery

- **Source edits.** Items 1 and 8 edit arm B's source (a student's middle name, deleted calendar dates, re-keyed class periods) and D5 edits the ODS source it reads. The edits are small and later items compare source against target, so they do not break a run, but for a pristine source run `.\Start-Arm.ps1 -Arm B -ResetSource` (recreates the source database from the populated template; `-ResetTarget` does the same for the target).
- **Interrupted exe runs.** A Debug or configuration-store run swaps `logging.json` / `configurationStoreSettings.json` in the publisher's folder and restores them afterwards. If the harness is killed in between, the next run restores the originals from the `.regression-backup` files before it swaps again; delete a leftover backup by hand only if you changed the originals since.
- **Proxy faults.** A killed item can leave a fault mapping loaded; every item that reads through the proxy resets the mappings before it starts, and `Import-Module .\lib\Regression.psm1; Reset-ProxyMappings (Get-Arm B)` does it by hand.
- **Wedged arm.** `.\Start-Arm.ps1 -Arm B -Down -Purge` then `.\Start-Arm.ps1 -Arm B` rebuilds everything (about five minutes).

## Exit codes the items rely on

| Code | Meaning (`PublisherExitCode`) | Items |
| --- | --- | --- |
| 0 | everything published | most |
| 1 | completed, documents rejected beyond tolerance | 5, 6 |
| 2 | processing incomplete, re-run expected | 4 (token endpoint down), 5 |
| 3 | authentication failed | 4 (bad credentials) |
| 4 | invalid configuration | none expected |
