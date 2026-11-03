# CSP Clio Staging Sync - Main Tasks Delivered

Goal: replace the old "clear and fully re-sync production every time" process with a controlled pipeline:
Clio API -> Clio_Staging (prepared and validated) -> EKPBI_New_Dev / EKPBI_New (published), then reporting tables rebuilt and checked.

## 1. Staging database and control framework
1. Created the `Clio_Staging` database with the same raw tables as production (scripts 01, 05).
2. Copied the legacy tables and reporting configuration needed by the pipeline from production.
3. Added a `control` schema that records every run: `SyncRun`, `SyncEndpointResult` (expected rows created at the start of each run).
4. Added a read-only preflight script (00, 06) that checks schemas and readiness before anything changes.

## 2. Staging extraction application (CSP.Clio.StagingSync)
5. Built on the same Clio API calls, mapping (`Save*`) code and endpoint order as CSP.Clio.Sync.
6. Safety guard: refuses to start unless the connection targets the configured staging database.
7. Extracts all 19 Clio endpoints for every active company into an emptied staging buffer.
8. Streams Clio data page by page, with automatic retry on rate limits (HTTP 429) and checks that the final page was reached.
9. Refreshes expired Clio tokens automatically.
10. Marks an endpoint COMPLETE only when pagination finished without error and the SQL row count equals the API row count.
11. Updates a company's sync times only when all of its endpoints completed.
12. Writes a timed per-endpoint log (rows, API, mapping, SQL write, throughput) to `Logs\CSP.Clio.StagingSync-last-run.txt`.
13. Runs read-only commands: `--preflight`, `--status`, `--validate`, `--help`.
14. Uses fixed exit codes (0 = completed, 1 = failed, 2 = refused or configuration error) for scheduling.

## 3. Validation before publishing
15. After extraction, runs a mandatory staging vs production comparison (10% tolerance).
16. A run that fails validation can never be passed to the publisher.
17. Publishing happens automatically only when `PublishAfterValidation` is on, or when `--publish-after-validation --confirm-publish` is passed.

## 4. Controlled publisher (07_Create_Production_Publisher scripts)
18. `PublishApprovedClioStagingRun` publishes only a completed FULL run, and only the latest started run.
19. Allowlist of 19 tables only; checks that staging and target table schemas match.
20. Checks that staging row counts still match the audit before copying (detects buffers changed after extraction).
21. Dry-run mode by default (`@DryRun = 1`), with a plan of rows to insert and replace.
22. Real run replaces data per company inside one transaction (rolls back fully on any error).
23. Locks and a publish lease stop a new extraction and a publication from overlapping.
24. Publish audit tables record what each publication changed.
25. A separate manual script targets `EKPBI_New_Dev` (production target kept as its own reviewed script).
26. After the raw copy, rebuilds the reporting tables in the same order as the old application (`ConvertViewsToTable` sequence).

## 5. Incremental sync (new capability)
27. `IncrementalSyncRunner` fetches only records changed since the last watermark (`updated_since`) with an overlap window.
28. Per-company, per-endpoint watermarks and results are stored in control tables.
29. It never overlaps a full run or another incremental run, never clears data and never publishes.
30. It is switched on only through configuration (`EnableIncrementalSync`).
31. The full daily run remains the authoritative reconciliation.

## 6. Reconciliation with the existing system - issues found and fixed
32. Added `BillingCollDailyCheck.sql`, which compares billing and collection totals per office and per matter.
33. Found that the difference between production and dev came from two independent snapshots of live Clio taken at different times.
34. Found that the publisher rebuilt only 8 reporting views, where the old application rebuilds all active views. Corrected the dev publisher script to rebuild all of them in the old order.
35. Found that the staging EXE wrote dates as day-first text because of machine regional settings, which caused "Conversion failed when converting date and/or time" and silently moved dates into the wrong months. Fixed by setting the application culture to en-US (`Program.cs`).
36. Added `Services\DateFormatAffectedRows.sql` to list and measure every affected row.

## 7. Status and remaining steps
- Done: items 1-36 are implemented in code and scripts.
- Pending verification (not yet run): rebuild the EXE, run a new full extraction, publish to `EKPBI_New_Dev`, re-run `DateFormatAffectedRows.sql` (expect no day-first rows) and `BillingCollDailyCheck.sql`, and compare with production.
- Pending decision: production (`EKPBI_New`) publisher script, and enabling incremental sync after its first test.
