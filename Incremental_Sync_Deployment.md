# Incremental polling deployment

This implementation is for scheduled polling only. It does not create Clio
webhooks, does not require a public web address, and does not publish a partial
incremental result to EKPBI_New_Dev.

## What the incremental task does

`CSP.Clio.StagingSync.exe --incremental` polls `updated_since` for the enabled
endpoints, then replaces matching raw staging rows using `CompanyID` and
`Clio_Internal_ID`. It stores a successful watermark separately for every
company and endpoint. A ten-minute overlap is re-read in the next run, making
the operation idempotent and protecting a record changed at a polling boundary.

The default endpoint set is Activities, CalendarEntries, Tasks, Matters, Bills,
Folders, and Contacts. All other raw tables remain owned by the existing daily
full extraction.

The task never truncates raw staging tables and never runs
`PublishApprovedClioStagingRun`. The daily no-argument full pipeline remains
the only approved production publication and the deletion reconciliation.

For local development, the project's **Debug** configuration has
`--incremental` as its default Start Argument. Pressing F5 in Debug therefore
starts the incremental poll. The Release configuration and the deployed daily
Task Scheduler full task have no arguments.

## Deployment order

1. Deploy the Release EXE, its config file, DLLs, and the SQL script together.
2. Run `10_Create_Incremental_Sync_Control.sql` once in `Clio_Staging` as the
   DBA. The script adds only `control` audit and watermark tables.
3. Leave `EnableIncrementalSync` as `false` until the test below is approved.
4. Make a non-production change in Clio to one enabled record, such as a test
   matter or activity.
5. Set `EnableIncrementalSync` to `true` in the deployed EXE config.
6. Run `CSP.Clio.StagingSync.exe --incremental` manually once and check:
   - `control.IncrementalSyncRun` is `COMPLETE`.
   - every configured endpoint/company row in
     `control.IncrementalSyncResult` is `COMPLETE`.
   - the changed raw staging row has the expected ETag/JSON and CompanyID.
   - no production publication was started.
7. Configure a separate Task Scheduler task, for example every 15 minutes.
   Configure it not to start a second instance if the first is running.
8. Keep the existing daily no-argument full scheduled task. Do not run an
   incremental task during the full extraction or publication window.

## Deletions

`updated_since` does not prove that an absent record was deleted. This version
does not physically delete rows during incremental polling. The DBA script
creates `control.IncrementalDeletionCandidate` for the later ID-only scan and
individual API-confirmation workflow.

Until that separate reconciliation job is implemented and tested, the daily
full sync is authoritative: it rebuilds staging from Clio and publishes only a
validated full RunID, so records no longer returned by Clio are reconciled by
the approved full publication process.

## Failure handling

No watermark moves forward for an endpoint/company whose poll fails. The next
successful poll reuses the earlier watermark plus the overlap window. If a
process stops after an incremental run was recorded as `RUNNING`, investigate
that run and mark it `ABANDONED` only after confirming the process is no longer
running; do not start another incremental task first.
