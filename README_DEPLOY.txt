CSP.Clio.StagingSync - server deployment, two-database pipeline (package v3)

The full, ordered list of what to change on the RDP server is in Server_Change_List.md. Short version:

1. Free disk space for one more copy of Clio_Staging. Back up Clio_Staging and EKPBI_New_Dev. Disable the old tasks, wait until no EXE runs.
2. Copy the Build folder to the fixed path on the server and edit CSP.Clio.StagingSync.exe.config (it contains LOCAL connection strings):
   - EKPBIEntities  -> server instance, catalog Clio_Staging
   - EKPBIEntitiesLive (new) -> same instance and login, catalog Clio_Live
   - ProductionSql  -> server instance, catalog EKPBI_New_Dev
   - LiveDatabaseName = Clio_Live (leave empty = old single-database mode), PublishAfterValidation = false
3. SSMS (sysadmin): run Scripts\5_run_fifth__13_Create_Clio_Live...sql (edit @BackupFolder and @LiveDataFolder at the top first).
   It copies Clio_Staging into the new Clio_Live and adds three small control tables. Scripts 10, 11 and 12 are NOT run again.
   Check that StagingRows = LiveRows in its result, then delete the .bak file it names.
4. By hand from the Build folder: --incremental, --delete-sync, --full-check (dry run), --full-check --confirm-publish,
   then BillingCollDailyCheck.sql on Dev and Prod (variance 0).
5. Only then run Setup_Scheduled_Tasks.ps1 (elevated PowerShell): it creates three tasks
   Incremental (every 5 min), Delete (every 60 min), Publish (every 90 min).
Rollback: disable the three tasks, set LiveDatabaseName empty (everything runs in Clio_Staging again) or re-enable the old tasks.
Pipeline_Workflow.md describes the whole workflow.
