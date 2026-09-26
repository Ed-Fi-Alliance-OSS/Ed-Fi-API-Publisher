# API Publisher regression harness

Scripted, repeatable execution of the release regression: the twelve items of [APIPUB-125](https://edfi.atlassian.net/browse/APIPUB-125) across the supported ODS/API arms, plus the five DMS items of [APIPUB-146](https://edfi.atlassian.net/browse/APIPUB-146). Built for v1.4 as a workstation runbook (Phase 1 on the [Confluence plan](https://edfi.atlassian.net/wiki/x/BwDbpw)); the item scripts take every connection detail as a parameter and exit non-zero on failure, so the v1.5 GitHub Actions job ([APIPUB-149](https://edfi.atlassian.net/browse/APIPUB-149)) wraps them without rewriting.

## Prerequisites

- Windows or Linux workstation with **PowerShell 7.4+** and **Docker Desktop** (Compose v2). Around 6 GB of RAM free per running ODS arm.
- Network access to Docker Hub (`edfialliance/*`, `wiremock/wiremock`).
- The publisher under test, either a local build (`dotnet build -c Release` then `src/EdFi.Tools.ApiPublisher.Cli/bin/Release/net10.0/EdFiApiPublisher.exe`) or a Docker image (`edfialliance/ods-api-publisher:<tag>`, or one built from `src/Dockerfile` with the RC version).
- For arm D, the [Data-Management-Service](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service) repository checked out next to this one (see `arms/arm-d-dms.md`).

## Quick start

```powershell
cd eng/regression

# 1. Bring an arm up (pulls images, clones the ODS templates, creates the API clients). About 5 minutes the first time.
.\Start-Arm.ps1 -Arm B

# 2. Run the short items against a local build and append the rows to results/results-1.4.0.md.
.\Invoke-Regression.ps1 -Arms B -Items 1,2,5,6,7,8,10,11,12 -Version 1.4.0 `
    -PublisherPath ..\..\src\EdFi.Tools.ApiPublisher.Cli\bin\Release\net10.0\EdFiApiPublisher.exe -SkipArmStart

# 3. The container-only items (memory limit, v1.3 baseline comparison).
.\Invoke-Regression.ps1 -Arms B -Items 3,9 -Version 1.4.0 -PublisherImage edfialliance/ods-api-publisher:pre -SkipArmStart

# 4. Other arms; the driver starts them itself.
.\Invoke-Regression.ps1 -Arms A,C -Items 1,2,5,6,10,12 -Version 1.4.0 -PublisherPath <exe>

# One item by hand, with its own parameters:
.\items\12-throttling.ps1 -Arm B -PublisherPath <exe> -MaxConcurrent 2 -RetryAfterSeconds 5

# Tear down.
.\Start-Arm.ps1 -Arm B -Down          # keeps the database volumes
.\Start-Arm.ps1 -Arm B -Down -Purge   # removes them too
```

Each item leaves its logs, memory samples, count reports and proxy journal under `results/runs/<timestamp>-<item>-<arm>/` (git-ignored) and appends one row to the results file, which is committed for the release.

## Layout

```text
eng/regression/
  README.md                      this file
  Invoke-Regression.ps1          driver: -Arms A,B,C,D -Items 1..12,d1..d5 -PublisherPath|-PublisherImage -Version
  Start-Arm.ps1                  up / -ResetTarget / -Down [-Purge] for one arm
  arms/
    ods-arm.yml                  one compose file for every ODS arm (source API, target API, two databases, proxy)
    arm-a.env, arm-b.env, arm-c.env   image tag, ports, credentials per arm; the only per-arm difference
    arm-d.env                    where the externally started DMS stack listens
    arm-d-dms.md                 how to start the DMS with Keycloak or self-contained auth
    bootstrap-pgsql.sql          Admin bootstrap: two ODS instances, two API clients (psql variables from the .env)
  proxy/
    README.md, mappings/         WireMock fault definitions loaded on demand (500, 401, token outage, 429, invalid id, pre-7 stub)
  items/
    01-baseline.ps1 .. 12-throttling.ps1, d1-discovery.ps1 .. d5-change-version.ps1
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

Change the `SOURCE_*` values of an arm's `.env` to point the functional items at another source (for example Northridge for the long runs); nothing else reads the machine's state.

## Items

| # | Script | Arms | Publisher | Mechanism | Duration |
| --- | --- | --- | --- | --- | --- |
| 1 | `01-baseline` | A, B, C | exe or image | full publish, offset/limit; one student edited; incremental publish from the recorded change version | short |
| 2 | `02-cursor-parity` | A, B, C | exe | `eng/Compare-PagingParity.ps1`; arm A must fall back to offset/limit with the log entry | short |
| 3 | `03-memory-long-run` | B | image | container with `--memory`; proxy 500 window mid-run; offset then cursor; every resource streamed once | long |
| 4 | `04-token-lifetime` | B | exe or image | long run over two refresh intervals; current token invalidated mid-run (401 replayed with a fresh token); token endpoint outage with the token invalidated; bad credentials | long |
| 5 | `05-exit-code` | A, B, C | exe or image | persistent 500 on one leaf resource (`studentGradebookEntries`); non-zero exit, error line, everything else published | short |
| 6 | `06-invalid-id` | A, B, C | exe or image | stubbed `educationContents` page, five invalid id shapes; error line with locator, siblings published, locator matches the proxy journal | short |
| 7 | `07-ds5-contacts` | B | exe or image | shipped configuration, contacts streamed and counted | short |
| 8 | `08-deletes-keychanges` | B | exe or image | one calendar date deleted and one class period re-keyed on the source; incremental publish removes and renames on the target | short |
| 9 | `09-discovery-regression` | B | image | `ods-api-publisher:v1.3.0` vs the RC at Debug level; request URL shapes and counts must be identical | short |
| 10 | `10-config-store` | A, B, C | exe or image | PostgreSQL configuration store in the arm's Admin database; publish by connection name; last change version recorded | short |
| 11 | `11-resume` | B | exe or image | run A killed after N pages of a resource; run B `--resumeLastRun=true` resumes at confirmed pages; state removed | medium |
| 12 | `12-throttling` | A, B, C | exe or image | proxy 429 with Retry-After window under `--maxConcurrentSourceRequests`; waits honoured; journal peak within the cap | short |
| D1 | `d1-discovery` | D | exe or image | Debug publish into the DMS; every target URL starts with a Discovery-advertised prefix | short |
| D2 | `d2-keycloak` | D | exe or image | oauth URL on a different host; publish with Keycloak; refresh interval line | short (long with item 4) |
| D3 | `d3-full-publish` | D | exe or image | full publish, ODS source vs DMS counts | short on Grand Bend |
| D4 | `d4-self-contained` | D | exe or image | DMS without Keycloak; publish completes | short |
| D5 | `d5-change-version` | D | exe or image | incremental publish into the DMS, or the failure text as the documented limitation | short |

`Invoke-Regression.ps1` skips an item on an arm it does not apply to and records `SKIP`. Item parameters (fault timing, page sizes, memory limit, scenarios) are documented in each script's header and can be forwarded from the driver with `-ItemArguments`.

## Conventions that keep Phase 2 a wrapper

- **Parameters, not machine state.** An item receives the arm name and the publisher under test; URLs, keys and ports come from `arms/*.env`. No item reads `docker ps`, a developer profile or a hardcoded path.
- **One result row, one exit code.** Every item ends with `Complete-Item`, which appends the row and returns 0 or 1; the driver runs items as child processes so a kill trigger or an exception in one item cannot stop the run.
- **Evidence next to the log.** Whatever an assertion used (count CSV, proxy journal, URL diff, memory samples, run state) is written into the run folder so a reviewer can re-check a row without re-running.
- **The driver is the only thing that knows about arms.** The v1.5 `workflow_dispatch` job passes a version and an arm matrix to `Invoke-Regression.ps1`; nothing else changes.

## Adding an arm or an item

- **ODS arm:** copy `arm-b.env`, change `COMPOSE_PROJECT_NAME`, `ODS_IMAGE_TAG`, the ports and `TPDM_ENABLED`; `Start-Arm.ps1 -Arm <name>` does the rest. A SQL Server arm would need a `-mssql` image tag and a SQL Server variant of `bootstrap-pgsql.sql`.
- **Item:** copy the closest script; keep the parameter block (`-Arm`, `-PublisherPath`, `-PublisherImage`, `-ResultsFile`, `-RunRoot`); use `Assert-Condition` for every check and end with `exit (Complete-Item ...)`. Add the arms it applies to in the `$applicability` table of the driver if it is not every ODS arm.
- **Fault:** add a mapping under `proxy/mappings/` with `__PLACEHOLDER__` tokens and enable it from the item with `Enable-ProxyFault`.

## Manual residue (what stays a workstation task)

- **Items 3 and 4 on Northridge.** The memory and token-lifetime long runs need hours and the 10.6 M document Northridge source (SQL Server on the host). Point arm B's `SOURCE_*` at `http://localhost:8001/` with the Northridge client and run the item overnight; the scripts are the same. Earlier runs starved the host SQL Server when RAM was low, so keep the host quiet.
- **Item 2** needs the local build because the parity script reads the SQLite targets with the publisher's own assemblies.
- **Items 3 and 9** need the publisher as a container (memory limit; v1.3 baseline image).
- **Arm D** is started from the DMS repository; switching between Keycloak and self-contained auth (D2 vs D4) is a restart of that stack.
- **Pre-7 isolation branch.** No arm has a source below ODS/API 7. `proxy/mappings/pre7-discovery.json` and `pre7-snapshots.json` stub a 6.2 Discovery document over a 7.x source so the snapshot branch can be exercised by hand: enable them with `-Replace @{ BASE_URL = $arm.ProxyUrl }`, run a publish through the proxy without `--ignoreIsolation`, and check for the `Snapshot-Identifier` header in the proxy journal. Not part of the scripted items.

## Exit codes the items rely on

| Code | Meaning (`PublisherExitCode`) | Items |
| --- | --- | --- |
| 0 | everything published | most |
| 1 | completed, documents rejected beyond tolerance | 5, 6 |
| 2 | processing incomplete, re-run expected | 4 (token endpoint down), 5 |
| 3 | authentication failed | 4 (bad credentials) |
| 4 | invalid configuration | none expected |
