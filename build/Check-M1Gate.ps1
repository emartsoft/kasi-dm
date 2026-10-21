<#
.SYNOPSIS
  Checks the M1 engine gate (plan.md 4.2, TPL 4) against the evidence there is, and writes the gate report.

.DESCRIPTION
  Six criteria. Each is judged from evidence the script collects with git, the GitHub CLI and the files in the repository, so a
  missing piece shows up as open rather than being assumed:

    1. Every P1 test case (TC-ENG-001 to 024, TC-RES-001 to 012) has a tagged test, the latest CI run on main is green, and the cases
       that only people can run (the Hyper-V power cut, TC-RES-003) are recorded in docs/m1-gate-evidence.json.
    2. The last three scheduled nightly-faults.yml runs on main succeeded, and in each of them every suite's summary says PASS. A
       SKIPPED suite is not a pass.
    3. The latest bench/results/idm-comparison-*.json passes the scorecard (median ratio at most 1.10, no source above 1.25).
    4. Kasi.Engine line coverage in the latest main CI run is at least 80%.
    5. No open Critical or High defect (labels "severity: critical" and "severity: high" on open issues).
    6. The gate report (this script's output, with the build and the links) for the owner's sign-off.

  The exit code is 0 only when all of 1 to 5 pass. -EvidenceFile judges a prepared evidence file instead of collecting it, which is
  how the tests exercise the judgement without GitHub.
#>
[CmdletBinding()]
param(
    [string] $Repository = 'emartsoft/kasi-dm',
    [string] $EvidenceFile,
    [string] $KasiBench = (Join-Path (Split-Path $PSScriptRoot -Parent) 'bench/Kasi.Bench/bin/Release/net10.0/Kasi.Bench.dll'),
    [string] $OutputDir = 'artifacts/gate',
    [string] $SaveEvidence
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$requiredCases = @(1..24 | ForEach-Object { 'TC-ENG-{0:D3}' -f $_ }) + @(1..12 | ForEach-Object { 'TC-RES-{0:D3}' -f $_ })
$nightlySuites = 'kill', 'commit-kill', 'server-mutation', 'storage-stall', 'disk-pressure', 'network-soak'

function Test-OwnerEvidence($evidence, [string] $case) {
    $recorded = $evidence.testCases.ownerEvidence.PSObject.Properties[$case]
    return ($null -ne $recorded -and $null -ne $recorded.Value -and $recorded.Value -ne $false)
}

# --- the judgement: pure, no I/O --------------------------------------------------------------------------------------------
function Test-Gate($evidence) {
    $criteria = @()

    # a case that only people can run (the Hyper-V power cut) needs the recorded evidence even though its harness code is tagged
    $ownerOnly = @($evidence.testCases.ownerRequired)
    $missing = @($evidence.testCases.required | Where-Object {
            if ($_ -in $ownerOnly) { -not (Test-OwnerEvidence $evidence $_) } else { $_ -notin @($evidence.testCases.tagged) }
        })
    $detail = if ($missing.Count) { "no tagged test, or no recorded owner evidence, for: $($missing -join ', ')" } elseif (-not $evidence.testCases.ciGreen) { 'the latest CI run on main is not green' } else { 'every case has a tagged test (and recorded owner evidence where only people can run it), and CI on main is green' }
    $criteria += [pscustomobject]@{ Name = '1. P1 test cases'; Passed = ($missing.Count -eq 0 -and [bool] $evidence.testCases.ciGreen); Detail = $detail }

    $nights = @($evidence.nights)
    $nightProblems = @()
    if ($nights.Count -lt 3) { $nightProblems += "only $($nights.Count) scheduled night(s) on main so far, 3 needed" }
    foreach ($night in $nights | Select-Object -First 3) {
        if ($night.conclusion -ne 'success') { $nightProblems += "run $($night.id) concluded $($night.conclusion)" }
        foreach ($suite in $nightlySuites) {
            $found = @($night.suites | Where-Object { $_.name -eq $suite })
            if ($found.Count -eq 0) { $nightProblems += "run $($night.id): no report for $suite" }
            elseif ($found[0].verdict -ne 'PASS') { $nightProblems += "run $($night.id): $suite is $($found[0].verdict)" }
        }
    }
    $criteria += [pscustomobject]@{ Name = '2. Three consecutive nights'; Passed = ($nightProblems.Count -eq 0); Detail = $(if ($nightProblems.Count) { $nightProblems -join '; ' } else { 'the last 3 scheduled runs passed, every suite PASS' }) }

    $bench = $evidence.benchmark
    $criteria += [pscustomobject]@{ Name = '3. Benchmark within 10% of IDM'; Passed = ($null -ne $bench -and [bool] $bench.passed); Detail = $(if ($null -eq $bench) { 'no idm-comparison session yet (owner evidence)' } elseif ($bench.passed) { "scorecard passes: $($bench.file)" } else { "scorecard fails: $($bench.file)" }) }

    $coverage = $evidence.coverage
    $criteria += [pscustomobject]@{ Name = '4. Engine coverage at least 80%'; Passed = ($null -ne $coverage -and $coverage -ge 0.80); Detail = $(if ($null -eq $coverage) { 'no coverage report found' } else { 'Kasi.Engine line coverage {0:P1}' -f $coverage }) }

    $defects = $evidence.openDefects
    $criteria += [pscustomobject]@{ Name = '5. No open Critical or High defect'; Passed = ($null -ne $defects -and $defects -eq 0); Detail = $(if ($null -eq $defects) { 'open defects could not be read' } else { "$defects open Critical or High defect(s)" }) }
    return $criteria
}

# --- the evidence: collected, then judged -----------------------------------------------------------------------------------
function Get-Evidence {
    $tagged = @(Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter *.cs | Select-String -Pattern '"(TC-(?:ENG|RES)-\d{3})"' -AllMatches |
            ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $owner = @{}
    $ownerFile = Join-Path $root 'docs/m1-gate-evidence.json'
    if (Test-Path $ownerFile) {
        $recorded = Get-Content $ownerFile -Raw | ConvertFrom-Json
        foreach ($entry in $recorded.testCases.PSObject.Properties) { if ($entry.Value) { $owner[$entry.Name] = $true } }
    }

    $ci = gh run list --repo $Repository --workflow ci.yml --branch main --limit 1 --json 'databaseId,conclusion,headSha,url' | ConvertFrom-Json
    $ciRun = @($ci) | Select-Object -First 1

    $nights = @()
    $runs = @(gh run list --repo $Repository --workflow nightly-faults.yml --branch main --event schedule --limit 3 --json 'databaseId,conclusion,url' | ConvertFrom-Json)
    foreach ($run in $runs) {
        $suites = @()
        $temp = Join-Path ([IO.Path]::GetTempPath()) ("gate-" + $run.databaseId)
        New-Item -ItemType Directory -Force -Path $temp | Out-Null
        gh run download $run.databaseId --repo $Repository --dir $temp 2>$null | Out-Null
        foreach ($summary in Get-ChildItem $temp -Recurse -Filter summary.md -ErrorAction SilentlyContinue) {
            $first = (Get-Content $summary.FullName -TotalCount 1)
            if ($first -match '^# Fault injection: ([\w-]+) \((\w+)\)') { $suites += [pscustomobject]@{ name = $Matches[1]; verdict = $Matches[2] } }
        }
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
        $nights += [pscustomobject]@{ id = $run.databaseId; url = $run.url; conclusion = $run.conclusion; suites = $suites }
    }

    $benchmark = $null
    $latest = Get-ChildItem (Join-Path $root 'bench/results') -Filter 'idm-comparison-*.json' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
    if ($latest -and (Test-Path $KasiBench)) {
        dotnet $KasiBench scorecard $latest.FullName | Out-Null
        $benchmark = [pscustomobject]@{ file = $latest.Name; passed = ($LASTEXITCODE -eq 0) }
    }

    $coverage = $null
    if ($ciRun) {
        $temp = Join-Path ([IO.Path]::GetTempPath()) ("gate-cov-" + $ciRun.databaseId)
        New-Item -ItemType Directory -Force -Path $temp | Out-Null
        gh run download $ciRun.databaseId --repo $Repository --name test-results-and-coverage --dir $temp 2>$null | Out-Null
        $cobertura = Get-ChildItem $temp -Recurse -Filter Cobertura.xml -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cobertura) {
            [xml] $xml = Get-Content $cobertura.FullName -Raw
            $package = @($xml.SelectNodes("//package[@name='Kasi.Engine']"))
            if ($package.Count -eq 1) { $coverage = [double]::Parse($package[0].'line-rate', [Globalization.CultureInfo]::InvariantCulture) }
        }
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }

    # "[]" is a real answer (no open bug); only a failed gh call leaves the count unknown
    $defects = $null
    $raw = gh issue list --repo $Repository --state open --label bug --json 'number,labels' --limit 200 2>$null
    if ($LASTEXITCODE -eq 0) {
        $issues = @(($raw | Out-String | ConvertFrom-Json))
        $defects = @($issues | Where-Object { $_ -and (@($_.labels.name) -contains 'severity: critical' -or @($_.labels.name) -contains 'severity: high') }).Count
    }

    return [pscustomobject]@{
        build      = $(if ($ciRun) { "CI run $($ciRun.databaseId) at $($ciRun.headSha.Substring(0, 7))" } else { 'no CI run found' })
        links      = [pscustomobject]@{ ci = $(if ($ciRun) { $ciRun.url } else { $null }); nights = @($nights | ForEach-Object { $_.url }) }
        testCases  = [pscustomobject]@{ required = $requiredCases; ownerRequired = @('TC-RES-003'); tagged = $tagged; ownerEvidence = [pscustomobject]$owner; ciGreen = [bool] ($ciRun -and $ciRun.conclusion -eq 'success') }
        nights     = $nights
        benchmark  = $benchmark
        coverage   = $coverage
        openDefects = $defects
    }
}

$evidence = if ($EvidenceFile) { Get-Content -LiteralPath $EvidenceFile -Raw | ConvertFrom-Json } else { Get-Evidence }
if ($SaveEvidence) { $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $SaveEvidence }
$criteria = Test-Gate $evidence

# --- the report -------------------------------------------------------------------------------------------------------------
$allPassed = -not @($criteria | Where-Object { -not $_.Passed }).Count
$text = New-Object System.Text.StringBuilder
[void] $text.AppendLine("# M1 engine gate: $(if ($allPassed) { 'READY FOR SIGN-OFF' } else { 'NOT READY' })")
[void] $text.AppendLine()
[void] $text.AppendLine("Build: $($evidence.build)")
[void] $text.AppendLine()
[void] $text.AppendLine('| Criterion | Result | Evidence |')
[void] $text.AppendLine('| --- | --- | --- |')
foreach ($c in $criteria) { [void] $text.AppendLine("| $($c.Name) | $(if ($c.Passed) { 'pass' } else { 'OPEN' }) | $($c.Detail) |") }
[void] $text.AppendLine('| 6. Gate report and plan v0.2 | for the owner | this report; plan v0.2 with the M2 cases detailed is a separate deliverable |')
if ($evidence.PSObject.Properties.Name -contains 'links' -and $evidence.links) {
    [void] $text.AppendLine()
    [void] $text.AppendLine("Links: CI $($evidence.links.ci); nightly runs: $(@($evidence.links.nights) -join ', ')")
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$report = Join-Path $OutputDir 'gate-report.md'
$text.ToString() | Set-Content -LiteralPath $report
Write-Host $text.ToString()
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $text.ToString() }
exit $(if ($allPassed) { 0 } else { 1 })
