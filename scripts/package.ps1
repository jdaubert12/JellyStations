# Packages the plugin as a zip suitable for manual install or a plugin repository manifest.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.12.0.0"
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
    changelog = "Stream health on the Status page: each active session shows whether the hardware decoder is really in use (confirmed by the GPU device ffmpeg holds open, or flagged when it fell back to software), the encoder, HDR tone mapping, CPU cores and how far ahead of real time the encoder runs, with problem lines for stutter or stalls. New Hardware section: Jellyfin's transcoding setting, what the default profile resolves to, visible /dev/dri nodes, this ffmpeg build's hwaccels and encoders, and a Test button that runs a few seconds of a station's program through the real chain and reports the decoder attached and the speed. Fix: files a GPU cannot decode (10-bit H.264, DivX/Xvid, codecs unticked in Jellyfin) no longer crash ffmpeg and switch the session to software; the plugin decides per file from the codec recorded at compile time, following Jellyfin's hardware decoding checkboxes, and a file the decoder still refuses is replayed in software on its own."
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
