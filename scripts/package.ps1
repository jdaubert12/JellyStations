# Packages the plugin as a zip for manual install, and records the release in manifest.json, the Jellyfin
# plugin repository file served from the repo (Dashboard > Plugins > Repositories).
param(
    [string]$Configuration = "Release",
    [switch]$NoManifest   # dev builds (deploy-local.ps1) must not record a checksum nobody will release
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.30.0.0"
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
    changelog = "4K channels and starter blocks. The Resolution list in the encoding settings offers 3840 x 2160 (4K). A channel has one picture size, so a 4K movie on a 1080p station is scaled down and a station set to 4K airs everything at 4K. With libx264 the H.264 level is now raised by itself when the size and frame rate need more than the one set (5.1 for 4K, 4.2 for 1080p at 60 fps); before, the stream carried the level as typed. Encoding check: a profile that is likely to disappoint is flagged at the top of the encoding form as you type, as an Encoding badge on the row of a station with its own profile, and on the Status page, where a weak default encoding is listed once however many stations follow it. It flags 4K from an H.264 encoder, a video bitrate too low for the picture (under 10000 kbps at 4K, 6000 at 1440p, 3000 at 1080p, 1500 at 720p, 600 below; HEVC about two thirds of that; half as much again at 50 or 60 fps), one so high it only costs bandwidth (two and a half times the figure that suits the size, such as 20000 kbps left on a station that went back to 1080p), and audio under 96 kbps; each names the figure that suits. Picking a size in the Resolution list now also moves the video bitrate to the figure that suits it (3500 at 720p, 8000 at 1080p, 20000 at 4K, less with an HEVC encoder below 4K) while the field still holds the suggested figure for the size you are leaving; a bitrate you set yourself is not touched. Every expanding source (library, genre, tag, series, collection, playlist) can be limited by picture size: 4K only, 1080p or better, 720p or better, by the size Jellyfin read from each file, so a 4K channel can play only 4K files and scale nothing up. Starter blocks: the dropdown next to ""Add block"" now offers Saturday morning cartoons, Prime-time movie, Late night and Weather on the hour beside your saved templates; they come with days, time and play order and without sources, and a template saved under the same name replaces one. Two fixes found while testing 4K: the stream kept only the last 2 MB for a viewer joining, less than one keyframe interval above about 8000 kbps, so a tune-in on a high-bitrate channel could start between keyframes and show nothing or garbage for up to the interval; it now keeps two intervals at the channel's bitrate. Weather screens are drawn at 1080 lines at most and scaled up on a larger channel, a tune-in no longer waits for a whole segment of screens to be drawn (the first segment is cut at what about 6 seconds of drawing gives and the rest are drawn while it plays), so a weather channel with several places and radar opens in seconds after a new hour or new reports. API: MinResolution on a content source, GET BlockTemplates/BuiltIn, EncodingWarnings per station and DefaultEncodingWarnings on GET Health."
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
