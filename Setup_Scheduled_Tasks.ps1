<#
  Creates the ONE Windows Task Scheduler task for CSP.Clio.StagingSync.
  Run once in an ELEVATED PowerShell on the server:

    .\Setup_Scheduled_Tasks.ps1 -ExePath "D:\Clio\CSP.Clio.StagingSync.exe"

  Optional: -RunAs "DOMAIN\svc_clio"
  Without -RunAs the task runs as SYSTEM; SYSTEM needs its own SQL login, so a service account with SQL access is better.
  With -RunAs you are asked for that account's password once.

  Task (one):
    CSP Clio Cycle    --cycle    starts at system start-up and again every day at 00:05 (a second start exits at once, see below)

  What --cycle does by itself, again and again (about 1 h 40 min per cycle):
    1. incremental window: a pass every 5 minutes for 55 minutes (CycleIncrementalWindowMinutes / CycleIncrementalIntervalMinutes)
       and, at the same time, CSP.Clio.StagingDelete.exe (one delete pass, retried once when it fails and enough of the window is left)
    2. when the incremental window is over and the delete pass is COMPLETE: the publish (--full-check --confirm-publish) to the target database
    3. a short gap (CycleGapMinutes), then the next cycle.
  CSP.Clio.StagingDelete.exe must sit in the same folder as CSP.Clio.StagingSync.exe (the build puts both in the same bin folder).
  Only one supervisor can run: a second start of the task (or a start by hand) exits at once with "another --cycle supervisor is already running".
  To stop it cleanly: create an empty file named cycle.stop next to the EXE (it ends after the current cycle), or stop the task in Task Scheduler
  (the child processes then end with it).
  The task has no time limit, restarts after a failure (3 times, 1 minute apart) and runs even when nobody is logged on.
  Logs: Logs\CSP.Clio.StagingSync-cycle-yyyyMMdd.txt (the cycle), and the last run of each part: ...-incremental-last-run.txt, ...-full-check-last-run.txt,
  CSP.Clio.StagingDelete-last-run.txt. The cycles are also recorded in Clio_Staging, table control.CycleRun.
#>
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunAs
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $ExePath)) { throw "EXE not found: $ExePath" }
$workDir = Split-Path -Parent (Resolve-Path $ExePath)
if (-not (Test-Path (Join-Path $workDir "CSP.Clio.StagingDelete.exe"))) {
    throw "CSP.Clio.StagingDelete.exe must be in the same folder as the main EXE ($workDir). Copy it (and its .config file) there first."
}

$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -StartWhenAvailable `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

if ($RunAs) {
    $cred = Get-Credential -UserName $RunAs -Message "Password of the account that runs the Clio task"
    $principal = New-ScheduledTaskPrincipal -UserId $RunAs -LogonType Password -RunLevel Highest
} else {
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
}

$action = New-ScheduledTaskAction -Execute $ExePath -Argument "--cycle" -WorkingDirectory $workDir
$triggers = @(
    (New-ScheduledTaskTrigger -AtStartup),
    (New-ScheduledTaskTrigger -Daily -At "00:05")      # only brings the supervisor back if it ever ended; while it runs, "do not start a new instance" applies
)
$task = New-ScheduledTask -Action $action -Trigger $triggers -Settings $settings -Principal $principal
if ($RunAs) {
    Register-ScheduledTask -TaskName "CSP Clio Cycle" -InputObject $task -User $RunAs -Password $cred.GetNetworkCredential().Password -Force | Out-Null
} else {
    Register-ScheduledTask -TaskName "CSP Clio Cycle" -InputObject $task -Force | Out-Null
}
Write-Host "Created: CSP Clio Cycle  (--cycle)"

# The cycle replaces every earlier task: running them together would compete for the database. Remove them if they exist.
foreach ($old in @("CSP Clio Incremental", "CSP Clio Publish", "CSP Clio Full Morning", "CSP Clio Full Night", "CSP.Clio.StagingSync", "CSP.FullStagingSync")) {
    if (Get-ScheduledTask -TaskName $old -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $old -Confirm:$false
        Write-Host "Removed old task: $old"
    }
}

Write-Host ""
Write-Host "Done. Check it in Task Scheduler (taskschd.msc) -> Task Scheduler Library."
Write-Host "To start it now:  Start-ScheduledTask -TaskName 'CSP Clio Cycle'"
