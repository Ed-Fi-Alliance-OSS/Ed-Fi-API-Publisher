# Fault proxy (WireMock)

Four of the APIPUB-125 items (3, 4, 6, 12) and item 5 cannot be exercised against a healthy ODS/API. Each ODS arm therefore runs `wiremock/wiremock` in front of its source API (service `proxy` in `../arms/ods-arm.yml`). The pass-through is the one mapping WireMock loads from disk (`wiremock/mappings/pass-through.json`, priority 10), so unmatched requests reach the source unchanged and a mappings reset restores it; the item scripts load a fault from this folder through the admin API for as long as the scenario needs it, then remove it. Nothing here is auto-loaded, so a run that does not enable a fault sees a transparent proxy.

| Mapping | Placeholders | Used by | Effect |
| --- | --- | --- | --- |
| `500-window.json` | `URL_PATTERN` | items 3, 5 | 500 on every GET matching the pattern while enabled |
| `401-window.json` | `URL_PATTERN` | optional | 401 on every matching request while enabled (item 4 revokes the token server-side instead, so the replay with a fresh token can succeed) |
| `401-for-token.json` | `AUTHORIZATION` | item 4 | 401 for requests carrying one specific bearer token (the one the publisher holds, read from the journal); a fresh token passes through |
| `token-unreachable.json` | none | item 4 | connection reset on `POST /oauth/token` |
| `429-retry-after.json` | `URL_PATTERN`, `RETRY_AFTER` | item 12 | 429 with `Retry-After` on matching GETs |
| `invalid-id-page.json` | literal `"__INVALID_ID__"` | item 6 | one fixed `educationContents` page (count and page request alike) whose second element carries the invalid id shape under test |
| `pre7-discovery.json`, `pre7-snapshots.json` | `BASE_URL` | optional | Discovery reports version 6.2 so the snapshot isolation branch runs; the second stub makes a snapshot "exist" |

`Enable-ProxyFault` (in `../lib/Regression.psm1`) substitutes `__NAME__` placeholders from `-Replace` and exact text from `-Literal`, posts the mapping, and returns its id for `Disable-ProxyFault`. The request journal (`Get-ProxyJournal`, `Measure-ProxyConcurrency`) is the evidence for the in-flight cap and the Retry-After wait.

The proxy keeps the `Host` header the publisher sent, so URLs the ODS writes into its Discovery document point back through the proxy and Discovery-based routing (APIPUB-108) keeps going through the faults.
