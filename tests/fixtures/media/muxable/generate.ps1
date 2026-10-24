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
