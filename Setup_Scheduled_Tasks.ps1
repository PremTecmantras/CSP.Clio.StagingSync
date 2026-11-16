<#
  Creates the three Windows Task Scheduler tasks for CSP.Clio.StagingSync (two-database mode).
  Run once in an ELEVATED PowerShell on the server:

    .\Setup_Scheduled_Tasks.ps1 -ExePath "D:\Clio\CSP.Clio.StagingSync.exe"

  Optional: -PublishEveryMinutes 90 -FirstPublishAt "00:30" -DeleteEveryMinutes 60 -FirstDeleteAt "00:15" -RunAs "DOMAIN\svc_clio"
  Without -RunAs the tasks run as SYSTEM; SYSTEM needs its own SQL login, so a service account with SQL access is better.
  With -RunAs you are asked for that account's password once.

  Tasks:
    CSP Clio Incremental   every 5 minutes                 --incremental                       writes to Clio_Live all day
    CSP Clio Delete        every DeleteEveryMinutes (60)   --delete-sync                       moves records deleted in Clio to the *_Deleted tables (Clio_Live), beside the incremental
    CSP Clio Publish       every PublishEveryMinutes (90)  --full-check --confirm-publish      copies Live to Clio_Staging, checks, builds the reports, publishes

  The three jobs run at the same time without waiting for each other: the incremental and the delete task work on Clio_Live, the publish cycle
  works on Clio_Staging and only holds Clio_Live for the short copy at its start (an incremental run that meets that moment skips and the next one,
  5 minutes later, catches up).
  Incremental and Delete: "do not start a new instance" (a run that is still going is never doubled). Publish: runs that overlap are QUEUED, not skipped.
  The first-slot times only set the rhythm; they are offset on purpose so the jobs do not all start in the same minute.
  Rules for all: no time limit, run even when nobody is logged on, run as soon as possible after a missed start.
#>
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [int]$PublishEveryMinutes = 90,
    [string]$FirstPublishAt = "00:30",
    [int]$DeleteEveryMinutes = 60,
    [string]$FirstDeleteAt = "00:15",
    [string]$RunAs
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $ExePath)) { throw "EXE not found: $ExePath" }
$workDir = Split-Path -Parent (Resolve-Path $ExePath)

$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

# Publish task: a slot that comes while the previous run is still going is QUEUED (one waiting), not skipped.
$queueSettings = New-ScheduledTaskSettingsSet `
    -MultipleInstances Queue `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

if ($RunAs) {
    $cred = Get-Credential -UserName $RunAs -Message "Password of the account that runs the Clio tasks"
    $principal = New-ScheduledTaskPrincipal -UserId $RunAs -LogonType Password -RunLevel Highest
} else {
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
}

function Register($name, $arguments, $trigger, $taskSettings = $settings) {
    $action = New-ScheduledTaskAction -Execute $ExePath -Argument $arguments -WorkingDirectory $workDir
    $task = New-ScheduledTask -Action $action -Trigger $trigger -Settings $taskSettings -Principal $principal
    if ($RunAs) {
        Register-ScheduledTask -TaskName $name -InputObject $task -User $RunAs -Password $cred.GetNetworkCredential().Password -Force | Out-Null
    } else {
        Register-ScheduledTask -TaskName $name -InputObject $task -Force | Out-Null
    }
    Write-Host "Created: $name  ($arguments)"
}

# Every 5 minutes, all day, starting one minute from now.
$every5 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
Register "CSP Clio Incremental" "--incremental" $every5

# Background delete pass, repeating all day from FirstDeleteAt.
$deleteAt = [datetime]::Today.Add([TimeSpan]::Parse($FirstDeleteAt))
$deleteTrigger = New-ScheduledTaskTrigger -Once -At $deleteAt -RepetitionInterval (New-TimeSpan -Minutes $DeleteEveryMinutes)
Register "CSP Clio Delete" "--delete-sync" $deleteTrigger

# Publish cycle, repeating all day from FirstPublishAt.
$publishAt = [datetime]::Today.Add([TimeSpan]::Parse($FirstPublishAt))
$publishTrigger = New-ScheduledTaskTrigger -Once -At $publishAt -RepetitionInterval (New-TimeSpan -Minutes $PublishEveryMinutes)
Register "CSP Clio Publish" "--full-check --confirm-publish" $publishTrigger $queueSettings

# Remove tasks from the earlier setups, if they exist.
foreach ($old in @("CSP Clio Full Morning", "CSP Clio Full Night")) {
    if (Get-ScheduledTask -TaskName $old -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $old -Confirm:$false
        Write-Host "Removed old task: $old"
    }
}

Write-Host ""
Write-Host "Done. Check them in Task Scheduler (taskschd.msc) -> Task Scheduler Library."
Write-Host "Tasks you created by hand earlier (for example 'CSP.Clio.StagingSync', 'CSP.FullStagingSync') are NOT touched: disable them, or they will run next to these."
Write-Host "To test one now:  Start-ScheduledTask -TaskName 'CSP Clio Publish'"
