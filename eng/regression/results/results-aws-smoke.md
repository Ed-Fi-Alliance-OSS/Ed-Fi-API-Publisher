# API Publisher regression results

One row per test item and arm, appended by the item scripts under `eng/regression/items` (APIPUB-125, APIPUB-146).

| Date | Item | Arm | Result | Publisher | Counts | Duration | Log | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-10-05 10:51 | 04 | B | FAIL | EdFiApiPublisher 0.1.0.1+37136f557720c8e795b30c7cb5ca206a4f7712fb |  | 0 s |  | the item stopped on an error at Regression.psm1:200: docker compose --env-file C:\GIT\Ed-Fi\Ed-Fi-API-Publisher\eng\regression\arms\arm-b.env -f C:\GIT\Ed-Fi\Ed-Fi-API-Publisher\eng\regression\arms\ods-arm.yml --profile proxy stop api-target failed (1). |
| 2026-10-05 11:05 | 04 | B | FAIL | EdFiApiPublisher 0.1.0.1+37136f557720c8e795b30c7cb5ca206a4f7712fb | 384 resources, source 111,831, target 111,831, 0 mismatch(es) | 6.5 min | [long-run.log](runs/20261005-105504-04-b/long-run.log) | scenarios LongRun,Unauthorized401,TokenEndpointDown,BadCredentials; token lifetime 30 min; Bearer token refresh interval for "source" API client set to 15.0 minutes (configured interval: 28.0 minutes, token lifetime reported by the API: 30.0 minutes).  ; the source token was refreshed at least twice during the run (0; token lifetime 30 min, run 2.2 min) |
| 2026-10-05 11:38 | 05 | B | PASS | EdFiApiPublisher 0.1.0.1+37136f557720c8e795b30c7cb5ca206a4f7712fb | 383 resources, source 111,581, target 111,581, 0 mismatch(es) | 2.7 min | [publisher.log](runs/20261005-113442-05-b/publisher.log) | persistent 500 on /ed-fi/studentGradebookEntries; exit code 2 |
