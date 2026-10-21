<#
.SYNOPSIS
  Registers the Kasi native messaging host for the current user (development only; the installer does this in M4, KASI-BRW-001).

.DESCRIPTION
  Writes one manifest per browser family next to the host and points the per-user (HKCU) registry keys for Chrome, Edge and Firefox at it.
  Chromium browsers get "allowed_origins" from -ChromeExtensionId; Firefox gets "allowed_extensions" from -FirefoxExtensionId. A browser
  whose id is not given is skipped. Nothing outside HKCU and the manifest folder is touched. -Unregister removes the keys and the manifests.

.PARAMETER HostPath            Kasi.NativeHost.exe (default: the Release publish folder). Kasi.exe must sit in the same folder.
.PARAMETER ChromeExtensionId   The 32-letter id of the extension in Chrome and Edge.
.PARAMETER FirefoxExtensionId  The add-on id (for example kasi@martsoft.example) in Firefox.
#>
[CmdletBinding()]
param(
    [string] $HostPath = (Join-Path $PSScriptRoot '..\..\src\Kasi.NativeHost\bin\Release\net10.0\win-x64\publish\Kasi.NativeHost.exe'),
    [string] $ChromeExtensionId,
    [string] $FirefoxExtensionId,
    [switch] $Unregister
)

$ErrorActionPreference = 'Stop'

$hostName = 'com.martsoft.kasi'
$manifestDir = Join-Path $env:LOCALAPPDATA 'Kasi\NativeHost'
$template = Get-Content (Join-Path $PSScriptRoot '..\..\src\Kasi.NativeHost\Registration\com.martsoft.kasi.json') -Raw

$targets = @(
    @{ Browser = 'Chrome';  Key = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName";   Allow = 'allowed_origins';    Id = $ChromeExtensionId;  Format = 'chrome-extension://{0}/' }
    @{ Browser = 'Edge';    Key = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName";  Allow = 'allowed_origins';    Id = $ChromeExtensionId;  Format = 'chrome-extension://{0}/' }
    @{ Browser = 'Firefox'; Key = "HKCU:\Software\Mozilla\NativeMessagingHosts\$hostName";         Allow = 'allowed_extensions'; Id = $FirefoxExtensionId; Format = '{0}' }
)

foreach ($t in $targets) {
    if ($Unregister) {
        if (Test-Path $t.Key) { Remove-Item $t.Key -Recurse -Force; Write-Host "Removed $($t.Browser)" }
        continue
    }
    if (-not $t.Id) { Write-Host "Skipped $($t.Browser): no extension id given"; continue }
    if ($t.Id -notmatch '^[A-Za-z0-9@._-]+$') { throw "The $($t.Browser) extension id contains characters that are not allowed." }
    if (-not (Test-Path $HostPath)) { throw "Host not found: $HostPath (publish it first: dotnet publish src/Kasi.NativeHost -c Release -r win-x64)" }

    New-Item -ItemType Directory -Force $manifestDir | Out-Null
    $manifest = $template.Replace('@HOST_PATH@', ((Resolve-Path $HostPath).Path -replace '\\', '\\')).
        Replace('@ALLOW_KEY@', $t.Allow).
        Replace('@ALLOWED@', ('"' + ($t.Format -f $t.Id) + '"'))
    $file = Join-Path $manifestDir "$hostName.$($t.Browser.ToLowerInvariant()).json"
    Set-Content -Path $file -Value $manifest -Encoding utf8NoBOM
    New-Item -Path $t.Key -Force | Out-Null
    Set-ItemProperty -Path $t.Key -Name '(default)' -Value $file
    Write-Host "Registered $($t.Browser): $file"
}

if ($Unregister -and (Test-Path $manifestDir)) { Remove-Item $manifestDir -Recurse -Force }
