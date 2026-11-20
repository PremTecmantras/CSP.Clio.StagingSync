# Cycle workflow: one task, two EXEs, Clio_Staging only

This replaces the "two scheduled tasks" setup of `Pipeline_Workflow.md` (sections 5 to 8 and the scheduler section). There is **no Clio_Live**; everything
happens in `Clio_Staging`, and the publish target stays `EKPBI_New_Dev`.

> **Updated 2026-11-19.** Changes since the first version: a new supervisor log file for every run (section 5b); token refresh under a lock and a 401 retry (section 9);
> the reporting build happens once, not twice (section 10); the delete pass can run as its own job (`CycleRunsDelete`, section 11); a full list of command-line arguments and
> of every `appSettings` key (sections 5a and 8); troubleshooting (section 12). Where this text and the code differ, the code and `App.config` win.

## 1. What runs

One Windows task (`CSP Clio Cycle`) starts `CSP.Clio.StagingSync.exe --cycle`. It repeats this cycle on its own:

```
t=0        incremental window   (a pass every 5 min for 55 min, then it ends)          CSP.Clio.StagingSync.exe --incremental --window-minutes 55 --interval-minutes 5
t=0        delete pass          (once, in parallel, about 20-45 min, ends by itself)   CSP.Clio.StagingDelete.exe
t=55       both are over and the delete pass is COMPLETE
           publish              (snapshot, 10% check, reporting build, publish)        CSP.Clio.StagingSync.exe --full-check --confirm-publish
t≈1h40     gap of 2 min, next cycle
```

Every part is a separate process started by the supervisor (hidden window, own log). The supervisor itself holds only one small lock, so a second `--cycle` exits at once.

## 2. Nothing can overlap (SQL applocks in Clio_Staging, released automatically if a process dies)

| Lock | Mode | Held by |
|---|---|---|
| `ClioStagingData` | **Shared** | an incremental pass; a whole delete pass (they may run together) |
| `ClioStagingData` | **Exclusive** | the publish cycle for its whole run; the first load |
| `ClioIncrementalRun` | Exclusive | one incremental pass at a time |
| `ClioDeleteRun` | Exclusive | one delete pass at a time |
| `ClioStagingPipeline` | Exclusive | one publish cycle at a time |
| `ClioCycleSupervisor` | Exclusive | the `--cycle` supervisor |

- **Publish checks delete:** it stops (exit code 3, nothing changed) if a delete pass is running, and it requires the latest `control.DeleteRun` row to be
  `COMPLETE` and not older than `FullCheckMaxDeleteAgeMinutes` (240). It then takes `ClioStagingData` exclusively, so no incremental or delete pass can start until it ends.
- **Delete checks publish:** at its start it needs `ClioStagingData` shared; while a publish runs that fails and the pass stops at once (exit 0, nothing changed).
  Because the pass holds the shared lock for its whole run, a publish can never start in the middle of it.
- **Incremental passes** skip quietly (exit 0) while a publish runs; the next pass reads everything changed since the saved watermark, so nothing is lost.

## 3. Rules of the supervisor

- The publish starts only when the incremental window is over **and** the delete pass of this cycle is `COMPLETE` (status in `control.DeleteRun`).
- A failed delete pass is started once more if at least `CycleDeleteRetryMinWindowMinutes` (25) of the window are left; if it still is not `COMPLETE`, **no publish this cycle**
  (the target keeps its last good data). The next cycle tries again.
- Hang protection: a delete pass longer than `CycleDeleteWatchdogMinutes` (90) or a publish longer than `CyclePublishWatchdogMinutes` (180) is ended; the cycle goes on.
- Every cycle is recorded in `control.CycleRun` (start, incremental end, delete status, publish start/end/exit code, outcome). Outcomes: `PUBLISHED`, `PUBLISH_FAILED`,
  `PUBLISH_SKIPPED_DELETE_NOT_COMPLETE`, `PUBLISH_DISABLED`, `CANCELLED`, `ERROR`.
- Stop cleanly: create the file `cycle.stop` next to the EXE (ends after the current cycle), or stop the task (child processes end with it, they are in a job object).
- `--cycle --once` runs a single cycle and ends (for testing).

## 4. Delete pass (CSP.Clio.StagingDelete.exe)

Same logic as before (full ID listing per company and endpoint, HTTP 404 confirmation, safety limits, soft delete into `<Table>_Deleted`), with two changes:
the **companies are listed in parallel** (`DeleteMaxParallelCompanies`, 4; every company has its own Clio token, and the listing speed of about 50 pages a minute is the
rate limit, so the pass takes about as long as the largest company), and the pass holds the shared lock for its whole run. `--report-only` changes nothing;
`--only Users,Tasks` limits the endpoints (recorded as `PARTIAL`, never counts as clean).

The EXE is built from the same source files as the main EXE (linked, not copied) and **shares its `App.config`**, so both always use the same connection strings and settings.
The build writes both into `CSP.Clio.StagingSync\bin\<Configuration>\`; copy both EXEs, both `.config` files and the DLLs to the server folder.

## 5. Settings (App.config)

| Key | Default | Meaning |
|---|---|---|
| `CycleIncrementalWindowMinutes` | 55 | length of the incremental window |
| `CycleIncrementalIntervalMinutes` | 5 | a pass every N minutes (start to start) |
| `CycleDeleteRetryMinWindowMinutes` | 25 | a failed delete pass is retried only if this much of the window is left |
| `CycleDeleteWatchdogMinutes` / `CyclePublishWatchdogMinutes` | 90 / 180 | hang protection |
| `CycleIncrementalWatchdogSlackMinutes` | 30 | the incremental process is ended after window + slack |
| `CycleGapMinutes` | 2 | pause between cycles |
| `CycleShortCycleMinutes` / `CycleShortCycleWaitMinutes` | 2 / 5 | a cycle shorter than the first value did nothing useful; the next one waits the second value |
| `CycleHeartbeatMinutes` | 5 | a `STATUS:` line in the cycle log every N minutes (0 = off) |
| `CycleChildOutputInLog` | important | `important`, `all` or `none`: how much of the parts' output goes into the cycle log |
| `LogDirectory` | (empty) | the one folder for all logs (empty = `Logs` next to the EXE) |
| `LogRetentionDays` | 30 | cycle logs and cycle folders older than this are deleted (0 = never) |
| `CycleDeleteExeName` | CSP.Clio.StagingDelete.exe | must be in the same folder |
| `CyclePublishEnabled` | true | false = run the cycle without publishing |
| `FullCheckMaxDeleteAgeMinutes` | 240 | the publish needs a COMPLETE delete pass newer than this (0 = no age check) |
| `DeleteMaxParallelCompanies` | 4 | companies listed at the same time |

The table above lists the cycle settings. **Every key of `appSettings` is explained in section 5a.**

## 5a. Complete reference: every `appSettings` key in App.config

`App.config` is shared by both EXEs (the delete project links it). After a change, **rebuild both projects** (`CSP.Clio.StagingSync.csproj` and `CSP.Clio.StagingDelete\CSP.Clio.StagingDelete.csproj`)
and check that `bin\...\CSP.Clio.StagingSync.exe.config` and `...StagingDelete.exe.config` hold the new value. On the server edit both `.config` files (or copy both).
Values shown are the ones in the file today; "default" is what the code uses when the key is missing or invalid.

**Databases and API**

| Key | Value / default | Meaning |
|---|---|---|
| `StagingDatabaseName` | `Clio_Staging` | the staging database; the code refuses to run against any other name |
| `ComparisonDatabaseName` | `EKPBI_New_Dev` | the publish target database (used for the 10% comparison and the publish) |
| `ClioApiBaseUrl` | `https://eu.app.clio.com/api/v4/` | Clio API root (must be absolute HTTPS) |
| `DocumentsRootPath` | `Documents` | folder for downloaded documents (legacy) |
| `BulkBatchSize.<Table>` | 1000 | rows per bulk insert, for Activities, Allocations, BankTransactions, Bills, CalendarEntries, LineItems, Contacts, Matters, MatterNotes, Tasks |

**First load (the no-argument full extraction)**

| Key | Value / default | Meaning |
|---|---|---|
| `MaxParallelCompanies` | 4 | companies extracted at the same time (each has its own token) |
| `AbandonPreviousRunningRun` | true | close a previous RUNNING run that is proven dead and go on |
| `PublishAfterValidation` | true | publish after the first load validated (with this true the command line needs no `--confirm-publish`) |
| `FirstLoadLastSyncOffsetMinutes` | 5 | the first load sets LastSyncTime this many minutes before it started |

**Incremental**

| Key | Value / default | Meaning |
|---|---|---|
| `EnableIncrementalSync` | true | `--incremental` refuses to run when this is not `true` |
| `IncrementalEndpoints` | all 19 | comma list of endpoints polled with `updated_since` |
| `IncrementalOverlapMinutes` | 10 | each pass re-reads this overlap so changes during the previous pass are not missed |
| `IncrementalInitialLookbackHours` | 72 | the first poll of an endpoint starts this many hours back |
| `IncrementalRebuildReporting` | false | true = rebuild the reporting tables after every pass (normally false; the publish does it) |

**Publish cycle (`--full-check`)**

| Key | Value / default | Meaning |
|---|---|---|
| `FullCheckLockWaitMinutes` | 10 | how long a publish waits for a running pass or cycle before it stops |
| `FullCheckMaxDeleteAgeMinutes` | 240 | the latest delete pass must be COMPLETE and younger than this (0 = no age check) |
| `StagingReportingBuild` | auto | step 4, the reporting build in Clio_Staging: `auto` = only when the target procedure does not rebuild itself, `true` = always, `false` = never (section 10) |
| `ReportingBuildTimeoutMinutes` | 90 | timeout of one reporting build step in staging |
| `ReportingBuildProgressSeconds` | 60 | a progress line (views refreshed so far) during the long `PopulateTables` step (0 = off) |
| `PublishTimeoutMinutes` | 120 | SQL timeout of `PublishApprovedClioStagingRun`; after it the transaction is rolled back |

**Delete pass**

| Key | Value / default | Meaning |
|---|---|---|
| `DeleteMaxParallelCompanies` | 4 | companies listed at the same time (1 = one after the other) |
| `DeleteUsesOwnClioToken` | **false** | true = the delete pass asks Clio for a second access token. **Keep false**: Clio ends the older token when a new one is issued (section 9) |
| `DeleteProgressSeconds` | 60 | progress line while a long ID list is read (0 = off) |
| `DeleteReconcileMaxPercent` | 5 | endpoints where more than this share of a company's rows is missing in Clio are never touched |
| `DeleteReconcileMaxConfirmations` | 2000 | at most this many records are confirmed one by one |
| `DeleteReconcileSmallTableAllowance` | 25 | small tables: up to this many missing records are always confirmed one by one |
| `DeleteReconcileListingAuthoritative` | Users | endpoints where Clio's ID listing alone decides (no per-record check) |
| `DeleteReconcileEndpoints` | (empty) | limit the pass to some endpoints; a limited pass is recorded PARTIAL and never counts as clean |
| `DeleteLockWaitMinutes` | 30 | wait for the data lock when the reconciler is called without holding it |

**Cycle supervisor (`--cycle`)**

| Key | Value / default | Meaning |
|---|---|---|
| `CycleIncrementalWindowMinutes` | 55 (file currently 5 = test value) | length of the incremental window |
| `CycleIncrementalIntervalMinutes` | 5 | a pass every N minutes |
| `CycleRunsDelete` | true | **false = the delete EXE is its own scheduled job** and the cycle only does incremental + publish (section 11) |
| `CycleDeleteRetryMinWindowMinutes` | 25 | a failed delete pass is retried once only if this much of the window is left |
| `CycleDeleteWatchdogMinutes` | 90 | a delete pass longer than this is ended |
| `CyclePublishWatchdogMinutes` | 180 | a publish longer than this is ended (**ending it in the middle rolls the publish back**) |
| `CycleIncrementalWatchdogSlackMinutes` | 30 | the incremental process is ended after window + slack |
| `CycleGapMinutes` | 2 | pause between cycles |
| `CycleShortCycleMinutes` / `CycleShortCycleWaitMinutes` | 2 / 5 | a cycle shorter than the first value did nothing useful; the next waits the second value |
| `CyclePublishEnabled` | true | false = run the cycle without publishing |
| `CycleDeleteExeName` | `CSP.Clio.StagingDelete.exe` | the delete EXE (same folder as the main EXE) |
| `CycleHeartbeatMinutes` | 5 | a status line in the cycle log every N minutes (0 = off) |
| `CycleChildOutputInLog` | important | `important`, `all` or `none` |
| `LogDirectory` | (empty) | the one folder for all logs (empty = `Logs` next to the EXE) |
| `LogRetentionDays` | 30 | logs older than this are deleted (0 = never) |

## 5b. Logs: one folder, defined in one place

Every log of both EXEs goes below ONE folder: `LogDirectory` in `App.config` (empty = the folder `Logs` next to the EXE). The code decides every log path in one class, `LogPaths.cs`.
A line of a log is written in one place only: the part writes its complete log itself, and the cycle log holds a readable extract.

```
Logs\
  CSP.Clio.StagingSync-cycle-yyyyMMdd-HHmmss.txt  the cycle supervisor: a NEW file for every supervisor start and every following cycle  -> THE FILE TO READ
  Cycles\yyyyMMdd-HHmmss\                        one folder per cycle (its start time)
      incremental.txt   delete.txt   publish.txt    the complete log of each part (a retried delete: delete-attempt2.txt)
  Manual\                                        parts started BY HAND (not by the cycle); replaced by the next run of the same part
      incremental-last-run.txt  delete-last-run.txt  full-check-last-run.txt  reconcile-deletes-last-run.txt  last-run.txt
```

The cycle starts each part with `--log-file "<path>"`; a part without that argument (started by hand) writes to `Manual`.

| File | Content | Kept |
|---|---|---|
| `CSP.Clio.StagingSync-cycle-yyyyMMdd-HHmmss.txt` | **The one file to read** (one per supervisor start; with `--cycle --once` that is one cycle). Columns: time, part, message. A `====` banner starts every cycle and a `CYCLE SUMMARY` table ends it (when each part started, how long it took, its result, and what the outcome means). Between them: the steps, a `status` line every `CycleHeartbeatMinutes` (which parts run, for how long, their last output line), the important output of each part, and `WARNING` / `PROBLEM` lines for anything that needs a look | one file per supervisor start or cycle, deleted after `LogRetentionDays` |
| `Cycles\yyyyMMdd-HHmmss\incremental.txt`, `delete.txt`, `publish.txt` | The **complete** log of each part of that cycle, written by the part itself while it runs (open it to follow a part live) | deleted after `LogRetentionDays` |
| `Manual\*-last-run.txt` | The log of a part you started by hand | replaced by the next run of the same part |
| table `control.CycleRun` (Clio_Staging) | One row per cycle: start, incremental end and exit code, delete status, publish start/end/exit code, outcome | kept |

`CycleChildOutputInLog` = `important` leaves out only routine lines in the cycle log (an endpoint with 0 changed rows, delete results with nothing missing, list reads under 10 seconds,
passed validation rows); everything else, including every error and every number that matters, is there. `all` copies every line (about 1,500 lines per cycle), `none` copies nothing.
Example of a cycle in the log:

```
==============================================================================
CYCLE   started 2026-11-17 18:23:33     id 45ec3f93
==============================================================================
18:23:33  cycle         Plan: incremental window of 55 min (a pass every 5 min) and the delete pass in parallel.
                        When both are finished and the delete pass is COMPLETE, the publish starts.
18:23:33  incremental   STARTED (process 15516): CSP.Clio.StagingSync.exe --incremental --window-minutes 55 --interval-minutes 5
18:23:34  delete        STARTED (process 17072): CSP.Clio.StagingDelete.exe
18:23:34  incremental   --- Incremental pass 1 at 00:00:00 of 00:55:00 ---
18:23:52  incremental   Incremental Activities - KAT: 4 changed row(s).
18:28:40  status        incremental  running 00:05:07
                            last output: Incremental window pass 2 at 00:05:00
                        delete       running 00:05:06
                            last output: ... Activities / MBLLF list read in 04:02
18:46:10  delete        Delete reconciliation finished: 0 record(s) moved to *_Deleted, 0 problem(s).
18:46:11  delete        FINISHED: COMPLETE after 00:22:37 (exit code 0).
19:18:40  incremental   FINISHED after 00:55:07 (exit code 0).
19:18:41  cycle         Incremental window over and delete pass COMPLETE: the publish starts now.
19:18:41  publish       STARTED (process 20344): CSP.Clio.StagingSync.exe --full-check --confirm-publish
...
20:01:30  publish       FINISHED after 00:42:49 (exit code 0).
------------------------------------------------------------------------------
CYCLE SUMMARY   outcome: PUBLISHED
  The data was published to the target database.

  Part          Started    Duration   Result
  Incremental   18:23:33   00:55:07   OK
  Delete        18:23:34   00:22:37   COMPLETE
  Publish       19:18:41   00:42:49   OK
  Whole cycle   18:23:33   01:38:57
  Complete logs of the parts: Logs\Cycles\20261117-182333
------------------------------------------------------------------------------
```

A problem is easy to find: search the file for `PROBLEM` or `WARNING`, or look at the `outcome:` of the summary tables.

## 6. Deploy on the server

1. Run `14_Create_LineItems_Bill_Index.sql` in Clio_Staging (speeds up the reporting build; see the note in the script). Optional but strongly advised: the build is about 38 of the 42 minutes of a publish.
2. Copy the new build (both EXEs and their `.config` files) into the server folder. Check the connection strings in `App.config`.
3. Disable or delete the old tasks (`CSP.Clio.StagingSync`, `CSP.FullStagingSync`, `CSP Clio Incremental`, `CSP Clio Publish`). `Setup_Scheduled_Tasks.ps1` removes the known ones.
4. Test by hand, in this order (each in its own console, from the EXE folder):
   - `CSP.Clio.StagingDelete.exe --report-only`, then `CSP.Clio.StagingDelete.exe` (must end `COMPLETE` in `control.DeleteRun`).
   - `CSP.Clio.StagingSync.exe --incremental --window-minutes 10 --interval-minutes 5` (3 passes).
   - While the delete EXE runs, start `CSP.Clio.StagingSync.exe --full-check`: it must stop with "a delete pass is running" (exit code 3). While a `--full-check` runs, start the delete EXE: it must stop with "a publish cycle ... is running".
   - `CSP.Clio.StagingSync.exe --cycle --once` with `CycleIncrementalWindowMinutes` 10 and `CyclePublishEnabled` false, then once with publish on.
5. Run `Setup_Scheduled_Tasks.ps1 -ExePath ...` to create the one task, then start it.

## 7. Not yet tested on the server (to check at the first cycles)

- The delete pass (about 50 Clio requests per minute per token) runs at the same time as the incremental passes on the same tokens: the incremental passes may meet HTTP 429
  and wait. Watch the pass durations in the incremental log during the first cycle. If it hurts, lower `DeleteMaxParallelCompanies` or run the delete pass first.
- The real durations of one cycle (expected about 1 h 40 min: 55 min window, then about 42 min publish; the index script should shorten the publish).
- The behaviour after a server restart (the task starts at start-up; an old supervisor lock is gone because applocks end with their session).
- The token coordination of section 9 and the copy-only procedure of section 10 are written but were **not yet run end to end**: check the first cycles for 401 lines and for the publish duration.

## 8. Command-line arguments

`CSP.Clio.StagingSync.exe` (the main EXE). Run from the EXE folder. Without a recognised argument it does the **first load** (see the first row).

| Argument | What it does |
|---|---|
| *(none)* | **First load only**: extract all 19 endpoints, build the reporting tables, validate, publish if configured. Refuses to run when the raw tables already hold data |
| `--force-full-extract` | with no other argument: rebuild the raw tables from scratch (clears them) |
| `--abandon-stale` | close a confirmed-dead RUNNING run, then extract |
| `--publish-after-validation --confirm-publish` | extract, validate, then publish the new run |
| `--incremental` | one incremental pass into staging; never publishes. Needs `EnableIncrementalSync=true` |
| `--incremental --window-minutes N --interval-minutes M` | repeat the pass every M minutes (default 5) for N minutes in one process, then end (this is what the cycle starts) |
| `--full-check` | the publish cycle **dry run**: needs a recent COMPLETE delete pass; catch-up, snapshot, 10% check, (reporting build if needed), publisher dry run. Never extracts or clears |
| `--full-check --confirm-publish` | the same, then the real publish |
| `--full-check ... --with-deletes` | manual use: run the delete reconciliation inside the run instead of requiring a recent delete pass (slow: reads every ID from Clio) |
| `--cycle` | the supervisor: incremental window + delete EXE in parallel, then publish, repeated. One per machine |
| `--cycle --once` | one cycle, then end (testing) |
| `--reconcile-deletes [--only A,B]` | report records deleted in Clio, change nothing |
| `--reconcile-deletes --apply [--only A,B]` | move confirmed deletions to the `*_Deleted` tables |
| `--validate [--run-id GUID]` | read-only staging/production validation |
| `--preflight` | check raw schemas and readiness, change nothing |
| `--status` | show recent staging and publication audit state |
| `--token-test` | show how Clio treats a second access token per company (a few tiny requests) |
| `--log-file "path"` | write this run's log to `path` (the cycle sets it for its parts; by hand it defaults to `Logs\Manual`) |
| `--help`, `-h`, `/?` | usage |

`CSP.Clio.StagingDelete.exe` (one delete pass, then it ends): `--report-only` (change and record nothing), `--only Users,Tasks` (limit to some endpoints; recorded PARTIAL).

Exit codes of the publish: `0` ok, `1` failed, `2` blocked or could not start, `3` stopped on purpose (a delete pass is running, no recent COMPLETE delete pass, an extraction running) with nothing changed.

Examples:

```powershell
.\CSP.Clio.StagingSync.exe --cycle                                  # the scheduled task
.\CSP.Clio.StagingSync.exe --cycle --once                           # one test cycle
.\CSP.Clio.StagingSync.exe --incremental                            # one pass now
.\CSP.Clio.StagingSync.exe --full-check --confirm-publish           # publish now (needs a recent COMPLETE delete pass)
.\CSP.Clio.StagingSync.exe --full-check --with-deletes --confirm-publish   # everything in one process, slow
.\CSP.Clio.StagingDelete.exe                                        # one delete pass
```

## 9. Clio tokens and refresh

- Clio access tokens stop working after a while; a **refresh token** gives a new one. The tokens are stored per company in `dbo._EKPCompanies` (`ClioToken`, `ClioRefreshToken`).
- **Clio ends the older access token when a new one is issued** (observed in the logs; not documented by Clio). The incremental passes, the delete pass and the publish are separate
  processes that read the same stored token, so if one of them refreshed while another was still using the old token, the other got **HTTP 401** (seen as `INCREMENTAL FAILED ... HTTP 401`
  and `RECONCILE FAILED ... HTTP 401`).
- What the code does now (`ClioTokens.cs`):
  1. A token counts as dead only when a small request (`users/who_am_i`) returns 401/403. A network error or a 5xx does not trigger a refresh.
  2. A refresh happens under a **lock file per company** (shared by all processes of the machine). After taking the lock the database is read again: if another process already refreshed,
     its token is used and no second refresh is made.
  3. The delete pass retries an endpoint once after a 401 (it takes the current token or refreshes once).
  4. `DeleteUsesOwnClioToken=false`: no second token is requested (a second token would end the stored one).
- **Rate limiting (HTTP 429)** is a separate matter: a refresh does not help. The delete pass reads about 50 pages a minute, which is Clio's limit per token, so waits of 30-60 s
  (`waited for Clio's rate limit 3x`) are normal. The code reads `Retry-After` and retries up to 5 times. Reduce the load with `DeleteMaxParallelCompanies`.
- If the same Clio refresh tokens are also used by another system (for example the production DLLs on the server), **their refreshes can end the tokens stored here**.
  Use a separate Clio app / refresh token for staging if you see 401 with no overlap of your own jobs.

## 10. Publish and the reporting build (PopulateTables): once, not twice

There are two ways to organise the reporting tables. The EXE detects which one the target database uses by reading the text of `dbo.PublishApprovedClioStagingRun` (comments ignored):

| Deployed procedure | Where the 34 views are built | Step 4 in Clio_Staging |
|---|---|---|
| **Script 12** (`12_Create_Staging_Publisher_EKPBI_New_Dev_Manual.sql`), copy | once, in **Clio_Staging**; the procedure only copies the raw tables, the reporting tables, `Matters_Detailed_Custom_Fields`, `Expected_Hours_Daily`, `UsersGroupReportTo` and the freshness metadata, in one transaction | **runs** (about 22 minutes) |
| Older procedure that executes `PopulateTables`, rebuild | once, in **EKPBI_New_Dev**, inside the publish transaction | **skipped** (building it in staging as well would be the same work twice) |

- `StagingReportingBuild=auto` follows the table above; `true` always builds in staging, `false` never does.
- **Recommended: script 12.** The long work runs in staging and the target is locked only for the copy, so a lost connection costs minutes of rollback, not an hour.
- Old `CSP.Clio.Sync` did the same work once (`ConvertViewsToTable`: `SP_CustomExecAfterSync`, `SP_InsertIntoMattersDetailedCustomFields`, `PopulateMatterNotesView`,
  `SP_UpdateNonBillableActivities`, one `PopulateTables` for all active views, `UpdateExpectedHoursDaily`, `SP_UpdateNonBillableActivities`, `SP_AddUserGroupReportTo`; then `SyncMatters`:
  `PopulateTables` for `VW_Users`, `VW_Contacts`, `VW_Folders`, `VW_PracticeAreas`, `VW_Matters`). The repeated views inside one build are inherited from it.
- Never close the console or stop the supervisor while step 5/5 runs: the connection drops and SQL Server rolls the whole publish back (nothing is committed; the rollback takes about as long as the work done).

## 11. Running the delete pass as its own job

Set `CycleRunsDelete=false` and schedule `CSP.Clio.StagingDelete.exe` separately (for example every 2 hours; one pass takes 10-90 minutes):

- The cycle then runs only the incremental window and the publish; its summary shows the delete part as "separate job".
- The publish still needs the latest COMPLETE delete pass to be younger than `FullCheckMaxDeleteAgeMinutes`, and it stops (exit 3) if a pass runs at that moment. The delete job stops quietly if a publish runs.
- Run the delete EXE once by hand first, until `control.DeleteRun` shows COMPLETE; until then every publish stops with "no delete pass recorded".
- Incremental and delete share the data lock and may run together.

## 12. Troubleshooting

| Message / sign | Cause | What to do |
|---|---|---|
| `INCREMENTAL FAILED ... HTTP 401` or `RECONCILE FAILED ... HTTP 401` | a process used a token that another process ended by refreshing | section 9; make sure both EXEs are rebuilt, `DeleteUsesOwnClioToken=false`, only one supervisor runs |
| `waited for Clio's rate limit` | Clio's 429, about 50 pages a minute per token | normal; lower `DeleteMaxParallelCompanies` if it hurts |
| `Msg 51031 Another publication is already running` | the application lock `ClioStagingToEKPBIPublish` is held by a session | find it with `sys.dm_tran_locks` (resource type APPLICATION); wait if it is a running publish; a session in `rollback` must finish first |
| `Msg 51049 A staging incremental sync is RUNNING` (script 12) | a row in `Clio_Staging.control.IncrementalSyncRun` has Status RUNNING | stop the cycles and wait; never edit the status of a live run |
| `Msg 51042 Current staging rows no longer match the approved run audit` | staging changed after the snapshot (incremental passes ran) | take a new snapshot: `--full-check --confirm-publish` |
| `Msg 51048 Another raw publication lease is already RUNNING` | `Clio_Staging.control.PublishLease` still RUNNING after a lost publish | check no session holds the publish lock, then call `Clio_Staging.control.EndRawPublishLease` with the PublishID and status FAILED, and set the `control.PublishRun` row to FAILED |
| session in status `rollback` | the publish connection was lost; SQL Server is undoing it | wait; `KILL <spid> WITH STATUSONLY` shows progress; killing does not speed it up |
| `FULL CHECK STOPPED: a delete pass is running` / `no delete pass has been recorded` | the publish rule of section 2 | wait for the pass, or run the delete EXE, or use `--with-deletes` |
| `PUBLISH_SKIPPED_DELETE_NOT_COMPLETE` | the delete pass of the cycle did not finish COMPLETE | read `delete.txt` of the cycle folder; the next cycle tries again |
| two supervisors in the logs | two `--cycle` started | keep one; the second should exit at once ("another --cycle supervisor is already running") |
| build fails with "file is locked" | a cycle is running | stop it (`cycle.stop`), then build both projects |

## 13. Change log

- **2026-11-18/19**: token refresh under a cross-process lock, 401 retry in the delete pass, stricter dead-token check, `DeleteUsesOwnClioToken=false`; a new supervisor log file per start / cycle;
  step 4 skipped when the target procedure rebuilds itself (`StagingReportingBuild`); `CycleRunsDelete` for a separate delete job; this document updated.
