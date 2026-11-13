# CSP.Clio.StagingSync – Workflow and Pipeline

Status: development complete, testing in progress (November 2026).

## 1. Purpose

Clio data is collected into a staging database (`Clio_Staging`), checked, turned into the reporting tables, and only then copied to the report database (`EKPBI_New_Dev`). Production (`EKPBI_New`) is never written directly. Success means `BillingCollDailyCheck.sql` shows 0 variance between Dev and Prod.

## 2. Overview

```
Clio API (19 endpoints, 4 companies)
        |  first load (once) + incremental every 5 min
        v
Clio_Staging  - raw tables (19) + reporting layer (views, procedures, functions, reference tables)
        |  full check (morning / night): catch-up -> delete reconciliation -> snapshot -> 10% check -> PopulateTables
        v
EKPBI_New_Dev - raw tables + 34 *_Table reporting tables (+3 helper tables)
```

## 3. Databases and what is in them

| Database | Content |
|---|---|
| `Clio_Staging` | 19 raw endpoint tables, `control.*` run tables, reporting layer copied from Prod (script 11): 35 active views (`CSPViewsToTable`), `PopulateTables`, post-sync procedures, 70 functions, reference tables, `<Table>_Deleted` archive tables |
| `EKPBI_New_Dev` | Publish target. Raw tables, `*_Table` reporting tables, publish control tables, `PublishApprovedClioStagingRun` |
| `EKPBI_New` (Prod) | Only read (script 05 and 11 copy reference tables and definitions from it). Never written |

Raw endpoints: Activities, ActivityDescriptions, Allocations, BankTransactions, Bills, CalendarEntries, Calendars, Contacts, CustomFields, CustomFieldSets, Folders, LineItems, MatterNotes, Matters, PracticeAreas, Relationships, Tasks, TaskTypes, Users.

## 4. Commands

| Command | When | What it does |
|---|---|---|
| (no arguments) | Once, when the raw tables are empty | First load of all 19 endpoints for every company, then builds the reporting tables and sets incremental watermarks. Refuses if the raw tables already hold rows (`--force-full-extract` overrides) |
| `--incremental` | Every 5 minutes | Polls Clio with `updated_since` (10-minute overlap) and upserts changed rows into the raw tables only. No reporting rebuild. Skips with exit 0 if another run holds the lock |
| `--full-check` | Morning and night | The steps in section 6, including the delete reconciliation (applied). The publish is only a dry run |
| `--full-check --confirm-publish` | Morning and night (scheduled) | Same, then publishes to Dev |
| `--full-check ... --skip-deletes` | Rarely | Same, but leaves the delete reconciliation out for this run |
| `--reconcile-deletes` | Optional, by hand | Report only: shows records the staging holds that Clio no longer has. Changes nothing |
| `--reconcile-deletes --apply` | Optional, by hand | Applies the deletions without a full check. Normally not needed, because every full check does it |

Exit codes: 0 success or skipped (busy), 1 failure or blocked, 2 refused or unexpected error.

## 5. Incremental sync (every 5 minutes)

The incremental sync keeps the 19 raw tables in `Clio_Staging` current without reloading them. It never rebuilds the reporting tables and never publishes to Dev.

**What one run does, for each company and each of the 19 endpoints:**

1. Takes the pipeline lock. If another run holds it, this run skips and exits with 0.
2. Reads the saved position (watermark) of the endpoint from `control.IncrementalWatermark`.
3. Asks Clio for every record changed since that position, minus a 10-minute overlap (`IncrementalOverlapMinutes`) so that records changed during the previous poll are read again safely.
4. Writes each record into the raw table: it deletes the old row with the same Clio ID and company, then inserts the new one. A **new** record is inserted, a **changed** record is replaced. Reading the same record twice does no harm.
5. Records the result per endpoint in `control.IncrementalSyncRun` and `control.IncrementalSyncResult`. The watermark moves forward only if the endpoint succeeded, so a failed endpoint is retried from the same point at the next run.
6. Prints one line per endpoint and company, for example `Incremental Activities - KAT: 863 changed row(s).`, and a total with the time taken.

**First run and long gaps:** an endpoint without a watermark starts `IncrementalInitialLookbackHours` back (default 72 hours). After a pause longer than that on a first run, raise the setting so that it covers the gap. After the first successful poll the setting is ignored. The first load seeds the watermarks itself.

**What it cannot do:** Clio's `updated_since` does not report deleted records. Deletions are handled by the delete reconciliation (section 7), which runs inside every full check.

**Timing:** one run after a gap of a few days handled about 2,900 changed rows in 1 minute 35 seconds. A normal 5-minute run has far fewer rows. Check that it finishes well inside 5 minutes on the server.

**Failure behaviour:** a failed endpoint is reported in the log and the other endpoints continue. A run that was killed leaves a RUNNING row that the next run closes as ABANDONED. A second copy started at the same time is skipped by the lock.

## 6. Full check – the steps

1. **Catch-up:** one more incremental poll so staging is current.
2. **Delete reconciliation (applied):** records deleted in Clio move to `<Table>_Deleted` and leave the raw tables (see section 7). It runs before the snapshot, so the reports and Dev show the deletions in the same run. If it reports a problem, the run stops and nothing is published. `--skip-deletes` leaves this step out.
3. **Snapshot:** records the current staging counts as an approved run (`control.SyncRun`, status EXTRACTED).
4. **10% check:** every endpoint must be COMPLETE (PASS) and within 10% of the target database row counts. Failure stops the run.
5. **Reporting build in `Clio_Staging`:** `PopulateTables` and the post-sync procedures (about 8 to 18 minutes).
6. **Publish:** copies the raw tables and the reporting tables to Dev (about 20 minutes). Without `--confirm-publish` this is only a dry run.

Note: the deletions in step 2 are applied in `Clio_Staging` even without `--confirm-publish`. Only the publish is a dry run, so a plain `--full-check` is not read-only.

Publisher safety checks stop the copy when, for example: an incremental run is active (51049), the latest incremental did not complete (51050), a reporting table is missing or differs in structure (51052, 51053), row counts differ after copy (51057), or reporting tables were built before the approved snapshot (51058). Any failure leaves Dev unchanged (transaction rollback).

## 7. Delete handling

Clio's `updated_since` cannot report deletions. The reconcile command compares the IDs in the table with Clio's full ID list per company and endpoint.

- A record counts as deleted only if Clio returns HTTP 404 for it. The exception is Users, where Clio's full list is the source of truth.
- Safeguards: an incomplete listing, an empty listing for a table with rows, more than 5% missing (small tables up to 25 missing are always checked), or more than 2000 confirmations stop the endpoint without changes.
- Confirmed deletions move to `dbo.<Table>_Deleted` (extra columns `DeletedAtUTC`, `DeletedByRunID`) and leave the raw table. The tables are created on first use.
- It runs automatically as step 2 of every full check, so the reporting tables and Dev reflect the deletions in the same run. No manual step is needed.
- It lists every ID, so large tables are slow (for example 90,000 Activities rows take minutes). It adds roughly 20 to 40 minutes to each full check (an estimate; a full check now takes about 1 to 1.5 hours).
- If it reports a problem or a safety limit stops an endpoint, the full check stops before the snapshot and Dev keeps its last good data. The standalone `--reconcile-deletes` commands remain for manual use.

## 8. Locks and safety

- One pipeline lock (`ClioStagingPipeline`) means incremental, full check, reconcile and first load never run together. A second copy skips or is blocked.
- The publisher has its own locks and a lease. A published run is checked against the staging snapshot.
- Stale RUNNING rows from a killed run are closed as ABANDONED at the next start.
- Dates are stored as text using US culture (pinned in the program). No time zone is pinned, so the program can run on the Dubai server.
- `PublishAfterValidation` should be false so that a no-argument run never publishes.

## 9. Setup from scratch

Run in SSMS or sqlcmd (folder `Clio_Staging_Setup_2026-10-03`).

1. `Clio_Staging`: `01_Create_Clio_Staging` -> `05_Copy_Legacy_Tables_From_Prod` -> `09_Update_Clio_Staging_ClearAllData` -> `10_Create_Incremental_Sync_Control` -> `11_Import_Reporting_Layer_From_Prod` (first with `@DryRun = 1`, read the output and report missing reference tables, then `@DryRun = 0`).
2. `EKPBI_New_Dev`: `07_Create_Production_Publisher...` -> `12_Create_Staging_Publisher...` (rerun 12 after any change).
3. Check the active companies and their credentials in staging, and `App.config` (`ProductionSql` -> `EKPBI_New_Dev`).
4. Build the EXE and run it from its `bin` folder.

## 10. Test plan

1. First load on an empty `Clio_Staging` (use a copy).
2. `--incremental` twice. Time it, it must finish within 5 minutes. Start two at once, the second must skip.
3. Optional: `--reconcile-deletes` (report only) to review what would be archived. On the old test data, Users showed about 49 stale rows (255 to 206).
4. `--full-check`, then `--full-check --confirm-publish`. The deletions are applied in step 2 of each. Check the `_Deleted` tables, then run `BillingCollDailyCheck.sql` on Dev and Prod: variance 0.
5. Failure checks: stop the EXE halfway and rerun, run `--full-check` while an incremental runs, read the logs.

## 11. Scheduling (Dubai production server)

| Task | Command | Schedule |
|---|---|---|
| Incremental | `--incremental` | Every 5 minutes |
| Publish (full check with deletes) | `--full-check --confirm-publish` | Every 90 minutes |

Measured on the server (13 November 2026): catch-up and delete step about 40 minutes, 10% check and reporting build about 38 minutes, publish to Dev about 4 minutes. A run with deletes therefore takes about 1 hour 25 minutes, which only just fits into the 90-minute rhythm; a run without deletes (`--skip-deletes`) takes about 45 minutes. A publish slot that comes while a run is still going is skipped ("do not start a new instance"), so a slow run means the next publish comes after about 3 hours. If this happens often, add `--skip-deletes` to the Publish task. The delete step also runs inside every publish run, so deletions reach Dev with every publish.

The script `Setup_Scheduled_Tasks.ps1` (run once in an elevated PowerShell) creates both tasks and removes the old "CSP Clio Full Morning" and "CSP Clio Full Night" tasks if they exist:

```
.\Setup_Scheduled_Tasks.ps1 -ExePath "D:\Clio\CSP.Clio.StagingSync\CSP.Clio.StagingSync.exe" -PublishEveryMinutes 90 -FirstPublishAt "00:30" -RunAs "DOMAIN\svc_clio"
```

Task settings it applies: "Do not start a new instance", no execution time limit, run whether the user is logged on or not, start late if the server was off, "Start in" set to the EXE folder. The account needs SQL login access to `Clio_Staging` and `EKPBI_New_Dev`, write access to the EXE folder and "Log on as a batch job". A full check takes about 1 to 1.5 hours, during which the 5-minute task skips and then catches up. Logs are in `Logs\*-last-run.txt` and are overwritten each run.

Server deployment: build in Release, copy the folder to a fixed path, change the connection strings (`EKPBIEntities` to `Clio_Staging`, `ProductionSql` to `EKPBI_New_Dev`), run scripts 01, 05, 09, 10, 11 on `Clio_Staging` and 07, 12 on `EKPBI_New_Dev`, set `Clio_Staging` to SIMPLE recovery with its files on a large drive, and run the first load, a full check and a timed incremental by hand before scheduling.

## 12. Open points

- Dated per-run logs and failure alerts (email or event log) are not built yet.
- A second copy of a command crashes on the locked log file instead of skipping cleanly.
- The reconcile step is slow on large tables and adds 20 to 40 minutes to each full check.
- Untested: the 5-minute timing, a complete full check with the delete step applied, the real publish with 0 variance on the server, the scheduled tasks, the Dubai server setup (scripts 11 and 12 must be run there too).
- Proven locally: first load (76 of 76 COMPLETE), catch-up, snapshot, 10% check, reporting build and the publisher dry run. Clio_Staging needs SIMPLE recovery and enough log space for the reporting build.
- Reference tables missing in Prod must be reported when script 11 runs.
