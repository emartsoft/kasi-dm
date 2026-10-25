# Regenerates the checked-in muxable fixtures. Run from the repository root with an FFmpeg build that has libx264:
#   pwsh tests/fixtures/media/muxable/generate.ps1
# The files are checked in so CI does not run FFmpeg to create them; only the code under test invokes FFmpeg.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path (Join-Path $root 'hls'), (Join-Path $root 'dash') | Out-Null

ffmpeg -y -hide_banner -loglevel error `
    -f lavfi -i 'testsrc=size=160x90:rate=25' `
    -f lavfi -i 'sine=frequency=440:sample_rate=48000' `
    -t 24 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 100 -keyint_min 100 -sc_threshold 0 -b:v 160k `
    -c:a aac -b:a 48k -f hls -hls_time 4 -hls_playlist_type vod `
    -hls_segment_filename (Join-Path $root 'hls/segment-%d.ts') `
    (Join-Path $root 'hls/index.m3u8')

ffmpeg -y -hide_banner -loglevel error `
    -f lavfi -i 'testsrc=size=160x90:rate=25' `
    -t 24 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 100 -keyint_min 100 -sc_threshold 0 -b:v 160k -an `
    -f dash -seg_duration 4 -use_template 0 -use_timeline 0 `
    -init_seg_name 'init.mp4' -media_seg_name 'seg-$Number$.m4s' `
    (Join-Path $root 'dash/manifest.mpd')

ffmpeg -y -hide_banner -loglevel error `
    -f lavfi -i 'sine=frequency=440:sample_rate=48000' `
    -t 24 -c:a aac -b:a 48k (Join-Path $root 'audio.m4a')

# The HLS master is hand-written; the generated media playlist is the source of the six 4 s segments.

# A compact 600 s HLS fixture for the fake-clock live test. Store 150 segment sizes alongside one
# concatenated transport stream so CI does not need to generate or track 150 individual files.
$liveBuild = Join-Path ([IO.Path]::GetTempPath()) ('kasi-live-' + [guid]::NewGuid().ToString('N'))
$liveRoot = Join-Path $root 'live'
New-Item -ItemType Directory -Path $liveBuild, $liveRoot -Force | Out-Null
try {
    ffmpeg -y -hide_banner -loglevel error `
        -f lavfi -i 'color=c=black:s=160x90:r=5' `
        -f lavfi -i 'anullsrc=r=48000:cl=mono' `
        -t 600 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 20 -keyint_min 20 -sc_threshold 0 `
        -c:a aac -b:a 16k -f segment -segment_time 4 -segment_format mpegts `
        (Join-Path $liveBuild 'segment-%03d.ts')
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg could not generate the live fixture.' }

    $segments = @(Get-ChildItem -LiteralPath $liveBuild -Filter '*.ts' | Sort-Object Name)
    if ($segments.Count -ne 150) { throw "Expected 150 live segments; found $($segments.Count)." }
    $output = [IO.File]::Create((Join-Path $liveRoot 'segments.ts'))
    try {
        $sizes = foreach ($segment in $segments) {
            $bytes = [IO.File]::ReadAllBytes($segment.FullName)
            $output.Write($bytes)
            $bytes.Length.ToString([Globalization.CultureInfo]::InvariantCulture)
        }
    }
    finally { $output.Dispose() }
    [IO.File]::WriteAllLines((Join-Path $liveRoot 'sizes.txt'), [string[]]$sizes)
}
finally {
    Get-ChildItem -LiteralPath $liveBuild -File | Remove-Item
    Remove-Item -LiteralPath $liveBuild
}
