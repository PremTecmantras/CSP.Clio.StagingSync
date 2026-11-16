# CSP.Clio.StagingSync – Workflow and Pipeline (two-database design)

Status: built and tested locally on the restored server databases (November 2026). Not yet running on the Dubai server – see `Server_Change_List.md`.

## 1. Purpose

Clio data is collected into a database that is always current (`Clio_Live`), copied into a frozen staging database (`Clio_Staging`), checked, turned into the reporting tables there, and only then published to the report database (`EKPBI_New_Dev`). Production (`EKPBI_New`) is never written directly. Success means `BillingCollDailyCheck.sql` shows 0 variance between Dev and Prod.

## 2. Overview

```
Clio API (19 endpoints, 4 companies)
   |  every 5 min: --incremental              |  background: --delete-sync (all day)
   v                                          v
Clio_Live      raw tables (19) + *_Deleted archives + control tables       <- written all day, never frozen
   |  publish cycle, every 90 min (--full-check --confirm-publish):
   |  1) copy the raw tables into Clio_Staging (Live is held for the copy only)
   v
Clio_Staging   raw copy + reporting layer. 2) snapshot  3) 10% check  4) PopulateTables (reports)
   |  5) publish
   v
EKPBI_New_Dev  raw tables + 34 *_Table reporting tables (+3 helper tables)
```

Three scheduled jobs, one EXE, running at the same time:

| Job | Command | Database | Rhythm |
|---|---|---|---|
| Incremental | `--incremental` | Clio_Live | every 5 minutes |
| Delete | `--delete-sync` | Clio_Live | every 60 minutes (a pass takes 15–45 min) |
| Publish cycle | `--full-check --confirm-publish` | Clio_Live → Clio_Staging → Dev | every 90 minutes |

The incremental and the delete task never wait for the publish cycle's long phases (check, reporting build, publish): those only touch `Clio_Staging`. Dev changes only at publish time, so it is as fresh as the last cycle; `Clio_Live` is at most 5 minutes behind Clio.

Single-database mode: leave `LiveDatabaseName` empty in the config and everything runs in `Clio_Staging` as before (the cycle then holds the database for its whole run and applies the deletions itself).

## 3. Databases

| Database | Content |
|---|---|
| `Clio_Live` | 19 raw endpoint tables, `<Table>_Deleted` archives, company list with the Clio tokens, `control.*` (incremental runs and watermarks, `DeleteRun`, deletion candidates). Created by script 13 as a copy of Clio_Staging. Nothing else writes here |
| `Clio_Staging` | Copy of the raw tables (replaced at the start of every cycle), the reporting layer imported from Prod (script 11: views, `PopulateTables`, procedures, functions, reference tables), the built `*_Table` reports, snapshot/check control tables, `control.LiveCopyRun` / `LiveCopyTable` (audit of the copies) |
| `EKPBI_New_Dev` | Publish target: raw tables, `*_Table` reporting tables, publish control tables, `PublishApprovedClioStagingRun` |
| `EKPBI_New` (Prod) | Only read. Never written |

Raw endpoints: Activities, ActivityDescriptions, Allocations, BankTransactions, Bills, CalendarEntries, Calendars, Contacts, CustomFields, CustomFieldSets, Folders, LineItems, MatterNotes, Matters, PracticeAreas, Relationships, Tasks, TaskTypes, Users. The copy also carries `_EKPCompanies` (Active flags), so Live is the master for the company list.

## 4. Commands

| Command | Database | What it does |
|---|---|---|
| (no arguments) | Live | First load of all 19 endpoints, only when the raw tables are empty (`--force-full-extract` overrides). Sets the incremental watermarks. Does not build reports or publish – the first `--full-check` does |
| `--incremental` | Live | Polls Clio with `updated_since` (10-minute overlap) and upserts changed rows. Skips (exit 0) if the previous run is still going or a publish cycle is copying Live |
| `--delete-sync` | Live | Background delete pass (section 6). Skips (exit 0) if another pass is still running |
| `--reconcile-deletes [--apply] [--only Users,Tasks]` | Live | By hand: report (or with `--apply` apply) deleted records; `--only` limits the endpoints. A restricted pass is recorded as PARTIAL |
| `--full-check` | Live → Staging | Publish cycle steps (section 5), publisher dry run at the end |
| `--full-check --confirm-publish` | Live → Staging → Dev | Same, then publishes to Dev |

Exit codes: 0 success or skipped (busy), 1 failure or blocked, 2 refused or unexpected error. A second copy of a command no longer crashes on the log file (it writes `...-pid<N>.txt`).

## 5. Publish cycle – the steps

0. **Task check:** the cycle never polls Clio. It stops (nothing published) if the last completed incremental is older than `FullCheckMaxIncrementalAgeMinutes` (30) or the last clean delete pass is older than `FullCheckMaxDeleteAgeMinutes` (240). A stopped task therefore cannot lead to a publish of old data.
1. **Copy Live → Staging:** takes the exclusive `ClioLiveWrite` lock on Live, replaces the 20 tables in Staging (parallel, up to 4 at a time, identity values kept, row counts compared), releases the lock. Incremental runs that meet this moment skip and catch up 5 minutes later. The copy writes a marker row (`control.IncrementalSyncRun`) so the publisher's checks 51049/51050 refuse a copy that is unfinished or failed.
2. **Snapshot:** records the Staging counts as an approved run (`control.SyncRun`, EXTRACTED).
3. **10% check:** every endpoint COMPLETE and within 10% of the Dev row counts, otherwise the cycle stops.
4. **Reporting build in `Clio_Staging`:** `PopulateTables` and the post-sync procedures.
5. **Publish:** copies the raw tables and the reports to Dev in one transaction. Without `--confirm-publish` this is only a dry run.

Publisher safety checks (unchanged): 51049 incremental/copy running, 51050 latest copy not complete, 51052/51053 reporting table missing or different, 51057 row counts differ after copy, 51058 reports built before the snapshot. Any failure leaves Dev unchanged.

## 6. Incremental and delete task

**Incremental (every 5 minutes):** for each company and endpoint it reads the saved position from `control.IncrementalWatermark`, asks Clio for everything changed since then minus 10 minutes, and replaces the rows with the same Clio ID (new rows inserted, changed rows replaced). The watermark advances only for endpoints that succeeded. A killed run is closed as ABANDONED at the next start.

**Delete task (`--delete-sync`):** `updated_since` cannot see deletions, so the task compares the IDs in each raw table with Clio's full ID list, per company and endpoint (76 chunks):
- a record counts as deleted only on HTTP 404 (Users: Clio's list is authoritative);
- safeguards: incomplete or empty listing, more than 5% missing (small tables up to 25 always checked), more than 2000 confirmations – the endpoint is skipped and reported;
- confirmed rows move to `<Table>_Deleted` (with `DeletedAtUTC`, `DeletedByRunID`) and leave the raw table. Only this archive step needs the database for itself, so it takes the shared lock for a few seconds; the long part (reading Clio's ID list) holds no lock;
- every pass is recorded in `control.DeleteRun` (COMPLETE, PARTIAL, PROBLEMS, FAILED, ABANDONED). The cycle requires a recent COMPLETE pass. A chunk that fails is retried by the next pass.

The deletions reach Staging with the next copy and Dev with the next publish.

## 7. Locks

Application locks, always in the database of the process that takes them:

| Lock | Taken by | Mode |
|---|---|---|
| `ClioLiveWrite` (Live) | incremental (whole run), delete archive step | Shared (can run together) |
| `ClioLiveWrite` (Live) | publish cycle during the copy, first load | Exclusive |
| `ClioIncrementalRun` (Live) | incremental | Exclusive (one run at a time) |
| `ClioDeleteRun` (Live) | delete task | Exclusive (one pass at a time) |
| `ClioStagingPipeline` (Staging) | publish cycle | Exclusive (one cycle at a time) |

Locks are session locks, released when the process ends and explicitly released when given up early (a lock kept by a pooled connection would otherwise block the incrementals for the rest of the cycle). Single-database mode: the cycle holds `ClioLiveWrite` exclusively for its whole run.

Safety: each process verifies by asking SQL Server which database it is connected to (`SELECT DB_NAME()`) before writing; the Live switch of the connection string is checked against `LiveDatabaseName`. Dates are stored as text under US culture. `PublishAfterValidation` must be false on the server.

## 8. Setup from scratch

Run in SSMS or sqlcmd (folder `Clio_Staging_Setup_2026-10-03`).

1. `Clio_Staging`: `01` -> `05` -> `09` -> `10_Create_Incremental_Sync_Control` -> `11_Import_Reporting_Layer_From_Prod` (first `@DryRun = 1`, then `@DryRun = 0`).
2. `EKPBI_New_Dev`: `07_Create_Production_Publisher...` -> `12_Create_Staging_Publisher...`.
3. **`13_Create_Clio_Live.sql`** (new): creates `Clio_Live` as a copy of `Clio_Staging`, sets SIMPLE recovery, adds `control.DeleteRun` (Live) and the copy audit tables (Staging). Needs disk for one backup file plus one more database copy; delete the backup file afterwards.
4. Config: `LiveDatabaseName = Clio_Live`, connection string `EKPBIEntitiesLive` (same server and login, `Initial Catalog = Clio_Live`), `EKPBIEntities` → `Clio_Staging`, `ProductionSql` → `EKPBI_New_Dev`, `PublishAfterValidation = false`.
5. Load: when starting from empty tables run the EXE without arguments (loads Live), then `--full-check` (copies, checks, builds). When Staging already holds the data, script 13 copies it and no load is needed.
6. Run `--delete-sync` once, then `--full-check`, then `--full-check --confirm-publish`, check `BillingCollDailyCheck.sql` (variance 0), then create the tasks with `Setup_Scheduled_Tasks.ps1`.

## 9. Scheduling

`Setup_Scheduled_Tasks.ps1` (elevated PowerShell, once) creates the three tasks and removes the old "CSP Clio Full Morning/Night" tasks:

```
.\Setup_Scheduled_Tasks.ps1 -ExePath "D:\Clio\CSP.Clio.StagingSync\CSP.Clio.StagingSync.exe" -RunAs "DOMAIN\svc_clio"
```

Options: `-PublishEveryMinutes 90 -FirstPublishAt 00:30 -DeleteEveryMinutes 60 -FirstDeleteAt 00:15`. Incremental and Delete use "do not start a new instance"; Publish uses "queue" so overlapping slots are not lost. All: no time limit, run when not logged on, start late if the server was off, "Start in" = EXE folder. The account needs SQL access to all four databases, write access to the EXE folder and "Log on as a batch job". Tasks created by hand earlier must be disabled. Logs: `Logs\*-last-run.txt`, replaced at each run.

## 10. Measured timings

On the server (13 November 2026, single database): reporting build about 38 minutes, publish to Dev 4–5 minutes, delete step 15–45 minutes, incremental 14–22 seconds.
Local test of the two-database design (restored copies of the server databases, 15–16 November 2026): incremental on Live about 1 minute (first runs after a gap), full delete pass about 19 minutes, copy Live → Staging 75 seconds for 431,222 rows, reporting build about 7 minutes (92% of it `PopulateTables`; slowest views `VW_Activities` 81 s, `VW_ActivitySummarized` 77 s, `VW_ActivitySummarized_NoBill` 56 s), publisher dry run passed.

## 11. Local test results

- Incremental writes to `Clio_Live` only; `Clio_Staging` stays unchanged. Verified the target database in the log line `Target: ... / Clio_Live`.
- Incremental runs while a delete pass is running and while the publish cycle builds the reports; it skips only during the short copy.
- A second delete pass and a second incremental are skipped while the first is running.
- The cycle stops, with a clear message, when the last incremental (30 min) or the last clean delete pass (240 min) is too old.
- Delete pass found real deletions (for example 46 BankTransactions, 22 Activities, 1 Task) and archived them in `Clio_Live` only; they reach Staging with the next copy.
- A short network drop failed 9 delete chunks (HTTP 0) once; retry for network drops (up to 6 times) and for the token refresh was added afterwards. The delete pass that followed finished with 0 problems.
- `--full-check` (dry run): copy, 76 of 76 endpoints COMPLETE, 10% check PASS, reporting build, `PUBLISH DRY RUN COMPLETE`.
- The real publish (`--confirm-publish`) was started locally but not waited for: the local Dev copy is too slow for the single large transaction. It must be tested on the server (where it took 4–5 minutes).

## 12. Open points

- Failure alerts (email or event log) and dated per-run logs are not built.
- The delete pass uses the same Clio API token as the incremental; heavy ID listing can slow both through Clio's rate limit (retries are built in).
- Untested: the real publish with 0 variance on the server, the three scheduled tasks on the server.
