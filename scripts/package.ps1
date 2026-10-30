# Packages the plugin as a zip for manual install, and records the release in manifest.json, the Jellyfin
# plugin repository file served from the repo (Dashboard > Plugins > Repositories).
param(
    [string]$Configuration = "Release",
    [switch]$NoManifest   # dev builds (deploy-local.ps1) must not record a checksum nobody will release
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.23.0.0"
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
    changelog = "Smarter franchise detection and a Classic Movies suggestion. Suggest stations now groups movies by the collection their metadata provider puts them in (TMDB's ""James Bond Collection"" and the like, stamped on every member whether or not Jellyfin builds the box set), so a series whose titles share nothing (Dr. No, Goldfinger, Skyfall) gets its marathon and its Franchise Nights slot; two members are enough, and the ""Collection"" suffix is dropped from the name. Titles without a collection are still grouped by shared prefix as before. A new ""Classic Movies"" suggestion offers every movie made before 1970 as one shuffled station once they total 8 hours, since old films rarely reach the decade rule's 8 hours one decade at a time; the cut-off year and the hours are thresholds, and the small-library toggle lowers the hours like the other rules. The station it creates is the same as setting a Movies library source to movies only with To year 1969, which any source could already do."
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
