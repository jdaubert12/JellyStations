# Packages the plugin as a zip for manual install, and records the release in manifest.json, the Jellyfin
# plugin repository file served from the repo (Dashboard > Plugins > Repositories).
param(
    [string]$Configuration = "Release",
    [switch]$NoManifest   # dev builds (deploy-local.ps1) must not record a checksum nobody will release
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.20.0.0"
$dll = Join-Path $root "src\Jellyfin.Plugin.JellyStations\bin\$Configuration\net10.0\Jellyfin.Plugin.JellyStations.dll"
if (-not (Test-Path $dll)) { throw "Build first: $dll not found" }

$dist = Join-Path $root "dist"
$stage = Join-Path $dist "JellyStations_$version"
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $dll $stage -Force

# Keep this string ASCII: the script has no BOM, so Windows PowerShell 5.1 reads it as ANSI and an
# en dash or degree sign here breaks the parse. Inner double quotes are doubled.
$meta = @{
    guid = "b7f3c1d2-5e8a-4f0b-9c6d-2a1e7f4b8d31"
    name = "JellyStations"
    description = "Curate always-on TV stations from your library, surfaced as native Live TV channels."
    overview = "Curate always-on TV stations from your library."
    owner = "JD"
    category = "LiveTV"
    version = $version
    targetAbi = "12.0.0.0"
    framework = "net10.0"
    changelog = "Alerts and title cards on the weather channel. While a place has a watch, warning or advisory in force, the channel shows an alerts screen for it before its current conditions: each alert with its severity colour, when it runs (in the place's own time), the area it covers and as much of its text as fits, up to three alerts per screen with the rest counted; the current, hourly and 7-day screens carry a one-line strip meanwhile. Alerts come from the US National Weather Service's free API (no key; United States and territories only, places elsewhere skip the screen), are checked every five minutes while the station is on and cached under data/JellyStations/weather/alerts. Each place now opens with a short title card (its name and region over a faint icon of its conditions, 3 seconds by default, 1 to 10) so the eye knows the channel has moved on. Both are on by default, with checkboxes in the editor; the forecast status gets an Alerts column and Preview screen can show both screens. Weather on ordinary stations: a time block can now play the local weather instead of programs (What plays: Local weather), and any block can repeat every N minutes until a time, so ""the weather at the top of every hour"" is a 5-minute weather block at 06:00 repeating every 60 minutes; the bumpers section gains Local weather in the breaks, a short run of the weather screens (up to a set length, before or after the clips) in every break with at least 45 seconds of room. Both use the station's Weather section, which appears in the editor when needed. Music levelling: every weather music track is measured once in the background (ffmpeg loudnorm) and played at -18 LUFS with peaks under -1 dB, on by default, with a status line in the editor; tracks fade out and in at their boundaries."
    timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    status = "Active"
    autoUpdate = $true
    imagePath = ""
}
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
# Jellyfin parses meta.json from raw bytes and rejects a UTF-8 BOM, which Out-File -Encoding utf8 emits on PowerShell 5.1.
[System.IO.File]::WriteAllText((Join-Path $stage "meta.json"), ($meta | ConvertTo-Json), $utf8NoBom)

$zip = Join-Path $dist "JellyStations_$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLower()
Write-Host "Created $zip" -ForegroundColor Green
Write-Host "MD5: $md5"

# Upsert this version in manifest.json. The sourceUrl is the GitHub release asset, so the release must be
# created with this exact zip (scripts in README: gh release create v$version dist\JellyStations_$version.zip)
# and the manifest committed and pushed before Jellyfin can see the version.
if ($NoManifest) { return }
$manifestPath = Join-Path $root "manifest.json"
# Assign first, then wrap: on PowerShell 5.1 ConvertFrom-Json emits a JSON array as one Object[], and
# @( ... | ConvertFrom-Json ) would nest it inside another array.
$manifest = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($manifestPath, $utf8NoBom))
$manifest = @($manifest)
$plugin = $manifest | Where-Object { $_.guid -eq $meta.guid } | Select-Object -First 1
if ($null -eq $plugin) { throw "manifest.json has no entry for guid $($meta.guid)" }
$entry = [pscustomobject]@{
    version = $version
    changelog = $meta.changelog
    targetAbi = $meta.targetAbi
    sourceUrl = "https://github.com/jdaubert12/JellyStations/releases/download/v$version/JellyStations_$version.zip"
    checksum = $md5
    timestamp = $meta.timestamp
}
$others = @($plugin.versions | Where-Object { $_.version -ne $version })
$plugin.versions = @($entry) + $others
$plugin.description = $meta.description
$plugin.owner = $meta.owner
$plugin.overview = $meta.overview
$json = ConvertTo-Json -InputObject $manifest -Depth 10
# ConvertTo-Json on 5.1 escapes non-ASCII as \uXXXX, which is valid JSON; normalise line endings only.
[System.IO.File]::WriteAllText($manifestPath, ($json -replace "`r`n", "`n") + "`n", $utf8NoBom)
Write-Host "manifest.json: $version recorded (checksum $md5)" -ForegroundColor Green
