# Packages the plugin as a zip suitable for manual install or a plugin repository manifest.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.11.0.0"
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
    changelog = "HDR sources (PQ or HLG) are tone mapped to SDR BT.709 and stripped of HDR side data, and every segment is tagged BT.709, so clients no longer re-encode a channel because of ""video range type""; stations recompile once at startup to record which items are HDR. New ""Hardware decoding"" and ""Hardware device"" fields on the encoding profile (default ""Follow Jellyfin"": qsv, vaapi, cuda, videotoolbox or d3d11va), falling back to software for the session if the device fails. h264_vaapi now gets its device and frame upload. Frame rate and bitrate hints: 24 fps for film stations, 3500 kbps for 720p, 8000 for 1080p."
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
