<#
  Creates the three Windows Task Scheduler tasks for CSP.Clio.StagingSync.
  Run once in an ELEVATED PowerShell on the server:

    .\Setup_Scheduled_Tasks.ps1 -ExePath "D:\Clio\CSP.Clio.StagingSync.exe"

  Optional: -PublishEveryMinutes 90 -FirstPublishAt "00:30" -RunAs "DOMAIN\svc_clio"
  Without -RunAs the tasks run as SYSTEM; SYSTEM needs its own SQL login, so a service account with SQL access is better.
  With -RunAs you are asked for that account's password once.

  Tasks (two):
    CSP Clio Incremental   every 5 minutes                 --incremental
    CSP Clio Publish       every PublishEveryMinutes (90)  --full-check --confirm-publish   (includes the delete reconciliation)
  A run with deletes takes about 1 h 25 min on the server, so it only just fits into 90 minutes. If a run is still going when the
  next slot comes, that slot is skipped. To make runs shorter, add --skip-deletes to the Publish task.
  Rules for both: never start a second copy, no time limit, run even when nobody is logged on, run as soon as possible after a missed start.
#>
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [int]$PublishEveryMinutes = 90,
    [string]$FirstPublishAt = "00:30",
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

if ($RunAs) {
    $cred = Get-Credential -UserName $RunAs -Message "Password of the account that runs the Clio tasks"
    $principal = New-ScheduledTaskPrincipal -UserId $RunAs -LogonType Password -RunLevel Highest
} else {
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
}

function Register($name, $arguments, $trigger) {
    $action = New-ScheduledTaskAction -Execute $ExePath -Argument $arguments -WorkingDirectory $workDir
    $task = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal
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

# Full check with the delete reconciliation and publish, repeating all day from FirstPublishAt.
$publishAt = [datetime]::Today.Add([TimeSpan]::Parse($FirstPublishAt))
$publishTrigger = New-ScheduledTaskTrigger -Once -At $publishAt -RepetitionInterval (New-TimeSpan -Minutes $PublishEveryMinutes)
Register "CSP Clio Publish" "--full-check --confirm-publish" $publishTrigger

# Remove tasks from the earlier setup, if they exist.
foreach ($old in @("CSP Clio Full Morning", "CSP Clio Full Night")) {
    if (Get-ScheduledTask -TaskName $old -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $old -Confirm:$false
        Write-Host "Removed old task: $old"
    }
}

Write-Host ""
Write-Host "Done. Check them in Task Scheduler (taskschd.msc) -> Task Scheduler Library."
Write-Host "To test one now:  Start-ScheduledTask -TaskName 'CSP Clio Publish'"
