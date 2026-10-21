# Packages the plugin as a zip suitable for manual install or a plugin repository manifest.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.13.0.0"
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
    changelog = "Up next card: a station can draw a panel over the last seconds of every program with the next program's title, artwork and start and end time, in a chosen corner at one or more times before the end, fading or sliding in and out, burned into the stream so every viewer sees it in step with the picture. Off by default. Fix: QSV (Intel Quick Sync) decoding failed on every file with ""Impossible to convert between the formats"", so stations and the hardware test never got Intel hardware decoding. The plugin now names the QSV decoder for the file's codec (h264_qsv, hevc_qsv, ...) on a QSV device created up front, which decodes to system memory; a file whose codec was never recorded stays in software. The hardware test confirms the decoder from ffmpeg's stream mapping or from the GPU device node ffmpeg holds open while it runs, and reports an unconfirmable hwaccel as unverified rather than as a problem. Fix: with the h264_qsv, h264_nvenc or h264_amf encoder a client tuning in mid-program got audio but no video until the next program started, because those encoders turn a forced key frame into a plain I frame without headers; they now set key frames by GOP alone, as Jellyfin does, so every GOP starts with an IDR frame and its SPS/PPS."
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
