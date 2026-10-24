# Fetches the pinned LGPL FFmpeg release asset and verifies its SHA-256.
# Usage:
#   pwsh build/Fetch-FFmpeg.ps1 -Url https://.../ffmpeg-lgpl.zip -Sha256 <64 hex> -Destination third_party/ffmpeg
# The asset is not checked into git. The installer bundles the extracted ffmpeg.exe/ffprobe.exe and the licence/source offer.
param(
    [Parameter(Mandatory = $true)] [string] $Url,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $Sha256,
    [Parameter(Mandatory = $true)] [string] $Destination
)

$ErrorActionPreference = 'Stop'
$archive = Join-Path ([IO.Path]::GetTempPath()) ("kasi-ffmpeg-" + [guid]::NewGuid().ToString('N') + ".zip")
try {
    Invoke-WebRequest -Uri $Url -OutFile $archive
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actual -ne $Sha256.ToUpperInvariant()) {
        throw "FFmpeg archive checksum mismatch: expected $Sha256, got $actual"
    }

    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force

    $ffmpeg = Get-ChildItem -LiteralPath $Destination -Filter ffmpeg.exe -Recurse | Select-Object -First 1
    $ffprobe = Get-ChildItem -LiteralPath $Destination -Filter ffprobe.exe -Recurse | Select-Object -First 1
    if (-not $ffmpeg -or -not $ffprobe) { throw "The archive did not contain ffmpeg.exe and ffprobe.exe" }

    Write-Host "Verified FFmpeg $($ffmpeg.FullName) and $($ffprobe.FullName)"
}
finally {
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
}
