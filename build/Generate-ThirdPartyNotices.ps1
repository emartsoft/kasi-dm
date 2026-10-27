# Generates THIRD-PARTY-NOTICES.txt from Directory.Packages.props. The committed file must equal the output; the
# ThirdPartyNoticesTests check that every package is present.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$props = [xml](Get-Content (Join-Path $root 'Directory.Packages.props') -Raw)
$packages = @($props.Project.ItemGroup.PackageVersion | ForEach-Object { $_.Include } | Sort-Object -Unique)

function Get-Licence([string]$name) {
    if ($name -match '^xunit|^AwesomeAssertions') { return 'Apache-2.0' }
    if ($name -match '^FsCheck') { return 'BSD-3-Clause' }
    if ($name -match '^Dapper|^Serilog') { return 'Apache-2.0' }
    return 'MIT'
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('Kasi Download Manager — third-party notices')
$lines.Add('=========================================')
$lines.Add('')
$lines.Add('Generated from Directory.Packages.props by build/Generate-ThirdPartyNotices.ps1. Do not edit by hand.')
$lines.Add('')
$lines.Add('Packages')
$lines.Add('--------')
foreach ($package in $packages) {
    $lines.Add("$package — $(Get-Licence $package)")
}
$lines.Add('')
$lines.Add('FFmpeg / ffprobe')
$lines.Add('----------------')
$lines.Add('Kasi invokes an external FFmpeg build for stream-copy muxing and duration probing. FFmpeg is not linked into any Kasi')
$lines.Add('assembly and is not distributed in this repository.')
$lines.Add('')
$lines.Add('- Project: https://ffmpeg.org/')
$lines.Add('- Licence of the shipped build: GNU Lesser General Public License, version 2.1 or later (LGPL-2.1-or-later), without')
$lines.Add('  GPL components and without `--enable-nonfree`.')
$lines.Add('- The exact build, version and SHA-256 are recorded in `docs/legal/ffmpeg-lgpl.md` and pinned in the release asset.')
$lines.Add('- Source code offer: the corresponding FFmpeg source, the build configuration and the scripts used to produce the shipped')
$lines.Add('  binary are offered with the release; see `docs/legal/ffmpeg-lgpl.md`.')
$lines.Add('- The binary is replaceable: Kasi locates it by path and never embeds it in an assembly.')
$lines.Add('')
$lines.Add('No GPL, AGPL or non-free component is part of a Kasi build. MonoTorrent, Velopack and Sentry are not included in this')
$lines.Add('build; they will be added to this file when the corresponding M4 work lands.')

[System.IO.File]::WriteAllLines((Join-Path $root 'THIRD-PARTY-NOTICES.txt'), $lines, [System.Text.UTF8Encoding]::new($false))
Write-Output "Wrote $($packages.Count) packages."
