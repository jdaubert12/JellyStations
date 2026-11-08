# Packages the plugin as a zip for manual install, and records the release in manifest.json, the Jellyfin
# plugin repository file served from the repo (Dashboard > Plugins > Repositories).
param(
    [string]$Configuration = "Release",
    [switch]$NoManifest   # dev builds (deploy-local.ps1) must not record a checksum nobody will release
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.33.0.0"
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
    changelog = "Week layouts, and block templates that travel. A week layout is a whole broadcast week of time blocks in one piece. Three ship with the plugin: Classic broadcast week (cartoons on weekday mornings and all Saturday morning, reruns through the day, a Sunday matinee, a movie every night at eight, grown-up shows late, nothing above PG-13 before eight), Movies all day (a family matinee, the main feature at eight, a late movie, shuffled films between) and Kids by day, grown-ups by night. Suggest stations now offers them first, under Broadcast week: each block is filled from the library by what it is for (everything rated G or below for cartoons, half-hour comedies for reruns or every show when there are too few, movies rated PG or below for the matinee, PG-13 and up for late night), with everything else in the pool between the blocks, and a block the library has nothing for is left out and named. The same preview, draft and create buttons apply. In the editor, the dropdown next to Add block lists the layouts: one adds all its blocks at once, without sources, for you to fill. Save blocks as layout keeps a station's blocks (and its family hours) as a layout of your own, with or without their sources; a layout saved with its sources is offered under Suggest stations too, and one saved under a built-in's name takes its place. Saved layouts are listed in Settings and travel with Export and Import. Block templates: the bookmark on a block now asks for a name and whether to keep the sources, so a template can carry its shows or only its shape. When a saved template or a layout is added to a station, any source whose id this server does not know (a template imported from another server, a library removed and added again) is matched by title the way Import does, and what cannot be matched is dropped and said. API: GET/POST/DELETE /JellyStations/WeekLayouts, POST WeekLayouts/{id}/Apply, POST BlockTemplates/{id}/Apply; the export document is version 3 and carries WeekLayouts. Three more things in the same release. A time block can carry a guide title (Saturday Morning Cartoons, The Late Movie): the Jellyfin guide shows it as the title of every program the block plays, with the program's own title on the episode line; changing it redraws the guide without reshuffling the station. Programs are marked New or Repeat in the guide from the station's air history (new when the item has not aired on that station before), and anything rated G, TV-Y, TV-Y7 or TV-G is marked Kids; the schedule preview shows the same New badge. Save blocks as layout now asks what Suggest stations should fill each block with from the library (its own sources, everything, kids, family, family movies, movies, reruns or grown-up) and what plays between the blocks, so a layout of your own is offered there the way the built-in ones are. API: GuideTitle on a time block and on schedule entries; Fills and Filler on POST WeekLayouts."
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
