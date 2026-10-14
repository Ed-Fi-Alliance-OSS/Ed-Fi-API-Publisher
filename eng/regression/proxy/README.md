# Fault proxy (WireMock)

Four of the APIPUB-125 items (3, 4, 6, 12) and item 5 cannot be exercised against a healthy ODS/API. Each ODS arm therefore runs `wiremock/wiremock` in front of its source API (service `proxy` in `../arms/ods-arm.yml`). The pass-through is the one mapping WireMock loads from disk (`wiremock/mappings/pass-through.json`, priority 10), so unmatched requests reach the source unchanged and a mappings reset restores it; the item scripts load a fault from this folder through the admin API for as long as the scenario needs it, then remove it. Nothing here is auto-loaded, so a run that does not enable a fault sees a transparent proxy.

| Mapping | Placeholders | Used by | Effect |
| --- | --- | --- | --- |
| `500-window.json` | `URL_PATTERN` | items 3, 5 | 500 on every GET matching the pattern while enabled |
| `401-for-token.json` | `AUTHORIZATION`, `DATA_PATH` | item 4 | 401 for requests carrying one specific bearer token (the one the publisher holds, read from the journal); a fresh token passes through |
| `token-unreachable.json` | `TOKEN_PATH` | item 4 | connection reset on `POST` to the source's token endpoint |
| `429-retry-after.json` | `URL_PATTERN`, `RETRY_AFTER` | item 12 | 429 with `Retry-After` on matching GETs |
| `invalid-id-page.json` | literal `"__INVALID_ID__"`, `DATA_PATH` | item 6 | one fixed `educationContents` page (count and page request alike) whose second element carries the invalid id shape under test |
| `pre7-discovery.json`, `pre7-snapshots.json` | `BASE_URL` | optional | Discovery reports version 6.2 so the snapshot isolation branch runs; the second stub makes a snapshot "exist" |

`Enable-ProxyFault` (in `../lib/Regression.psm1`) substitutes `__NAME__` placeholders from `-Replace` and exact text from `-Literal`, and fills `DATA_PATH` and `TOKEN_PATH` with the source's paths as the proxy serves them (`/data/v3` and `/oauth/token` for an ODS/API, `/api/data` and `/api/oauth/token` for a DMS) unless `-Replace` sets them, posts the mapping, and returns its id for `Disable-ProxyFault`. The request journal is the evidence that does not depend on the publisher's own log: the in-flight cap (`Measure-ProxyConcurrency`, item 12), the gap between each 429 and the retry of the same URL (item 12), every page read exactly once (`Get-ProxyPageReads`, `Test-ProxyPageReads`, item 3), the change version windows (item 13) and the token requests during an outage (item 4). Journals saved to a run folder go through `Save-ProxyJournal`, which redacts every `Authorization` header (the proxy preserves them, so the journal holds bearer tokens and the Basic credentials of token requests).

The proxy keeps the `Host` header the publisher sent, so URLs the ODS writes into its Discovery document point back through the proxy and Discovery-based routing (APIPUB-108) keeps going through the faults.
