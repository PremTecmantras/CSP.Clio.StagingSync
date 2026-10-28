# Clio staging setup for Joseph and Biren

Prepared 3 October 2026. Baseline: the supplied `scripts(3).sql`, dated 27 September 2026, and the staging design dated 28 September.

## Deliverable status

This package establishes the isolated `Clio_Staging` database and the controlled publication design. The staging synchronisation scope is the explicit 19-endpoint manifest in `CSP.Clio.StagingSync`; the other installed raw candidates are legacy/reference tables or currently out of scope. They are not extracted, validated as endpoint results, cleared by the staging EXE, or published by `dbo.PublishApprovedClioStagingRun`.

The production publisher deployment script now contains the same reporting procedure order as the existing production full sync. It must be deployed and proven through the dry-run and rollback tests in this README before a scheduled production cutover. A script file is not evidence that the procedure has been deployed or that every called procedure is transaction-safe.

## Initial one-time setup and controlled first test

1. Open `00_ReadOnly_Preflight.sql` in SSMS on the instance hosting `EKPBI_New`. Run it and save the result sets. It reads metadata, non-secret company information, procedure definitions and job names. Check that `Clio_Staging` does not already exist and that the instance's default data/log volumes have adequate free space. The initial empty allocation will grow during ingestion; size and growth should be set by the DBA using actual staging volumes. Do not return credentials or job command lines in chat.
2. Run **the entire** `01_Create_Clio_Staging.sql` as a DBA, once. It uses the instance's default database paths and matches the source database collation. It creates 61 table definitions: 49 candidate raw tables, `TableSettings`, eight lookups, and three support/configuration tables, plus new control tables. Primary keys and identity definitions are retained. Secondary reporting indexes are not installed in this extraction-only database.
3. A successful setup returns `SCHEMA_READY_AUTH_AND_EXE_REVIEW_REQUIRED` and `PublishEnabled=0`. All company rows are inactive; authentication columns and sync timestamps are NULL. Only explicit non-secret configuration and lookup data are copied from production. No production rows or objects are changed.
4. If the current source column definitions differ from the saved baseline, setup returns the differences and rolls back its table creation. The newly created empty database remains for DBA review. Do not drop an existing database or bypass the check to force a rerun. Send the differences so the package can be updated against the current schema.
5. The DBA creates/maps a **dedicated test SQL login** in `Clio_Staging` and adds that database user to `clio_stage_loader`. Do not use the existing production `cspsync` login or a sysadmin login for the test EXE. Verify effective access, including server roles and inherited permissions, cannot write to production. The script grants no login access by itself. The loader role has table-level DML, ALTER on ingestion tables for TRUNCATE, and execution rights on the included local support procedures.
6. Biren configures a separate EXE/config instance with `Initial Catalog=Clio_Staging`. Keep the same `dbo` table names. He must inspect hardcoded database names, extra connection strings, SQL Agent invocations and post-sync hooks before starting it. Configure Clio authentication directly on the server. Do not run two independent OAuth refresh processes against a shared rotating refresh token without coordinating token ownership.
7. Enable only the agreed test entities in the **staging** `_EKPCompanies`, using their existing GUIDs. Do not automatically include OH because it now exists. Use a full extraction with staging `LastRefreshTime=NULL`. Do not reset production timestamps. Disable the EXE's materialization/reporting hook. An intentional staging `PopulateTables` blocker throws an error if that hook is still called, making the dependency visible.
8. Start with a single writer and an empty staging buffer. Run the first full test, then stop ingestion and run `02_Staging_Test_Diagnostics.sql` as the DBA. It produces counts per company where supported, whole-table counts otherwise, endpoint audit records and recent errors. It is read-only. The automated EXE repeats the endpoint-completion, received/written and 10 percent count checks before any production publication, but the first controlled test results must still be reviewed by the DBA.

Keep the existing production schedule unchanged while preparing the isolated test. This means the existing process may still update production independently. The new protection only becomes effective at controlled cutover, when direct production ingestion and competing post-processing jobs are retired or rerouted.

## Normal automated workflow after approved cutover

After the one-time setup, endpoint-scope confirmation, dry run and rollback test are approved, one scheduled execution of `CSP.Clio.StagingSync.exe` performs the following sequence. Do not allow overlapping scheduler executions.

1. Read the staging and production connection strings from the EXE configuration. `EKPBIEntities` must target `Clio_Staging`; `ProductionSql` identifies the production target. The EXE refuses to continue if the two connection strings point to the same database.
2. Check `control.SyncRun`. With `AbandonPreviousRunningRun=true`, a confirmed earlier stopped `RUNNING` run is marked `ABANDONED`, and its unfinished endpoint records are marked `FAILED`, before a new RunID is created. This setting must not be used with overlapping live executions.
3. Extract only the approved 19 endpoints for the active staging companies. The EXE creates the full expected RunID/company/endpoint audit set before extraction and clears only those 19 endpoint buffers per company.
4. Mark the RunID `EXTRACTED` only when every expected endpoint is `COMPLETE` and its `RowsReceived` equals `RowsWritten`. Any API, JSON, SQL-write, pagination or endpoint failure produces a failed RunID and stops the pipeline.
5. Run the automated read-only validation. It verifies the 19-table allowlist, active-company audit completeness, staging rows versus `RowsWritten`, and the staging-versus-production row-count difference for every active company/table. The default threshold is 10 percent. An already published RunID, incomplete result, changed staging buffer, or count above the threshold blocks publication.
6. Only a passed new RunID calls `dbo.PublishApprovedClioStagingRun` with `@DryRun=0` and `@RebuildReporting=1`. The procedure publishes only the same 19-table allowlist. Legacy/reference candidates remain untouched.
7. In the same controlled publisher transaction, the procedure performs the approved internal reporting sequence: `SP_CustomExecAfterSync`, `SP_InsertIntoMattersDetailedCustomFields`, `PopulateMatterNotesView`, `SP_UpdateNonBillableActivities`, `PopulateTables`, `UpdateExpectedHoursDaily`, the second `SP_UpdateNonBillableActivities`, `SP_AddUserGroupReportTo`, then `PopulateTables` for `VW_Users`, `VW_Contacts`, `VW_Folders`, `VW_PracticeAreas` and `VW_Matters`. It does not start `MatterSyncJob`, execute `SP_Sync_Matters_From_View`, or call external matters, fees, expenses or document APIs.
8. The scheduled run is successful only when `control.PublishRun` reports `COMPLETE`. Any validation or SQL error stops the pipeline and records the failing RunID/PublishID and error message for investigation.

## What is included and intentionally absent

`Table_Inventory.txt` lists every installed source table. The 49 raw candidates are taken from the source definitions; their existence does **not** establish that all are active API endpoints. The approved operational scope is the 19 endpoints in the EXE and `dbo.PublishApprovedClioStagingRun`: PracticeAreas, Activities, Calendars, CalendarEntries, Relationships, Tasks, TaskTypes, Matters, Bills, BankTransactions, CustomFields, CustomFieldSets, Folders, LineItems, Contacts, Allocations, Users, ActivityDescriptions and MatterNotes. All other candidates are legacy/reference/out-of-scope tables for this pipeline. They may be copied from production as bootstrap data or remain empty; they must not be inferred to be API-extracted or published.

The support procedures `SP_ClearAllData`, `SP_ClearData`, `SP_CleanData`, `SP_SyncAllData`, and `Stop_Sync` are scoped to staging. The first two retain the baseline's limited table lists, so they do **not** establish a clean full snapshot across every candidate table. Before a second test, preserve the failed buffer if needed and arrange a complete reset of all confirmed endpoint targets while the loader is stopped. Never mix remnants from different runs.

`SP_CustomExecAfterSync` retains the bill-currency adjustment but now rethrows errors. Its original implementation logs an error and returns normally, which must not be treated as successful completion.

No business-owned tables such as `Employees`, `FeeEarnerCostRates`, expected-hours, budgets or partner mappings are installed or overwritten in staging. Reporting views and materialized outputs are not cloned into staging. The staging EXE never runs `ConvertViewsToTable`, `PopulateTables` or `SyncMatters` against `Clio_Staging`; reporting is run only by the approved production publisher after a passed validation.

## Controls required before production publishing

| Gate | Required evidence |
|---|---|
| Expected scope | An explicit, versioned list of every required company and endpoint, captured at run start. Compare against that list, not only the endpoints that happened to report completion. |
| One complete run | Unique RunID; fresh full staging buffer; single writer; every expected endpoint COMPLETE; normal end of every pagination chain; all received records accounted for in database writes. An empty response can be valid only under an endpoint-specific rule. |
| Error handling | No unresolved HTTP, pagination-token, JSON, SQL-write or post-processing error; retries exhausted must fail the run; no skipped bad records disguised as success. A process exit or LastSyncTime alone is insufficient. |
| Data validation | Required fields, valid conversions, business-key uniqueness, allowed NULLs, referential checks scoped by entity, and expected currencies/dates. The current automated count threshold is 10 percent for every active company/approved endpoint table; unexplained row drops or unexpected zeroes block publication. Any future company/endpoint-specific threshold requires a reviewed configuration change. |
| Deletions | Apply missing-record deletions only for a verified full extraction of the same source scope. A delta, permission change or partial endpoint result is not proof of deletion. |
| Frozen candidate | No staging writes or buffer resets from validation through publication. A RunID in a control table alone does not bind every raw row to that run. The initial single-buffer implementation needs enforced orchestration/locking; versioned buffers are a later option. |
| Atomic publication | One controlled publisher, explicit table/column allowlist, tested rollback, critical reconciliation inside the transaction, and no publish on stale validation. Do not publish every table that happens to exist in staging. |
| Reporting readiness | Complete the reporting boundary before exposing a new generation. Power BI starts only after READY. Failed Power BI refresh is separate from SQL publication failure. |

The audit contract is `control.SyncRun` plus `control.SyncEndpointResult`, keyed by RunID, CompanyID and endpoint. The staging EXE creates the expected rows at startup, records committed row counts, and closes a configured stale run as `ABANDONED` before the next run starts. The filtered unique index limits tracked RUNNING rows; it does not prevent an uninstrumented EXE from writing. Never log access tokens or pagination URLs containing sensitive tokens.

For retained failures, a single staging buffer cannot preserve 7-14 days of snapshots automatically. Retain/export a failed buffer before reuse, or implement run-specific storage. No automatic retention cleanup is installed here.

## Publication and PopulateTables decision

The publisher commits the allowlisted raw production tables and the full production reporting sequence in one explicit transaction. The implementation sequence matches the existing full-sync `ConvertViewsToTable` and `SyncMatters` order. A failure must roll back the raw and reporting changes before `control.PublishRun` is marked failed.

The following transaction boundary must be tested before production cutover:

* Execute the allowlisted raw replacement, `SP_CustomExecAfterSync`, `SP_InsertIntoMattersDetailedCustomFields`, `PopulateMatterNotesView`, both `SP_UpdateNonBillableActivities` calls, `PopulateTables`, `UpdateExpectedHoursDaily`, `SP_AddUserGroupReportTo`, the five internal `SyncMatters` view materialisations, and reconciliation under one proven outer transaction, committing only after all succeed. External `MatterSyncJob` API processing is intentionally excluded.
* Audit every called procedure, dynamic statement and error path for transaction compatibility. Test the existing per-table swap and CATCH rollback behavior; do not assume that a wrapper transaction is sufficient. Assess lock duration and log space for the full materialization.
* If this boundary proves impractical, do not enable automatic publication. Build and validate the complete reporting candidate outside live objects, using a consistent copy of required business inputs, then atomically promote the approved raw and reporting generation. Cross-database references must be retargeted deliberately and local business tables must remain owned by production.

Do not use TRUNCATE-and-insert universally. Several source `ID` columns are local SQL identity keys while `Clio_Internal_ID` is the API identifier. Verify downstream use of local IDs before choosing replacement or keyed reconciliation. For multi-company data, the business key commonly includes `CompanyID`; some saved raw tables lack that column and require an explicit source-scope solution before publication.

A single committed database generation also needs reader coordination. Remove/handle dirty reads (`NOLOCK` / READ UNCOMMITTED) on the published path and prevent multi-query Power BI refresh from straddling a publication. A READY flag by itself does not freeze data while Power BI reads it.

SQL transaction mechanics: use explicit error propagation, `SET XACT_ABORT ON`, TRY/CATCH rollback, and check all negative `sp_getapplock` return values. Every cooperating publisher must use the same database, principal and lock resource. These mechanics do not substitute for endpoint-completion evidence.

Microsoft references:
* https://learn.microsoft.com/en-us/sql/t-sql/statements/set-xact-abort-transact-sql?view=sql-server-ver17
* https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver17

## Biren's test handoff

Deliver back: complete endpoint/table manifest, EXE dependencies and destination checks, explicit run-completion evidence, staging test timings/counts/errors, and whether the existing pipeline can participate in one transaction. Test a mid-page API failure, one missing company/endpoint, JSON failure, SQL write failure, unexpected zero/drop, concurrent start, and later both publication and PopulateTables failures. Compare the prior and resulting production business data to prove rollback; audit logging may still record the failure.

The immediate next step is to execute the read-only preflight and review its output. The creation script can then provision the isolated target; production publication should remain unavailable until the above contract is implemented and tested.
