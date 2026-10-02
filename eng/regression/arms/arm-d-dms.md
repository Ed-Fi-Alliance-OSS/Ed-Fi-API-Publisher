# Arm D: Ed-Fi DMS target (APIPUB-146)

Arm D is not started from this folder. The DMS stack comes from the `Data-Management-Service` repository, checked out next to this one, and `arm-d.env` only records where it listens. Items `d1` to `d5` then run through the same driver and module as the ODS arms.

## Bring the DMS up

From `Data-Management-Service/eng/docker-compose` (PowerShell 7):

```powershell
# One-shot: infrastructure (PostgreSQL, Config Service, Keycloak), schema for Data Standard 5.2, DMS.
.\bootstrap-local-dms.ps1 -EnableKeycloak -DataStandardVersion 5.2

# Or phase by phase (see the script header for the DMS-1153 phase contract):
.\start-local-dms.ps1 -InfraOnly -EnableKeycloak
.\configure-local-data-store.ps1
.\provision-dms-schema.ps1
.\start-local-dms.ps1 -DmsOnly
```

Data Standard 5.2 matches arm B (the source). The `.env.ds52` overlay in that folder selects the DS 5.2 schema packages; `.env.ds61` exists for a 6.1 pairing with arm C if that is wanted later.

Then create the DMS client the publisher writes with (`setup-keycloak.ps1` for the Keycloak realm and client; the Config Service API for the vendor and application) and put its key and secret as `TARGET_KEY` / `TARGET_SECRET` in `arm-d.local.env` (git-ignored; `Get-Arm` layers it over the committed `arm-d.env`). The publisher discovers the DMS token endpoint from the Discovery document (`urls.oauth`, APIPUB-109), so no token URL is configured here.

## Two identity modes

| Item | DMS mode | How to switch |
| --- | --- | --- |
| D1, D2, D3, D5 | Keycloak (`-EnableKeycloak`) | default above |
| D4 | Self-contained (OpenIddict in the Config Service) | restart the DMS without `-EnableKeycloak`; `setup-openiddict.ps1` creates the client |

## Reset between items

The ODS arms reset their target by recreating the minimal template. For the DMS, set `TARGET_RESET_COMMAND` in `arm-d.env` to a PowerShell command that returns the DMS to an empty, provisioned schema (for example re-provisioning it with `provision-dms-schema.ps1` after `start-local-dms.ps1 -InfraOnly`, from the DMS repository); `Reset-RegressionTarget` runs it and waits for the DMS to answer again. Without it the target is not reset and a warning says so.

D3 resets the target and then requires it to be empty for every non-descriptor resource of the source before it publishes, because D1 and D2 publish the same source first and matching counts on their data would say nothing about D3's run. Without a reset command D3 fails that check with a pointer here. D5 does not depend on the starting state: it checks that the one document it edits on the source arrives in the DMS.

## Container-mode runs

To run the publisher as a container against the DMS, set `DOCKER_NETWORK` in `arm-d.env` to the DMS compose network (`docker network ls`), and `TARGET_URL_IN_NETWORK` to the DMS service name on it (for example `http://dms:8080/`). The source stays arm B; its in-network name from arm D's point of view is not resolvable unless both stacks share a network, so container mode for arm D normally uses `host.docker.internal` for the source, which `Invoke-Publisher` substitutes automatically for `localhost` URLs.

## What the runner does for a DMS target

The runner tells a DMS from an ODS/API by the version its Discovery document reports (8.x for the DMS). For a DMS target it:

- copies the source's school years into the target after `TARGET_RESET_COMMAND`, because the publisher never publishes `schoolYearTypes` and a reprovisioned DMS data store has none;
- starts retries at 1000 ms (`--retryStartingDelayMilliseconds=1000`), because the DMS answers transient 500s under the default concurrency;
- expects the shortfall listed in `KNOWN_TARGET_REJECTIONS` (source records the DMS rejects as invalid and an ODS/API accepts) and tolerates exactly that many rejected documents, when the source is an ODS/API. A DMS source never holds those records. The committed values are Grand Bend's; set the key empty, or to the right values, in `arm-d.local.env` for another source.

## DMS as the source

Arm D also covers DMS to DMS and DMS to ODS/API: point `SOURCE_*` at the DMS in `arm-d.local.env`, one profile per direction.

- **Credentials.** `EdFiAPIPublisherReader` is enough for the items that only read the source (D1, D3, item 3, item 11). Item 8 and D5 also edit the source and then publish incrementally, which needs both write access and `ReadChanges`; neither publisher claim set has both, so use an `EdFiSandbox` application on the source data store for those items.
- **Isolation.** A DMS data store has no snapshot unless one is configured, so the runner's default `--ignoreIsolation=true` applies.
- **Proxy (item 3).** Item 3 reads the source through a WireMock proxy, and arm D has none of its own. Start arm B for its proxy and in `arm-d.local.env` set `PROXY_PORT=8280` (arm B's), `PROXY_FORWARD_URL=http://host.docker.internal:<DMS port>`, `PROXY_BASE_PATH=api/` (the DMS serves under `/api`), and `DOCKER_NETWORK=apipub-reg-b_default` (arm B's network) so the publisher container reaches `proxy:8080`. Arm B's own items reset the proxy back to its source when they run. The same settings serve every proxy item (3, 4, 5, 6, 12 and the ChangeVersionPaging scenario of 13): the fault mappings take the data and token paths from the Discovery document the source answers through the proxy, so they match `/api/data` and `/api/oauth/token` on a DMS.
- **Configuration store (items 6 and 10).** These items keep their connections in a PostgreSQL configuration store inside an arm's `db-admin` container, and arm D has none. Set `STORE_ARM=B` in `arm-d.local.env` to use arm B's, which the publisher container reaches on the same network as the proxy. The proxy preserves the Host header, so the URLs the DMS writes into its Discovery document point back through the proxy.

## Dependencies (page blockers 3 and 4)

- APIPUB-108 (Discovery-based resource routing, PR #174) and APIPUB-109 (token endpoint from Discovery, PR #179) are both on `main`, so D1 and D2 no longer need `-Preliminary` against a build that includes them.
- A shared DMS test environment with Keycloak (delivery-plan risk #1) would replace the local compose; nothing in the items depends on where the DMS runs.
