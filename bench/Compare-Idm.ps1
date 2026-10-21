<#
.SYNOPSIS
  Head-to-head Kasi against Internet Download Manager (TPL 6.1; KASI-NFR-001).

.DESCRIPTION
  For every real source in bench/sources.json (placeholders are skipped; a source can be supplied at run time through the
  environment variable KASI_BENCH_URL_<ID>, with the id upper-cased and '-' turned into '_', which is how a signed URL with a
  query string stays out of the file) and every connection count (8, then 16): run IDM, then Kasi, alternating, -Runs times each.

  Timing is the same for both tools and is taken here, outside either tool: from the start of the call to the full-size file.
  IDM's command line only queues a download, so the script polls the target until it has the expected size and can be opened
  exclusively. Kasi is timed from process start to exit, which includes about 0.3 s of start-up and is a conservative bias against
  Kasi. The SHA-256 is computed afterwards, outside the timing; every run of a source must produce the same hash as the first one,
  or that run is marked unverified and its source is left out of the scorecard (never counted as fast).

  IDM takes its connection count from its own settings, not from the command line. Before each connection-count pass the script
  asks you to set IDM's maximum connections (use -NoPrompt for unattended or dry runs).

  The DNS cache is flushed before every run (Clear-DnsClientCache, needs elevation; -SkipDnsFlush records that it was not done).
  Run this on the dedicated benchmark machine, wired, with no other traffic, and note the line speed (-LineSpeedMbps) from a
  reference speed test; run each source at business hours and at night (-Window).

  Output: bench/results/idm-comparison-<date>.json (a session file, schema version 1) and a Markdown scorecard beside it.
  The exit code is that of the scorecard: 0 when the median ratio is at most 1.10 and no source is above 1.25.

.PARAMETER KasiBench  Path to the built Kasi.Bench.dll (dotnet build -c Release).
#>
[CmdletBinding()]
param(
    [string] $Sources = (Join-Path $PSScriptRoot 'sources.json'),
    [int[]] $Connections = @(8, 16),
    [int] $Runs = 5,
    [ValidateSet('business', 'night', 'unspecified')] [string] $Window = 'unspecified',
    [double] $LineSpeedMbps = 0,
    [string] $OutputDir = (Join-Path $PSScriptRoot 'results'),
    [string] $IdmPath = 'C:\Program Files (x86)\Internet Download Manager\IDMan.exe',
    [string] $KasiBench = (Join-Path $PSScriptRoot 'Kasi.Bench\bin\Release\net10.0\Kasi.Bench.dll'),
    [string] $WorkDir = (Join-Path ([IO.Path]::GetTempPath()) ('kasi-idm-' + [guid]::NewGuid().ToString('N'))),
    [string] $SourceId,
    [int] $TimeoutMinutes = 30,
    [switch] $SkipDnsFlush,
    [switch] $NoPrompt
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $IdmPath)) { throw "IDM not found at '$IdmPath' (use -IdmPath)." }
if (-not (Test-Path -LiteralPath $KasiBench)) { throw "Kasi.Bench not found at '$KasiBench' (dotnet build -c Release, or use -KasiBench)." }
New-Item -ItemType Directory -Force -Path $WorkDir, $OutputDir | Out-Null

# --- the sources to run -----------------------------------------------------------------------------------------------
$catalogue = Get-Content -LiteralPath $Sources -Raw | ConvertFrom-Json
$plan = @()
foreach ($source in $catalogue.sources) {
    if ($SourceId -and $source.id -ne $SourceId) { continue }
    $override = [Environment]::GetEnvironmentVariable('KASI_BENCH_URL_' + ($source.id.ToUpperInvariant() -replace '-', '_'))
    if ($source.placeholder -and -not $override) {
        Write-Host "skip $($source.id): placeholder, the owner has not supplied this source yet"
        continue
    }
    $url = if ($override) { $override } else { $source.url }
    $size = $source.sizeBytes
    if (-not $size) {
        $head = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing
        $size = [long] ($head.Headers['Content-Length'] | Select-Object -First 1)
    }
    $plan += [pscustomobject]@{ Id = $source.id; Url = $url; Size = [long] $size }
}
if ($plan.Count -eq 0) { throw 'No runnable source: every entry is a placeholder. Supply real sources in bench/sources.json or KASI_BENCH_URL_<ID>.' }

# --- timing -----------------------------------------------------------------------------------------------------------
function Clear-Dns {
    if (-not $SkipDnsFlush) { Clear-DnsClientCache }
}

function Test-FileReady([string] $path, [long] $size) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    if ((Get-Item -LiteralPath $path).Length -ne $size) { return $false }
    try {
        # IDM keeps the file locked until it has finished and renamed it
        $stream = [IO.File]::Open($path, 'Open', 'Read', 'None')
        $stream.Dispose()
        return $true
    } catch { return $false }
}

function Measure-Idm($source, [string] $name) {
    $target = Join-Path $WorkDir $name
    Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    Clear-Dns
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Start-Process -FilePath $IdmPath -ArgumentList '/d', $source.Url, '/p', $WorkDir, '/f', $name, '/n', '/q' | Out-Null
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not (Test-FileReady $target $source.Size)) {
        if ((Get-Date) -gt $deadline) { $clock.Stop(); return @{ Seconds = $clock.Elapsed.TotalSeconds; Path = $target; Done = $false } }
        Start-Sleep -Milliseconds 100
    }
    $clock.Stop()
    return @{ Seconds = $clock.Elapsed.TotalSeconds; Path = $target; Done = $true }
}

function Measure-Kasi($source, [string] $name, [int] $count) {
    $target = Join-Path $WorkDir $name
    Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    Clear-Dns
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & dotnet $KasiBench fetch --url $source.Url --folder $WorkDir --name $name --connections $count | Out-Null
    $exit = $LASTEXITCODE
    $clock.Stop()
    return @{ Seconds = $clock.Elapsed.TotalSeconds; Path = $target; Done = ($exit -eq 0 -and (Test-Path -LiteralPath $target)) }
}

# --- the alternating runs ---------------------------------------------------------------------------------------------
$rows = New-Object System.Collections.Generic.List[object]
$reference = @{}   # source id -> the first verified SHA-256
foreach ($count in $Connections) {
    if (-not $NoPrompt) { Read-Host "Set IDM's maximum connections per download to $count, then press Enter" | Out-Null }
    foreach ($source in $plan) {
        for ($run = 1; $run -le $Runs; $run++) {
            foreach ($tool in 'idm', 'kasi') {
                $name = "$($source.Id)-$tool-$count-$run.bin"
                $measured = if ($tool -eq 'idm') { Measure-Idm $source $name } else { Measure-Kasi $source $name $count }

                # verification is outside the timing
                $ok = $false
                $bytes = 0
                if ($measured.Done -and (Test-Path -LiteralPath $measured.Path)) {
                    $bytes = (Get-Item -LiteralPath $measured.Path).Length
                    $hash = (Get-FileHash -LiteralPath $measured.Path -Algorithm SHA256).Hash
                    if (-not $reference.ContainsKey($source.Id)) { $reference[$source.Id] = $hash }
                    $ok = ($bytes -eq $source.Size) -and ($hash -eq $reference[$source.Id])
                }
                Remove-Item -LiteralPath $measured.Path -Force -ErrorAction SilentlyContinue

                $mbps = if ($measured.Seconds -gt 0) { [math]::Round($bytes * 8 / 1e6 / $measured.Seconds, 2) } else { 0 }
                $rows.Add([ordered]@{
                    source = $source.Id; connections = $count; run = $run; tool = $tool
                    seconds = [math]::Round($measured.Seconds, 3); bytes = $bytes; mbps = $mbps
                    peakConnections = $count; sha256Ok = $ok; peakWorkingSetMegabytes = $null
                })
                Write-Host ("{0} c={1} run {2} {3}: {4:0.0} s {5}" -f $source.Id, $count, $run, $tool, $measured.Seconds, $(if ($ok) { 'ok' } else { 'UNVERIFIED' }))
            }
        }
    }
}

# --- the session file and the scorecard -------------------------------------------------------------------------------
$idmVersion = (Get-Item -LiteralPath $IdmPath).VersionInfo.ProductVersion
$session = [ordered]@{
    schemaVersion = 1
    startedUtc    = (Get-Date).ToUniversalTime().ToString('o')
    machine       = [ordered]@{
        cpu = "$env:PROCESSOR_IDENTIFIER"; logicalCores = [Environment]::ProcessorCount
        memoryMegabytes = [long] ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1MB); os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    }
    lineSpeedMbps = $(if ($LineSpeedMbps -gt 0) { $LineSpeedMbps } else { $null })
    window        = $Window
    dnsFlushed    = (-not $SkipDnsFlush)
    tools         = [ordered]@{ idm = "$idmVersion"; kasi = 'bench' }
    rows          = $rows
}
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmm'
$json = Join-Path $OutputDir "idm-comparison-$stamp.json"
$markdown = Join-Path $OutputDir "idm-comparison-$stamp.md"
$session | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $json -Encoding utf8
Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue

& dotnet $KasiBench scorecard $json --out $markdown
exit $LASTEXITCODE
