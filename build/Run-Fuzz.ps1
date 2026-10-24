<#
.SYNOPSIS
  Fuzzes one target with SharpFuzz and libFuzzer (TPL 7.2): the Content-Disposition and URL filename parser (-Target filename;
  KASI-ENG-003, TC-ENG-004, TC-ENG-005) or the native messaging host's frame reader, strict parser and error replies (-Target host;
  KASI-NFR-010, TC-BRW-006).

.DESCRIPTION
  Publishes tests/Kasi.Fuzz, instruments the assemblies under test with the sharpfuzz tool (dotnet-tools.json: Kasi.Engine.dll for
  filename; Kasi.Ipc.dll and Kasi.NativeHost.dll for host), seeds a corpus (filename: tests/fixtures/disposition-corpus/seeds.txt and the
  saved findings in tests/fixtures/fuzz/crashes; host: one valid message of every type from Kasi.Fuzz --host-seeds and the saved findings
  in tests/fixtures/fuzz/host-crashes), and runs libfuzzer-dotnet for -Seconds (the TPL budget is 3 minutes for filename and 5 for host,
  which are the defaults). A crash, an exception from a violated invariant, a hang over 2 seconds or
  a process over -RssLimitMegabytes ends the run: the input is minimised and copied to <OutputDir>/findings, and the exit code is 1.
  Add the minimised file to tests/fixtures/fuzz/crashes (host: host-crashes) with the fix and file a High defect (TPL 7.2).

.PARAMETER LibFuzzer  Path to libfuzzer-dotnet (libfuzzer-dotnet-windows.exe from github.com/Metalnem/libfuzzer-dotnet releases).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $LibFuzzer,
    [ValidateSet('filename', 'host', 'hls', 'dash')] [string] $Target = 'filename',
    [int] $Seconds = 0,
    [string] $OutputDir = '',
    [int] $TimeoutSeconds = 2,
    [int] $RssLimitMegabytes = 512
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if ($Seconds -eq 0) { $Seconds = if ($Target -eq 'host' -or $Target -eq 'hls' -or $Target -eq 'dash') { 300 } else { 180 } }
if (-not $OutputDir) { $OutputDir = if ($Target -eq 'host') { 'artifacts/fuzz-host' } elseif ($Target -in 'hls','dash') { "artifacts/fuzz-$Target" } else { 'artifacts/fuzz' } }
if (-not (Test-Path -LiteralPath $LibFuzzer)) { throw "libfuzzer-dotnet not found at '$LibFuzzer'." }
$bin = Join-Path $OutputDir 'bin'
$corpus = Join-Path $OutputDir 'corpus'
$findings = Join-Path $OutputDir 'findings'
Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $bin, $corpus, $findings | Out-Null

dotnet publish tests/Kasi.Fuzz/Kasi.Fuzz.csproj -c Release -o $bin --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$fuzzDll = Join-Path $bin 'Kasi.Fuzz.dll'
if ($Target -eq 'host') {
    # the corpus: one valid message of every type (mode byte 0: the payload of a frame); written before instrumenting, because an
    # instrumented assembly only runs under the fuzzer
    $env:KASI_FUZZ_TARGET = 'host'
    dotnet $fuzzDll --host-seeds $corpus
    if ($LASTEXITCODE -ne 0) { throw 'writing the host seeds failed' }
}

# only the assemblies under test are instrumented; instrumenting the runtime or SharpFuzz itself would only slow the run down
dotnet tool restore | Out-Null
$under = if ($Target -eq 'host') { 'Kasi.Ipc.dll', 'Kasi.NativeHost.dll' } elseif ($Target -in 'hls','dash') { 'Kasi.Media.dll' } else { 'Kasi.Engine.dll' }
foreach ($assembly in $under) {
    dotnet tool run sharpfuzz (Join-Path $bin $assembly)
    if ($LASTEXITCODE -ne 0) { throw "instrumentation of $assembly failed" }
}

if ($Target -eq 'host') {
    $env:KASI_FUZZ_TARGET = 'host'
    $crashDir = 'tests/fixtures/fuzz/host-crashes'
}
elseif ($Target -in 'hls','dash') {
    $env:KASI_FUZZ_TARGET = $Target
    $pattern = if ($Target -eq 'hls') { '*.m3u8' } else { '*.mpd' }
    Get-ChildItem tests/fixtures/media -Recurse -File -Filter $pattern | Copy-Item -Destination $corpus
    $crashDir = 'tests/fixtures/fuzz/media-crashes'
}
else {
    # the corpus: every seed as "header\nurl", plus every saved finding
    $index = 0
    foreach ($seed in Get-Content tests/fixtures/disposition-corpus/seeds.txt | Where-Object { $_ }) {
        [IO.File]::WriteAllText((Join-Path $corpus ('seed-{0:D3}' -f $index++)), "$seed`n$seed", [Text.UTF8Encoding]::new($false))
    }
    $crashDir = 'tests/fixtures/fuzz/crashes'
}
Get-ChildItem $crashDir -File | Where-Object { $_.Extension -ne '.md' } | Copy-Item -Destination $corpus

$replayHint = $(if ($Target -ne 'filename') { "`$env:KASI_FUZZ_TARGET='$Target'; " } else { '' }) + "dotnet $fuzzDll --replay <file>"
$common = @("--target_path=dotnet", "--target_arg=$fuzzDll")
$prefix = (Resolve-Path $findings).Path + [IO.Path]::DirectorySeparatorChar
& $LibFuzzer @common "-timeout=$TimeoutSeconds" "-rss_limit_mb=$RssLimitMegabytes" "-max_total_time=$Seconds" "-artifact_prefix=$prefix" $corpus
$exit = $LASTEXITCODE

$crashes = @(Get-ChildItem $findings -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(crash|timeout|oom|leak)-' })
if ($exit -eq 0 -and $crashes.Count -eq 0) {
    Write-Host "fuzzing ($Target) finished: no finding in $Seconds s"
    exit 0
}

foreach ($crash in $crashes) {
    Write-Host "finding: $($crash.Name); minimising"
    & $LibFuzzer @common "-timeout=$TimeoutSeconds" "-rss_limit_mb=$RssLimitMegabytes" '-minimize_crash=1' '-runs=20000' "-exact_artifact_path=$findings/minimized-$($crash.Name)" $crash.FullName | Out-Null
}
"Reproduce: $replayHint" | Set-Content (Join-Path $findings 'README.txt')
Write-Host "FINDING: see $findings. Reproduce with: $replayHint"
exit 1
