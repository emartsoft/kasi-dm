<#
.SYNOPSIS
  Fails when the line coverage of one assembly in a Cobertura file is below a threshold (KASI-NFR-018: Kasi.Engine at least 80%).

.DESCRIPTION
  Reads the line-rate of the named assembly (the Cobertura <package>) from the merged report that ReportGenerator writes, not from
  the Markdown summary. Exit codes: 0 at or above the threshold, 1 below it, 2 when the file or the assembly is missing (a gate
  that cannot find its input must not pass). The figure is also written to the GitHub job summary when one is available.

.PARAMETER Cobertura  The merged Cobertura XML (ReportGenerator report type "Cobertura").
.PARAMETER Assembly   The package name to gate. Default Kasi.Engine.
.PARAMETER Threshold  The minimum line-rate as a fraction. Default 0.80.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Cobertura,
    [string] $Assembly = 'Kasi.Engine',
    [double] $Threshold = 0.80
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Stop-Gate([string] $Message, [int] $Code) {
    [Console]::Error.WriteLine($Message)
    exit $Code
}

if (-not (Test-Path -LiteralPath $Cobertura)) {
    Stop-Gate "Coverage file '$Cobertura' not found: the gate cannot be evaluated." 2
}

[xml] $report = Get-Content -LiteralPath $Cobertura -Raw
$package = @($report.SelectNodes("//package[@name='$Assembly']"))
if ($package.Count -ne 1) {
    Stop-Gate "Assembly '$Assembly' is not in '$Cobertura' (found $($package.Count) matches): the gate cannot be evaluated." 2
}

$rate = [double]::Parse($package[0].'line-rate', [Globalization.CultureInfo]::InvariantCulture)
$percent = [math]::Round($rate * 100, 1)
$required = [math]::Round($Threshold * 100, 1)
$passed = $rate -ge $Threshold
$verdict = if ($passed) { 'PASS' } else { 'FAIL' }
$line = "$Assembly line coverage: $percent% (required $required%) - $verdict"
Write-Host $line

if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "### Coverage gate`n`n$line`n"
}

if (-not $passed) {
    Stop-Gate "$Assembly coverage is $percent%, below the $required% required by KASI-NFR-018." 1
}
