# Client RDP server – list of changes to make (two-database pipeline)

What this changes: the 5-minute incremental sync and a new background delete task write to a new database `Clio_Live`; the publish cycle (every 90 minutes) copies `Clio_Live` into `Clio_Staging`, checks, builds the reports and publishes to `EKPBI_New_Dev`. All three run at the same time. Read `Pipeline_Workflow.md` for the design.

Everything below is done **on the RDP server**. Items marked **[SQL]** run in SSMS, **[EXE]** run from the EXE folder, **[TS]** are Task Scheduler / PowerShell. Do the steps in order. Plan about 3 hours (most of it is waiting for the first full cycle).

## 0. What is in the package

`CSP.Clio.StagingSync_Server_Package_v3.zip`:
- `Build\` – the new EXE and DLLs (`CSP.Clio.StagingSync.exe`, `.exe.config` with LOCAL connection strings that you must edit, see step 3).
- `Scripts\5_run_fifth__13_Create_Clio_Live__Clio_Staging_to_Clio_Live.sql` (script 13) – the only new SQL script. The other four scripts in that folder (10, 07, 11, 12) are **unchanged** and are already on the server: do NOT run them again (script 12 does not need a re-run).
- `Setup_Scheduled_Tasks.ps1` – creates the three tasks.
- `Server_Change_List.md` (this file), `Pipeline_Workflow.md`, `README_DEPLOY.txt`.

## 1. Before you start (preparation)

1. **Free space.** `Clio_Live` is a copy of `Clio_Staging`: you need space for one backup file plus one more copy of the database (data about as large as `Clio_Staging`, check with `EXEC sp_spaceused` in Clio_Staging) plus log. The server had only about 11 GB free on C:. Put the Live files and the backup on the large drive (for example `E:\SQLData`, `E:\SQLBackup`; the folders must exist and the SQL Server service account needs write access).
2. **Backups.** Take a full backup of `Clio_Staging` and `EKPBI_New_Dev` (keep it until the new setup has run for a day). `EKPBI_New` (Prod) is never touched.
3. **Stop everything that writes.** In Task Scheduler **disable** the old tasks: `CSP.Clio.StagingSync`, `CSP.FullStagingSync`, and any "CSP Clio ..." task. Wait until no `CSP.Clio.StagingSync.exe` process is running (Task Manager / `Get-Process CSP.Clio.StagingSync`). Check `Clio_Staging.control.IncrementalSyncRun` and `control.SyncRun` have no row with Status = `RUNNING` (if one is left from a killed run it is closed automatically later).
4. **Rename the old EXE folder** (for example `Clio_old`) and keep it for rollback.

## 2. Copy the new build

1. Unzip the package. Copy `Build\*` into the fixed EXE folder on the server (for example `D:\Clio\CSP.Clio.StagingSync\`), keep the folder name you use for the Task Scheduler path.
2. Create an empty `Logs` folder next to the EXE if it does not exist (the EXE creates it itself; the account needs write access to the folder).

## 3. Edit the EXE config (`CSP.Clio.StagingSync.exe.config`)

All values go into the config next to the EXE on the server, not into the zip.

| Setting | Value on the server |
|---|---|
| Connection string `EKPBIEntities` | the server SQL instance (replace `(localdb)\MSSQLLocalDB`), `initial catalog=Clio_Staging`, same login as before |
| **Connection string `EKPBIEntitiesLive` (new)** | the same instance and login, `initial catalog=Clio_Live` (copy the `EKPBIEntities` string and change only the catalog) |
| Connection string `ProductionSql` | the same instance, `initial catalog=EKPBI_New_Dev` |
| `StagingDatabaseName` | `Clio_Staging` |
| **`LiveDatabaseName` (new)** | `Clio_Live` (leave empty to run in the old single-database mode) |
| `ComparisonDatabaseName` | `EKPBI_New_Dev` |
| `PublishAfterValidation` | `false` |
| `EnableIncrementalSync` | `true` |
| `IncrementalEndpoints` | all 19 (as shipped) |
| `FullCheckMaxIncrementalAgeMinutes` | `30` (new, default) |
| `FullCheckMaxDeleteAgeMinutes` | `240` (new, default) |
| `FullCheckLockWaitMinutes`, `LiveCopyLockWaitMinutes`, `LiveCopyParallelism`, `LiveCopyTimeoutMinutes`, `DeleteLockWaitMinutes` | `10`, `15`, `4`, `60`, `30` (new, defaults) |
| `ReportingBuildTimeoutMinutes` / `PublishTimeoutMinutes` | `90` / `120` |

The login that runs the EXE must be `db_owner` (or have the equivalent rights) in `Clio_Staging`, `Clio_Live` and `EKPBI_New_Dev`, and the scheduled-task account needs "Log on as a batch job". The restore in step 4 keeps the users of `Clio_Staging` inside `Clio_Live`, so the same login works there.

## 4. [SQL] Create `Clio_Live` (script 13)

1. Open the script 13 file (`Scripts\5_run_fifth__13_Create_Clio_Live...sql`) in SSMS as a **sysadmin**. Before running, edit the two lines at the top:
   - `@BackupFolder` = for example `N'E:\SQLBackup'`
   - `@LiveDataFolder` = for example `N'E:\SQLData'` (data and log file of Clio_Live)
2. Run it. It takes a COPY_ONLY backup of `Clio_Staging`, restores it as `Clio_Live`, sets SIMPLE recovery, creates `control.DeleteRun` in `Clio_Live` and `control.LiveCopyRun` / `control.LiveCopyTable` in `Clio_Staging`. Nothing else in `Clio_Staging` changes. It is safe to run twice (if `Clio_Live` exists it only adds the missing tables).
3. Check the result grid at the end: **StagingRows = LiveRows for all 20 tables**.
4. **Delete the backup file** named in the output (`...Clio_Live_seed_from_Clio_Staging.bak`).
5. Check `SELECT name, recovery_model_desc, state_desc FROM sys.databases WHERE name IN ('Clio_Staging','Clio_Live','EKPBI_New_Dev')` – Live: SIMPLE, ONLINE.
6. Check the Clio_Live files are on the large drive: `SELECT name, physical_name FROM Clio_Live.sys.database_files`.
7. Add `Clio_Live` to the server's normal database backup job.

Because `Clio_Live` is a copy taken now, the incremental watermarks in it continue where the old incremental stopped. If the old incremental had been stopped for days, raise `IncrementalInitialLookbackHours` is NOT needed (the watermark rows exist; the first run simply reads the gap, which can take longer than 5 minutes once).

## 5. [EXE] Manual test on the server (from the EXE folder, in this order)

Use a normal command prompt. Do not click into the console window while a command runs (a click pauses the program; press Enter to continue).

| # | Command | Expected |
|---|---|---|
| 1 | `CSP.Clio.StagingSync.exe --help` | usage lists `--delete-sync` and `--full-check` |
| 2 | `CSP.Clio.StagingSync.exe --incremental` | first line `Target: <server> / Clio_Live`, endpoints OK, `INCREMENTAL SYNC COMPLETE`. Time it (normally under 1 minute; the first run after a gap longer) |
| 3 | `CSP.Clio.StagingSync.exe --incremental` again | completes quickly |
| 4 | `CSP.Clio.StagingSync.exe --reconcile-deletes --only Users,Tasks` | report only; look at `Missing` column. Users should show 0 missing |
| 5 | `CSP.Clio.StagingSync.exe --delete-sync` (leave it running, about 15 to 45 minutes) | ends with `0 problem(s)` |
| 6 | while 5 runs, open a second prompt: `CSP.Clio.StagingSync.exe --incremental` | **runs** (does not wait) and completes |
| 7 | while 5 runs, second prompt: `CSP.Clio.StagingSync.exe --delete-sync` | `DELETE PASS SKIPPED: another delete pass is still running` |
| 8 | after 5: `SELECT TOP 3 * FROM Clio_Live.control.DeleteRun ORDER BY StartedAtUTC DESC` | latest row Status = `COMPLETE` |
| 9 | `CSP.Clio.StagingSync.exe --full-check` (dry run, about 45 to 60 minutes) | `Target: ... / Clio_Staging`; step 1 prints the copy of 20 tables; 10% check PASS; reporting build; `PUBLISH DRY RUN COMPLETE`. Note the copy time |
| 10 | `CSP.Clio.StagingSync.exe --full-check --confirm-publish` | `PUBLISH COMPLETE`. In Dev: `SELECT TOP 1 * FROM EKPBI_New_Dev.control.PublishRun ORDER BY StartedAtUTC DESC` Status = `COMPLETE` |
| 11 | Run `BillingCollDailyCheck.sql` on `EKPBI_New_Dev` and on `EKPBI_New` | **variance 0** |
| 12 | In `Clio_Staging`: `SELECT OBJECT_ID('dbo.VW_PartnerFact_Billing_Collections_Table')` and the `%[_]Table` count | table exists; about 35 or more `_Table` tables |

If step 9 stops at "the last clean delete pass is missing", step 5 did not finish with COMPLETE. If it stops with "last completed incremental is N minutes old", run step 2 again.

## 6. [TS] Replace the old tasks with the three new ones

1. Delete (do not only disable) the old hand-made tasks `CSP.Clio.StagingSync` and `CSP.FullStagingSync` and any old "CSP Clio ..." task.
2. Open an **elevated** PowerShell in the EXE folder and run (use the real service account; you are asked for its password):
   ```
   .\Setup_Scheduled_Tasks.ps1 -ExePath "D:\Clio\CSP.Clio.StagingSync\CSP.Clio.StagingSync.exe" -RunAs "DOMAIN\svc_clio"
   ```
   Optional: `-PublishEveryMinutes 90 -FirstPublishAt 00:30 -DeleteEveryMinutes 60 -FirstDeleteAt 00:15`.
3. In Task Scheduler check the three tasks:

| Task | Arguments | Trigger | "If the task is already running" |
|---|---|---|---|
| CSP Clio Incremental | `--incremental` | every 5 minutes | Do not start a new instance |
| CSP Clio Delete | `--delete-sync` | every 60 minutes | Do not start a new instance |
| CSP Clio Publish | `--full-check --confirm-publish` | every 90 minutes | Queue a new instance |

   For each: "Run whether user is logged on or not", "Start in" = the EXE folder, no execution time limit.
4. Test each once from Task Scheduler (right click → Run) and look at the **Last Run Result** (0 = OK) and the files in `Logs\`.

## 7. After going live – checks (first day)

Run these in SSMS.

```sql
-- incremental: should be one row about every 5 minutes, seconds long
SELECT TOP 12 StartedAtUTC, CompletedAtUTC, DATEDIFF(second,StartedAtUTC,CompletedAtUTC) AS secs, Status FROM Clio_Live.control.IncrementalSyncRun ORDER BY StartedAtUTC DESC;
-- delete passes
SELECT TOP 6 StartedAtUTC, CompletedAtUTC, Status, RowsMoved, Problems FROM Clio_Live.control.DeleteRun ORDER BY StartedAtUTC DESC;
-- copies Live -> Staging (duration and rows per cycle)
SELECT TOP 6 StartedAtUTC, CompletedAtUTC, DATEDIFF(second,StartedAtUTC,CompletedAtUTC) AS secs, Status, RowsCopied FROM Clio_Staging.control.LiveCopyRun ORDER BY StartedAtUTC DESC;
-- publishes to Dev
SELECT TOP 6 StartedAtUTC, CompletedAtUTC, DATEDIFF(second,StartedAtUTC,CompletedAtUTC) AS secs, Status FROM EKPBI_New_Dev.control.PublishRun ORDER BY StartedAtUTC DESC;
```

What to expect: incrementals COMPLETE in seconds; a publish cycle takes about 45 to 55 minutes and a COMPLETE PublishRun appears every 90 minutes; delete passes COMPLETE with `Problems = 0`. During the copy (a few minutes at the start of each cycle) one or two incremental runs show "SKIPPED" in their log – that is normal. Also check free disk space and the size of the `Clio_Live` and `Clio_Staging` log files after the first day.

If a log shows `FULL CHECK STOPPED: the last completed incremental sync is N minutes old` or `the last clean delete pass is ... old`, the named task is not running or failing – check that task's last result and its log in `Logs\`.

## 8. Rollback (if something goes wrong)

1. Disable the three new tasks.
2. In the EXE config set `LiveDatabaseName` to empty (single-database mode: everything runs in `Clio_Staging` as before) **or** re-enable the old tasks with the old EXE folder from step 1.4.
3. `Clio_Staging` and `EKPBI_New_Dev` were never changed by the setup except two audit tables in `Clio_Staging`; `Clio_Live` can stay or be dropped (`DROP DATABASE Clio_Live`). Dev keeps its last published data.

## 9. Summary of what changes on the server

| Area | Change |
|---|---|
| Databases | **New** `Clio_Live` (copy of Clio_Staging, SIMPLE recovery). `Clio_Staging` gets 2 audit tables. `EKPBI_New_Dev` and `EKPBI_New`: no change |
| SQL scripts | Run only `13_Create_Clio_Live.sql`. Scripts 10, 11, 12 unchanged |
| Files | New EXE build, edited `.exe.config`, `Setup_Scheduled_Tasks.ps1` |
| Config | New: `LiveDatabaseName`, connection string `EKPBIEntitiesLive`, and the `FullCheck*`, `LiveCopy*`, `Delete*` settings (defaults are fine) |
| Task Scheduler | Old tasks removed; **3 new tasks**: Incremental (5 min), Delete (60 min), Publish (90 min) |
| Disk | One more copy of the database (about the size of Clio_Staging) plus its log |
| Backups | Add `Clio_Live` to the backup job |
| Permissions | The EXE login: `db_owner` on Clio_Live, Clio_Staging, EKPBI_New_Dev; task account: "Log on as a batch job" |
| Commands | New: `--delete-sync`, `--reconcile-deletes --only ...`. `--full-check` now starts with the copy from Live and no longer applies deletions itself |
