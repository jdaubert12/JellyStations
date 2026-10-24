# Builds the plugin and runs the unit tests.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root "JellyStations.sln") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
dotnet test (Join-Path $root "tests\Jellyfin.Plugin.JellyStations.Tests") -c $Configuration --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
Write-Host "Output: src\Jellyfin.Plugin.JellyStations\bin\$Configuration\net10.0\Jellyfin.Plugin.JellyStations.dll" -ForegroundColor Green
