<#
.SYNOPSIS
  Checks the published Native AOT host's size and records its cold start (docs/plans/m2-2.md section 3; KASI-NFR-010, TRD 5.2).

.DESCRIPTION
  Fails (exit 1) when Kasi.NativeHost.exe is larger than -MaxMegabytes (5). The start-up time is the median of -Launches runs of the
  executable with stdin closed at once, from process creation to its clean exit; it is reported, never gated, because the TRD target
  (50 ms) is judged on the benchmark machine and a shared runner is noisier. The result goes to the console and, in CI, to the job summary.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [double] $MaxMegabytes = 5,
    [int] $Launches = 20
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Exe)) { throw "Host executable not found at '$Exe'." }
$bytes = (Get-Item -LiteralPath $Exe).Length
$megabytes = [math]::Round($bytes / 1MB, 2)

$times = foreach ($i in 1..$Launches) {
    $info = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Exe).Path)
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.UseShellExecute = $false
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    $process.StandardInput.Close()
    $process.WaitForExit()
    $clock.Stop()
    if ($process.ExitCode -ne 0) { throw "The host exited with code $($process.ExitCode) on an empty stdin." }
    $process.Dispose()
    $clock.Elapsed.TotalMilliseconds
}
$sorted = @($times | Sort-Object)
$median = [math]::Round(($sorted[[int][math]::Floor(($sorted.Count - 1) / 2)] + $sorted[[int][math]::Ceiling(($sorted.Count - 1) / 2)]) / 2, 1)

$line = "Kasi.NativeHost.exe (win-x64, Native AOT): $megabytes MB (limit $MaxMegabytes MB); cold start, median of $Launches launches: $median ms (min $([math]::Round($sorted[0], 1)), max $([math]::Round($sorted[-1], 1))); the 50 ms target is judged on the benchmark machine"
Write-Host $line
if ($env:GITHUB_STEP_SUMMARY) { "### Native host`n`n$line" | Add-Content -Path $env:GITHUB_STEP_SUMMARY }

if ($bytes -gt $MaxMegabytes * 1MB) { Write-Error "The host is $megabytes MB, over the $MaxMegabytes MB limit."; exit 1 }
