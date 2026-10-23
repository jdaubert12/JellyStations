# Packages the plugin as a zip suitable for manual install or a plugin repository manifest.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.14.0.0"
$dll = Join-Path $root "src\Jellyfin.Plugin.TvStation\bin\$Configuration\net10.0\Jellyfin.Plugin.TvStation.dll"
if (-not (Test-Path $dll)) { throw "Build first: $dll not found" }

$dist = Join-Path $root "dist"
$stage = Join-Path $dist "TvStation_$version"
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $dll $stage -Force

$meta = @{
    guid = "b7f3c1d2-5e8a-4f0b-9c6d-2a1e7f4b8d31"
    name = "TvStation"
    description = "Curate always-on TV stations from your library, surfaced as native Live TV channels."
    overview = "Curate always-on TV stations from your library."
    owner = "Justin"
    category = "LiveTV"
    version = $version
    targetAbi = "12.0.0.0"
    framework = "net10.0"
    changelog = "Weather channel: a new station mode that shows current conditions, the next twelve hours and the week ahead for up to eight places, drawn by the plugin as full-screen graphics (three colour themes, the station logo, a live clock in each place's own time zone) that cross-fade and cycle like the cable weather channel, with an optional ""on the 8s"" pass at :08, :18, :28..., a travel-cities screen and hourly guide entries titled from the places. Forecasts come from Open-Meteo (free, no account; only each place's latitude and longitude is sent), polled every 15 minutes, cached on disk, and the screens say when a forecast is older than the station allows. Music plays underneath from Jellyfin music (playlists, albums, artists, music genres, tracks) and/or every audio file in a folder on the server, in a seeded shuffle that is the same for every viewer and crosses segment boundaries without a gap, with the track title and artist on screen. The editor gets a Weather channel preset, place search, a music picker, a forecast status table with Fetch now, and screen previews. New endpoints under /TvStation/Weather (Search, Status, Refresh, Preview). The plugin ships no music; use tracks you have the right to play. Also: the Inter font (SIL OFL) is embedded for the screens. Music packs: the editor lists curated instrumental sets under licences that allow playing and passing them on; ""Get it"" fetches each track from the artist's own site into data/TvStation/music/<pack>/ with a SHA-256 check and a CREDITS.txt (starter pack: thirty Kevin MacLeod lounge and light jazz pieces, CC BY 4.0; optional: HoliznaCC0 lo-fi jazz guitar, CC0, downloaded by hand from the Free Music Archive). The music folder setting takes several folders separated by "";""."
    timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    status = "Active"
    autoUpdate = $true
    imagePath = ""
}
# Jellyfin parses meta.json from raw bytes and rejects a UTF-8 BOM, which Out-File -Encoding utf8 emits on PowerShell 5.1.
[System.IO.File]::WriteAllText((Join-Path $stage "meta.json"), ($meta | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))

$zip = Join-Path $dist "TvStation_$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLower()
Write-Host "Created $zip" -ForegroundColor Green
Write-Host "MD5: $md5"
