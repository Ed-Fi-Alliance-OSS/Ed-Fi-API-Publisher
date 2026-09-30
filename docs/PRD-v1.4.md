# Ed-Fi API Publisher v1.4 Product Requirements Document

> **Status:** draft \
> **Release:** v1.4 (Jira release "API Publisher v1.4", target date 2026-10-14) \
> **Jira Epic:** APIPUB-115 \
> **Jira Project:** APIPUB \
> **Repository:** `Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher` \
> **Baseline:** [PRD.md](PRD.md)

This document describes only what v1.4 changes. Everything in the baseline [PRD.md](PRD.md) still applies unless a requirement here changes it. Requirement IDs continue the baseline's numbering, so each ID refers to one requirement across both documents. Each requirement is marked **New** (not in the baseline) or **Changed** (replaces the baseline requirement with the same ID).

## 1. Release Overview

v1.4 has four themes:

| Theme                      | Summary                                                                                                                                                                      |
| -------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **DMS alignment**          | Either end of a publish may be an Ed-Fi Data Management Service (DMS) as well as an Ed-Fi ODS/API. The publisher takes the paths and the token endpoint from each API's Discovery document instead of assuming them. |
| **Reliable run outcome**   | A run that lost documents no longer exits `0`. Exit codes distinguish the kinds of failure, and every run prints a per-resource summary. An expired bearer token is recovered from instead of silently voiding the rest of the run. |
| **Scale and memory**       | Cursor paging with partitions against ODS/API 7.3+, resumable runs, bounded internal buffers, streamed page parsing, and more considerate behavior against a busy source API. |
| **Platform**               | .NET 10 (LTS) for the binaries and the Docker image. .NET 8 reaches end of support on November 10, 2026.                                                                    |

It also includes several defect fixes that clarify existing requirements (Section 3).

## 2. Jobs to Be Done

### New

#### JTBD 9: Knowing Whether an Unattended Run Succeeded

**Personas**: Infrastructure/DevOps Engineer

When the publisher runs on a schedule, I want its exit code to tell a complete run from one that lost documents, so that a bad run is noticed in minutes by automation rather than after the data has been consumed downstream.

**How API Publisher Helps**: The publisher exits non-zero when the target rejects more documents than a configurable tolerance allows (`--toleratedItemErrorCount`, default 0). It uses distinct exit codes for rejected documents, an incomplete run, an authentication failure and invalid configuration. Every run prints a per-resource summary of expected, attempted, failed and skipped documents.

#### JTBD 10: Publishing Into or Out of the Ed-Fi Data Management Service

**Personas**: Data Integration Developer, Infrastructure/DevOps Engineer

When piloting the Ed-Fi Data Management Service, I want to load data into it from an existing ODS/API, or read data out of it, with the same tool and configuration I already use, so that I do not need a separate loader.

**How API Publisher Helps**: The publisher reads each connection's Discovery document and takes from it the data management path, the change queries path and the token endpoint. An ODS/API (`data/v3`, `oauth/token`) and a DMS (`data`, with Keycloak or self-contained authentication) are therefore both addressed correctly without product-specific settings. Each of these values can be overridden on the connection.

#### JTBD 11: Recovering a Large Publish That Failed Partway

**Personas**: Infrastructure/DevOps Engineer, Data Integration Developer

When a long full publish fails or is stopped after hours of work, I want to continue from where it stopped, so that I do not re-read everything that already reached the target.

**How API Publisher Helps**: Against an ODS/API 7.3+ source, the publisher records, for each partition, the last page whose documents all reached the target. `--resumeLastRun=true` continues from that point and replays the original run's change window.

### Changed

#### JTBD 1: Full Data Replication on First Sync

**Addition:** A full publish assumes the target is empty and skips deletes and key changes. That assumption often fails in the field, for example after an earlier full publish failed partway and left data in the target. `--processDeletesAndKeyChangesOnFullPublish=true` makes a full publish process deletes and key changes as well.

## 3. Functional Requirements

### FR-PUBLISH: Core Publishing

- **FR-PUB-8 (Changed):** The publisher SHALL update `lastChangeVersionsProcessed` in the Configuration Store after a successful publishing run. A run that reported an error for any document SHALL NOT advance it, even when `--toleratedItemErrorCount` allows the run to report success (see FR-OUT-4).
- **FR-PUB-12 (New):** The publisher SHALL skip delete and key change processing on a full publish (change window starting at version 1 or below) by default. It SHALL process them when `--processDeletesAndKeyChangesOnFullPublish=true` is set.
- **FR-PUB-13 (New):** A source document with a missing or invalid `id` SHALL produce a logged error that is counted in the run's error set, without faulting the resource. The remaining documents SHALL continue to publish. The error record SHALL NOT retain the source document body.
- **FR-PUB-14 (New):** Key change processing SHALL complete when exactly one resource is selected with `--includeOnly` and a last change version is supplied. Previously, this combination failed with "Unable to reduce resource dependencies for processing key changes".

### FR-CHANGES: Change Query and Version Paging

- **FR-CHG-5 (New):** Against an ODS/API 7.3+ source, the publisher SHALL read main resources with partitioned cursor paging (`GET /{resource}/partitions`, `pageToken`/`pageSize`, `Next-Page-Token`). Support SHALL be detected automatically, and `--disableCursorPaging` SHALL force `offset`/`limit`. `/deletes` and `/keyChanges` SHALL always use `offset`/`limit`.
- **FR-CHG-6 (New):** The publisher SHALL request a configurable number of partitions per resource via `--cursorPagingPartitionCount` (1..200). The default is `--maxDegreeOfParallelismForStreamResourcePages`, capped at 200, the API maximum.
- **FR-CHG-7 (New):** For each cursor-paged resource and partition, the publisher SHALL persist the last page token whose documents all reached the target, together with the run's change window. This lets `--resumeLastRun` continue a failed run without re-reading the pages before that point. A page that lost a document SHALL NOT be treated as completed. State SHALL be kept in a local file whose path is configurable via `--runStatePath`, and SHALL be removed by a run that loses no documents.

### FR-CONN: Connection Management

- **FR-CONN-7 (New):** The publisher SHALL determine each connection's data management path and change queries path in this order of precedence:
  1. The path stated on the connection (`DataManagementUrlSegment`, `ChangeQueriesUrlSegment`).
  2. The path declared in the API's Discovery document.
  3. The conventional `data/v3` and `changeQueries/v1`.

  It SHALL log the path in use for each connection at startup.
- **FR-CONN-8 (New):** The publisher SHALL request each connection's bearer token from the first of these that is available:
  1. The `AuthUrl` stated on the connection.
  2. The `oauth` URL declared in the API's Discovery document.
  3. `oauth/token` relative to the connection URL.

  A declared token endpoint on another host, such as a separate identity provider, SHALL be supported, subject to NFR-SEC-5.
- **FR-CONN-9 (New):** For a deployment that qualifies its routes by tenant, the connection URL SHALL name the tenant (for example, `https://server/tenant1/`).

### FR-RETRY: Resilience and Retry

- **FR-RETRY-3 (Changed):** The publisher SHALL periodically refresh bearer tokens before expiry, configurable via `--bearerTokenRefreshMinutes` (the shipped settings set 28 minutes). When the API reports the token's lifetime, the interval SHALL be capped at half of that lifetime.
- **FR-RETRY-4 (New):** A failed token refresh SHALL be retried on a short backoff (5 seconds, doubling, capped at 60 seconds) while the current token is still usable. Once the token has expired, or when the API reports no lifetime, five consecutive failures SHALL end the run with a `FATAL` log entry. The run SHALL NOT keep sending requests the API will reject.
- **FR-RETRY-5 (New):** When the API responds `401 Unauthorized`, the request SHALL wait while the token is re-acquired and SHALL then be sent again.
- **FR-RETRY-6 (New):** A source read rejected with `429 Too Many Requests` SHALL be retried after the wait given in the `Retry-After` header. When that header is absent or cannot be read, it SHALL be retried after exponential backoff. The number of attempts SHALL be configurable via `--tooManyRequestsRetryAttempts`: the default `-1` follows `--maxRetryAttempts`, and `0` restores the earlier behavior of failing on the first rejection. A `429` from the target API SHALL be reported on the first response, as before.
- **FR-RETRY-7 (New):** The publisher SHALL optionally cap the number of requests in flight against the source API, counted across all resources, via `--maxConcurrentSourceRequests` (default `0`, uncapped).

### FR-AUTH: Authorization Failure Handling

- **FR-AUTH-3 (New):** For a resource configured for authorization-failure handling, a `403 Forbidden` POST SHALL NOT be published as an error. Instead, the entire resource SHALL be re-published through a second, bounded streaming pass once all of its `updatePrerequisitePaths` have completed.
- **FR-AUTH-4 (New):** An authorization-failure handling entry whose path or prerequisites do not exist in the source's dependency graph SHALL be skipped with a warning that names it. It SHALL NOT fail the run. The shipped default SHALL target Data Standard 5.x and later (`/ed-fi/contacts` after `/ed-fi/studentContactAssociations`).

### FR-REMED: Remediations

- **FR-REMED-4 (New):** Remediation handling SHALL be safe when `--maxDegreeOfParallelismForPostResourceItem` is greater than 1, including for POST failures the script does not handle.

### FR-OUT: Run Outcome (New section)

- **FR-OUT-1 (New):** The publisher SHALL report the run's outcome through its exit code:

  | Exit code | Meaning                                                                                                   |
  | --------- | --------------------------------------------------------------------------------------------------------- |
  | `0`       | Everything was published, or the failed documents were within `--toleratedItemErrorCount`.                |
  | `1`       | Every resource ran to completion, but the target rejected more documents than the configured tolerance.  |
  | `2`       | The run did not complete, or part of the source could not be read, so what was published is unknown.     |
  | `3`       | The publisher could not obtain a bearer token from the source or target API.                              |
  | `4`       | The configuration or supplied options are invalid, so nothing was published.                              |

- **FR-OUT-2 (New):** The number of target-rejected documents a run may tolerate SHALL be configurable via `--toleratedItemErrorCount`. `0` (the default) fails a run that lost any document, a positive number allows that many, and `-1` selects best-effort publishing.
- **FR-OUT-3 (New):** Every run, whether it succeeds or fails, SHALL print a summary table with one row per resource and stage, reporting the expected, attempted, failed and skipped document counts.
- **FR-OUT-4 (New):** A run that reported an error for any document SHALL NOT advance `lastChangeVersionsProcessed`, even when the run reports success under `--toleratedItemErrorCount`. Documents abandoned through `--treatForbiddenPostAsWarning` are reported as `Skipped` and SHALL NOT hold back the change version.

## 4. Non-Functional Requirements

### NFR-SEC: Security

- **NFR-SEC-5 (New):** A request carries the connection's credentials, so the publisher SHALL restrict where it follows the Discovery document:
  - A data management or change queries path declared on a different host SHALL be refused.
  - A token endpoint declared on another host SHALL be followed only over HTTPS, SHALL be refused when `IgnoreSSLErrors` is set, and SHALL have its host reported in the log.
  - A declaration SHALL NOT move a token request from HTTPS to plain HTTP.
  - An unacceptable endpoint SHALL stop the run with a configuration error (exit code `4`) before anything is published.
- **NFR-SEC-6 (New):** The run-state file used for resume SHALL be treated as sensitive, because its page tokens describe what was read and how to read more.

### NFR-COMPAT: Compatibility

- **NFR-COMPAT-1 (Changed):** The publisher SHALL require the source and target APIs to be on the same Ed-Fi Data Standard version. Each end may be an Ed-Fi ODS/API or an Ed-Fi Data Management Service.
- **NFR-COMPAT-2 (Changed):** The publisher SHALL target .NET 10 (LTS, supported through November 2028) in both the binaries and the Docker image.

### NFR-PERF: Performance

- **NFR-PERF-3 (New):** Peak memory under cursor paging SHALL NOT exceed that of the `offset`/`limit` path at the same `StreamingPageSize`. Partitions are streamed page by page through the bounded item buffer.
- **NFR-PERF-4 (New):** The resource-processing and error-publishing buffers SHALL be bounded, so that a slow target applies backpressure to source page streaming instead of letting memory grow without limit. The bound is configured via `--processingBlockBoundedCapacity`: `0` (the default) sizes it automatically, `-1` disables it as a rollback lever, and a positive number sets an explicit item capacity. The bound SHALL also apply to the authorization-failure retry pass.
- **NFR-PERF-5 (New):** Pages read from an API source SHALL be parsed in a single forward-only pass, one document at a time, without buffering the whole page as a string. The SQLite connector is exempt because it stores whole pages.
- **NFR-PERF-6 (New):** Memory SHALL stay stable over a long run against a large resource, including in cloud environments that use server GC.

### NFR-OPS: Operations

- **NFR-OPS-5 (New):** The v1.4 release notes SHALL call out the behavior changes in Section 5.

## 5. Behavior Changes for Existing Users

These changes can affect automation or operations that worked with v1.3:

| Change | Effect | Requirement |
| ------ | ------ | ----------- |
| Specific exit codes | Every failure used to return `-1` (`255` on Linux and macOS). A caller that checks for any non-zero code is unaffected. A caller that checks for `255` or `-1` specifically must be updated. | FR-OUT-1 |
| Document loss fails the run | A run that lost any document now exits `1` by default, where it used to exit `0`. Use `--toleratedItemErrorCount` to allow some loss. | FR-OUT-2 |
| Authentication failure mid-run | A run that can no longer authenticate now ends with a non-zero exit code, where it used to continue and exit `0`. | FR-RETRY-4 |
| `429` on source reads | These reads are now waited out and retried, so a run against a busy source can take longer before it succeeds or fails. `--tooManyRequestsRetryAttempts=0` restores the earlier behavior. | FR-RETRY-6 |
| Paths from the Discovery document | Paths are no longer assumed. An ODS/API is unaffected because it declares the same `data/v3` path the publisher used before. | FR-CONN-7, FR-CONN-8 |
| Cursor paging on ODS/API 7.3+ | Cursor paging is on by default when the source supports it. It adds one request per resource, which can make sources made up of small resources slightly slower. `--disableCursorPaging=true` restores `offset`/`limit`. | FR-CHG-5 |
| Authorization-failure default | The shipped default now targets Data Standard 5.x (`/ed-fi/contacts`). Sources on Data Standard 4.0 or earlier need the `/ed-fi/parents` entry. | FR-AUTH-4 |
| Runtime | The framework-dependent build now requires the .NET 10 runtime. The self-contained Windows build carries its own runtime, and the Docker image has moved to a .NET 10 base. | NFR-COMPAT-2 |

## 6. Known Limitations and Out of Scope

### Limitations Introduced or Clarified in v1.4

| Limitation | Detail | Resolution Status |
| ---------- | ------ | ----------------- |
| DMS support validation | End-to-end validation of publishing into a DMS target (Keycloak and self-contained authentication, change queries) is in progress under APIPUB-146. That ticket gates only the inclusion of DMS support: if it does not pass, DMS support moves to v1.5. | In progress. |
| Resume scope | Only cursor-paged main resources from an ODS/API 7.3+ source are resumed. `/deletes`, `/keyChanges` and offset-paged reads are read in full. SQLite targets are not resumable, and both connections must be named. | By design. |
| Target `429` responses | A `429 Too Many Requests` from the target on POST is not retried. Neither is a `429` on a token request. | No current timeline. |

### Deferred to v1.5

- Non-blocking initialization of the Ed-Fi API client (APIPUB-147).
- Regression harness automated in GitHub Actions (APIPUB-149).
- NuGet lock files to pin transitive dependencies (APIPUB-152).

## 7. Glossary Additions

| Term                   | Definition                                                                                                                                                         |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Cursor Paging**      | An ODS/API 7.3+ paging mode that walks a resource with an opaque `pageToken` over partitions returned by `GET /{resource}/partitions`, instead of `offset`/`limit`. |
| **Discovery Document** | The anonymous JSON document at an Ed-Fi API's root URL that declares its version and the URLs of its data management, change queries and `oauth` endpoints.        |
| **DMS**                | The Ed-Fi Data Management Service, an implementation of the Ed-Fi API that serves data management resources under `data` rather than `data/v3`.                   |
| **Run State**          | The local file (`--runStatePath`) holding the per-partition page tokens and change window that `--resumeLastRun` uses to continue a failed run.                    |

## 8. Traceability to Jira

| Area                                 | Requirements                                              | Jira                                           |
| ------------------------------------ | --------------------------------------------------------- | ---------------------------------------------- |
| DMS alignment                        | JTBD 10, FR-CONN-7–9, NFR-SEC-5, NFR-COMPAT-1             | APIPUB-107, APIPUB-108, APIPUB-109, APIPUB-146 |
| Run outcome and exit codes           | JTBD 9, FR-OUT-1–4, FR-PUB-8, NFR-OPS-5                   | APIPUB-120                                     |
| Bearer token recovery                | FR-RETRY-3–5                                              | APIPUB-119                                     |
| Busy source APIs                     | FR-RETRY-6–7                                              | APIPUB-140 (under APIPUB-135)                  |
| Cursor paging and resume             | JTBD 11, FR-CHG-5–7, NFR-PERF-3, NFR-SEC-6                | APIPUB-131, APIPUB-135, APIPUB-142             |
| Memory growth                        | NFR-PERF-4–6, FR-AUTH-3                                   | APIPUB-112, APIPUB-133, APIPUB-134             |
| Full publish into a non-empty target | JTBD 1, FR-PUB-12                                         | APIPUB-113                                     |
| Invalid source document id           | FR-PUB-13                                                 | APIPUB-102, APIPUB-103                         |
| Single-resource key changes          | FR-PUB-14                                                 | APIPUB-104                                     |
| Data Standard 5.x defaults           | FR-AUTH-4                                                 | APIPUB-132                                     |
| Remediation concurrency              | FR-REMED-4                                                | APIPUB-114                                     |
| .NET 10                              | NFR-COMPAT-2                                              | APIPUB-116, APIPUB-121, APIPUB-122             |

Build, CI and dependency work in the release does not change product requirements: APIPUB-111, 123, 137, 143, 145, 148, 150, 151, 153 and 154. Neither does the release process: APIPUB-124, 125, 126 and 156.
