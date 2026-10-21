<#
.SYNOPSIS
  Fuzzes the Content-Disposition and URL filename parser with SharpFuzz and libFuzzer (TPL 7.2; KASI-ENG-003, TC-ENG-004, TC-ENG-005).

.DESCRIPTION
  Publishes tests/Kasi.Fuzz, instruments Kasi.Engine.dll with the sharpfuzz tool (dotnet-tools.json), seeds a corpus from
  tests/fixtures/disposition-corpus/seeds.txt and the saved findings in tests/fixtures/fuzz/crashes, and runs libfuzzer-dotnet for
  -Seconds (3 minutes is the TPL budget for this target). A crash, an exception from a violated invariant, a hang over 2 seconds or
  a process over -RssLimitMegabytes ends the run: the input is minimised and copied to <OutputDir>/findings, and the exit code is 1.
  Add the minimised file to tests/fixtures/fuzz/crashes with the fix and file a High defect (TPL 7.2).

.PARAMETER LibFuzzer  Path to libfuzzer-dotnet (libfuzzer-dotnet-windows.exe from github.com/Metalnem/libfuzzer-dotnet releases).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $LibFuzzer,
    [int] $Seconds = 180,
    [string] $OutputDir = 'artifacts/fuzz',
    [int] $TimeoutSeconds = 2,
    [int] $RssLimitMegabytes = 512
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not (Test-Path -LiteralPath $LibFuzzer)) { throw "libfuzzer-dotnet not found at '$LibFuzzer'." }
$bin = Join-Path $OutputDir 'bin'
$corpus = Join-Path $OutputDir 'corpus'
$findings = Join-Path $OutputDir 'findings'
Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $bin, $corpus, $findings | Out-Null

dotnet publish tests/Kasi.Fuzz/Kasi.Fuzz.csproj -c Release -o $bin --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# only Kasi.Engine is under test; instrumenting the runtime or SharpFuzz itself would only slow the run down
dotnet tool restore | Out-Null
dotnet tool run sharpfuzz (Join-Path $bin 'Kasi.Engine.dll')
if ($LASTEXITCODE -ne 0) { throw 'instrumentation failed' }

# the corpus: every seed as "header\nurl", plus every saved finding
$index = 0
foreach ($seed in Get-Content tests/fixtures/disposition-corpus/seeds.txt | Where-Object { $_ }) {
    [IO.File]::WriteAllText((Join-Path $corpus ('seed-{0:D3}' -f $index++)), "$seed`n$seed", [Text.UTF8Encoding]::new($false))
}
Get-ChildItem tests/fixtures/fuzz/crashes -File | Where-Object { $_.Extension -ne '.md' } | Copy-Item -Destination $corpus

$target = Join-Path $bin 'Kasi.Fuzz.dll'
$common = @("--target_path=dotnet", "--target_arg=$target")
$prefix = (Resolve-Path $findings).Path + [IO.Path]::DirectorySeparatorChar
& $LibFuzzer @common "-timeout=$TimeoutSeconds" "-rss_limit_mb=$RssLimitMegabytes" "-max_total_time=$Seconds" "-artifact_prefix=$prefix" $corpus
$exit = $LASTEXITCODE

$crashes = @(Get-ChildItem $findings -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(crash|timeout|oom|leak)-' })
if ($exit -eq 0 -and $crashes.Count -eq 0) {
    Write-Host "fuzzing finished: no finding in $Seconds s"
    exit 0
}

foreach ($crash in $crashes) {
    Write-Host "finding: $($crash.Name); minimising"
    & $LibFuzzer @common "-timeout=$TimeoutSeconds" "-rss_limit_mb=$RssLimitMegabytes" '-minimize_crash=1' '-runs=20000' "-exact_artifact_path=$findings/minimized-$($crash.Name)" $crash.FullName | Out-Null
}
"Reproduce: dotnet $target --replay <file>" | Set-Content (Join-Path $findings 'README.txt')
Write-Host "FINDING: see $findings. Reproduce with: dotnet $target --replay <file>"
exit 1
