<#
.SYNOPSIS
  Power-cut suite on a Hyper-V host (TC-RES-003, KASI-RES-003; test plan 3.4 and 7.1).

.DESCRIPTION
  Per iteration: restore the base checkpoint, start the guest, start the download inside it (PowerShell Direct), wait until it has
  checkpointed a seeded share of the file (or, with -CutAtCommit, until it is stuck inside a checkpoint commit), turn the guest
  off like a pulled plug (Stop-VM -TurnOff), start it again and run the harness's verify phase: PRAGMA integrity_check, the
  database agreeing with itself, then the download finished with the right SHA-256.

  This must run on the dedicated host (docs/fault-injection.md, "Power cut"). Run it elevated. It is never run from a pull request.
  The guest password comes from the environment (KASI_FAULT_GUEST_PASSWORD), never from the command line.

.PARAMETER VMName       The guest. It needs .NET 10, the harness build in -GuestHarness, and a checkpoint named -Checkpoint.
.PARAMETER GuestUser    A local account in the guest.
.PARAMETER Iterations   Cuts to perform (the test plan asks for 20).
.PARAMETER Seed         Fixes the cut points; printed and stored in the report.
.PARAMETER HarnessSource A folder with the built harness (dotnet publish of tests/Kasi.FaultInjection); copied into the guest each iteration.
.PARAMETER CutAtCommit Cut while the guest is inside a checkpoint commit instead of at a percentage.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $VMName,
    [Parameter(Mandatory)] [string] $GuestUser,
    [int] $Iterations = 20,
    [int] $Seed = (Get-Random -Minimum 1 -Maximum 2000000000),
    [string] $Checkpoint = 'kasi-fault-base',
    [string] $GuestHarness = 'C:\kasi\fault\Kasi.FaultInjection.dll',
    [string] $GuestState = 'C:\kasi\state',
    [long] $Size = 2GB,
    [long] $Rate = 16MB,
    [string] $HarnessSource,
    [switch] $CutAtCommit,
    [string] $OutputDir = 'artifacts/power-cut'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script elevated: Hyper-V cmdlets and PowerShell Direct need Administrator rights.'
}
if (-not $env:KASI_FAULT_GUEST_PASSWORD) {
    throw 'Set KASI_FAULT_GUEST_PASSWORD to the guest account password.'
}

$credential = [pscredential]::new($GuestUser, (ConvertTo-SecureString $env:KASI_FAULT_GUEST_PASSWORD -AsPlainText -Force))
$random = [System.Random]::new($Seed)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Write-Host "Power cut: VM=$VMName iterations=$Iterations seed=$Seed mode=$(if ($CutAtCommit) { 'commit' } else { 'percent' })"

function Start-Guest {
    Restore-VMCheckpoint -VMName $VMName -Name $Checkpoint -Confirm:$false
    Start-VM -Name $VMName | Out-Null
    # the session only proved the guest is ready; the caller opens its own
    Remove-PSSession (Wait-GuestReady)
}

function Wait-GuestReady {
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-VM -Name $VMName).Heartbeat -notin 'OkApplicationsHealthy', 'OkApplicationsUnknown') {
        if ((Get-Date) -gt $deadline) { throw 'The guest did not report a heartbeat within 5 minutes.' }
        Start-Sleep -Seconds 2
    }
    # PowerShell Direct needs the guest's session service, which can lag behind the heartbeat
    while ($true) {
        try { return New-PSSession -VMName $VMName -Credential $credential -ErrorAction Stop } catch {
            if ((Get-Date) -gt $deadline) { throw }
            Start-Sleep -Seconds 3
        }
    }
}

function Invoke-Harness([System.Management.Automation.Runspaces.PSSession] $session, [string] $phase) {
    Invoke-Command -Session $session -ScriptBlock {
        param($dll, $state, $phase, $size, $rate, $seed)
        & dotnet $dll --scenario power-guest --phase $phase --state $state --size $size --rate $rate --seed $seed
        $LASTEXITCODE
    } -ArgumentList $GuestHarness, $GuestState, $phase, $Size, $Rate, $Seed
}

$results = @()
for ($i = 1; $i -le $Iterations; $i++) {
    $cutPercent = $random.Next(10, 71)
    $commitsBefore = $random.Next(1, 6)
    $problem = $null
    try {
        Start-Guest | Out-Null
        $session = New-PSSession -VMName $VMName -Credential $credential

        # the checkpoint predates this build: put the harness in the guest (it needs only the .NET runtime)
        if ($HarnessSource) {
            $guestFolder = Split-Path $GuestHarness
            Invoke-Command -Session $session -ScriptBlock { param($f) New-Item -ItemType Directory -Force $f | Out-Null } -ArgumentList $guestFolder
            Copy-Item -ToSession $session -Path (Join-Path $HarnessSource '*') -Destination $guestFolder -Recurse -Force
        }

        # start the download inside the guest and leave it running
        Invoke-Command -Session $session -ScriptBlock {
            param($dll, $state, $size, $rate, $seed, $atCommit, $commitsBefore)
            Remove-Item -Recurse -Force $state -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force $state | Out-Null
            if ($atCommit) {
                $env:KASI_FAULT_COMMIT_MARKER = Join-Path $state 'commit.marker'
                $env:KASI_FAULT_COMMITS_BEFORE = $commitsBefore
            }
            Start-Process dotnet -ArgumentList $dll, '--scenario', 'power-guest', '--phase', 'run', '--state', $state, '--size', $size, '--rate', $rate, '--seed', $seed -WindowStyle Hidden
        } -ArgumentList $GuestHarness, $GuestState, $Size, $Rate, $Seed, $CutAtCommit.IsPresent, $commitsBefore

        # wait for the moment to cut
        $deadline = (Get-Date).AddMinutes(10)
        while ($true) {
            if ((Get-Date) -gt $deadline) { throw 'The guest never reached the cut point.' }
            if ($CutAtCommit) {
                $ready = Invoke-Command -Session $session -ScriptBlock { param($s) Test-Path (Join-Path $s 'commit.marker') } -ArgumentList $GuestState
            } else {
                $percent = [int](Invoke-Command -Session $session -ScriptBlock {
                    param($dll, $state, $size) & dotnet $dll --scenario power-guest --phase progress --state $state --size $size
                } -ArgumentList $GuestHarness, $GuestState, $Size)
                $ready = $percent -ge $cutPercent
            }
            if ($ready) { break }
            Start-Sleep -Milliseconds 500
        }

        # the pulled plug
        Remove-PSSession $session
        Stop-VM -Name $VMName -TurnOff -Force
        Start-VM -Name $VMName | Out-Null
        $session = Wait-GuestReady

        $exit = Invoke-Harness $session 'verify' | Select-Object -Last 1
        if ($exit -ne 0) { $problem = "verify exited with $exit" }
        Copy-Item -FromSession $session -Path (Join-Path $GuestState 'power-report.json') -Destination (Join-Path $OutputDir "iteration-$i.json") -ErrorAction SilentlyContinue
        Remove-PSSession $session
    } catch {
        $problem = $_.Exception.Message
    } finally {
        Stop-VM -Name $VMName -TurnOff -Force -ErrorAction SilentlyContinue
    }

    $results += [pscustomobject]@{ Iteration = $i; CutPercent = $(if ($CutAtCommit) { $null } else { $cutPercent }); Success = ($null -eq $problem); Error = $problem }
    Write-Host ("iteration {0}: {1}{2}" -f $i, $(if ($problem) { 'FAIL ' } else { 'ok' }), $problem)
}

$failed = @($results | Where-Object { -not $_.Success })
$report = [pscustomobject]@{ Scenario = 'power-cut'; Seed = $Seed; Iterations = $Iterations; Failed = $failed.Count; Results = $results }
$report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDir 'fault-report.json')
$summary = "# Fault injection: power-cut ($(if ($failed.Count) { 'FAIL' } else { 'PASS' }))`n`n| Seed | Iterations | Failed |`n| --- | --- | --- |`n| $Seed | $Iterations | $($failed.Count) |`n"
foreach ($f in $failed) { $summary += "- iteration $($f.Iteration): $($f.Error)`n" }
$summary += "`nRe-run the same cut points with ``-Seed $Seed``.`n"
Set-Content (Join-Path $OutputDir 'summary.md') $summary
if ($failed.Count) { exit 1 }
