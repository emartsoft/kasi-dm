<#
.SYNOPSIS
  Registers the Kasi native messaging host for the current user (development only; the installer does this in M4, KASI-BRW-001).

.DESCRIPTION
  Writes one manifest per browser next to the host and points the per-user (HKCU) registry keys for Chrome, Edge, Chromium and Firefox at it.
  Chromium browsers get "allowed_origins", Firefox gets "allowed_extensions" (KASI-NFR-010, TC-BRW-007). Each browser allows the store ids in
  extension/store-ids.json plus any ids given on the command line (the developer id of an unpacked build); the lists are not shared between
  browsers: Chrome and Chromium use the Chrome list, Edge uses the Edge list. A browser with no id at all is skipped.
  Ids are validated before anything is written. Nothing outside the registry root and the manifest folder is touched.
  -Unregister removes the keys and the manifest files this script writes and nothing else; it is safe to run twice.

.PARAMETER HostPath            Kasi.NativeHost.exe (default: the Release publish folder). Kasi.exe must sit in the same folder.
.PARAMETER ChromeExtensionId   Extra 32-letter ids for Chrome and Chromium (for example an unpacked build).
.PARAMETER EdgeExtensionId     Extra 32-letter ids for Edge.
.PARAMETER FirefoxExtensionId  Extra add-on ids for Firefox (for example kasi-dev@example.test).
.PARAMETER StoreIdsFile        The store ids (default: extension/store-ids.json).
.PARAMETER RegistryRoot        Where the browsers' keys live (default HKCU:\Software); a test passes a private root.
.PARAMETER ManifestDirectory   Where the manifests are written (default %LOCALAPPDATA%\Kasi\NativeHost).
#>
[CmdletBinding()]
param(
    [string] $HostPath = (Join-Path $PSScriptRoot '..\..\src\Kasi.NativeHost\bin\Release\net10.0\win-x64\publish\Kasi.NativeHost.exe'),
    [string[]] $ChromeExtensionId = @(),
    [string[]] $EdgeExtensionId = @(),
    [string[]] $FirefoxExtensionId = @(),
    [string] $StoreIdsFile = (Join-Path $PSScriptRoot '..\..\extension\store-ids.json'),
    [string] $RegistryRoot = 'HKCU:\Software',
    [string] $ManifestDirectory = (Join-Path $env:LOCALAPPDATA 'Kasi\NativeHost'),
    [switch] $Unregister
)

$ErrorActionPreference = 'Stop'

$hostName = 'com.martsoft.kasi'
$chromiumId = '^[a-p]{32}$'
$firefoxId = '^[A-Za-z0-9@._-]+$'

$store = Get-Content $StoreIdsFile -Raw | ConvertFrom-Json
$chrome = @(@($store.chrome) + @($ChromeExtensionId) | Where-Object { $_ } | Select-Object -Unique)
$edge = @(@($store.edge) + @($EdgeExtensionId) | Where-Object { $_ } | Select-Object -Unique)
$firefox = @(@($store.firefox) + @($FirefoxExtensionId) | Where-Object { $_ } | Select-Object -Unique)

$targets = @(
    @{ Browser = 'Chrome';   Key = "$RegistryRoot\Google\Chrome\NativeMessagingHosts\$hostName";  Allow = 'allowed_origins';    Ids = $chrome;  Pattern = $chromiumId; Format = 'chrome-extension://{0}/' }
    @{ Browser = 'Edge';     Key = "$RegistryRoot\Microsoft\Edge\NativeMessagingHosts\$hostName"; Allow = 'allowed_origins';    Ids = $edge;    Pattern = $chromiumId; Format = 'chrome-extension://{0}/' }
    @{ Browser = 'Chromium'; Key = "$RegistryRoot\Chromium\NativeMessagingHosts\$hostName";       Allow = 'allowed_origins';    Ids = $chrome;  Pattern = $chromiumId; Format = 'chrome-extension://{0}/' }
    @{ Browser = 'Firefox';  Key = "$RegistryRoot\Mozilla\NativeMessagingHosts\$hostName";        Allow = 'allowed_extensions'; Ids = $firefox; Pattern = $firefoxId;  Format = '{0}' }
)

function Get-ManifestFile($target) { Join-Path $ManifestDirectory "$hostName.$($target.Browser.ToLowerInvariant()).json" }

if ($Unregister) {
    foreach ($t in $targets) {
        if (Test-Path $t.Key) { Remove-Item $t.Key -Recurse -Force; Write-Host "Removed $($t.Browser)" }
        $file = Get-ManifestFile $t
        if (Test-Path $file) { Remove-Item $file -Force }
    }
    # the folder goes only when it is empty: it may be shared
    if ((Test-Path $ManifestDirectory) -and -not (Get-ChildItem $ManifestDirectory -Force)) { Remove-Item $ManifestDirectory -Force }
    return
}

# all ids are checked before the first key or file is written
foreach ($t in $targets) {
    foreach ($id in $t.Ids) {
        if ($id -cnotmatch $t.Pattern) { throw "The $($t.Browser) extension id '$id' is not valid." }
    }
}

$template = Get-Content (Join-Path $PSScriptRoot '..\..\src\Kasi.NativeHost\Registration\com.martsoft.kasi.json') -Raw

foreach ($t in $targets) {
    if ($t.Ids.Count -eq 0) { Write-Host "Skipped $($t.Browser): no extension id"; continue }
    if (-not (Test-Path $HostPath)) { throw "Host not found: $HostPath (publish it first: dotnet publish src/Kasi.NativeHost -c Release -r win-x64)" }

    New-Item -ItemType Directory -Force $ManifestDirectory | Out-Null
    $allowed = ($t.Ids | ForEach-Object { '"' + ($t.Format -f $_) + '"' }) -join ', '
    $manifest = $template.Replace('@HOST_PATH@', ((Resolve-Path $HostPath).Path -replace '\\', '\\')).
        Replace('@ALLOW_KEY@', $t.Allow).
        Replace('@ALLOWED@', $allowed)
    $file = Get-ManifestFile $t
    Set-Content -Path $file -Value $manifest -Encoding utf8NoBOM
    New-Item -Path $t.Key -Force | Out-Null
    Set-ItemProperty -Path $t.Key -Name '(default)' -Value $file
    Write-Host "Registered $($t.Browser): $file"
}
