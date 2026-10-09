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

## Dependencies (page blockers 3 and 4)

- APIPUB-108 (Discovery-based resource routing, PR #174) and APIPUB-109 (token endpoint from Discovery, PR #179) are both on `main`, so D1 and D2 no longer need `-Preliminary` against a build that includes them.
- A shared DMS test environment with Keycloak (delivery-plan risk #1) would replace the local compose; nothing in the items depends on where the DMS runs.
