# API Publisher v1.4.0 regression results

One row per test item and arm, appended by the item scripts under `eng/regression/items` (APIPUB-125, APIPUB-146). Evidence for each row (publisher log, count CSVs, proxy journal, memory samples) is in the run folder the Log column points to; run folders are not committed, so the reviewer pulls them from the workstation that produced the row.

Arms: A = ODS/API 7.1 / DS 4.0.0, B = ODS/API 7.3.2 / DS 5.2.0, C = ODS/API 7.3.2 / DS 6.1.0, D = Ed-Fi DMS 8.x (APIPUB-146).

Release candidate under test: _to be filled in when the RC is cut (nupkg version, Docker image tag)._

| Date | Item | Arm | Result | Counts | Duration | Log | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
