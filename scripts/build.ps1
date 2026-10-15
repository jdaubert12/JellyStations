# Builds the plugin and runs the unit tests.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root "TvStation.sln") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
dotnet test (Join-Path $root "tests\Jellyfin.Plugin.TvStation.Tests") -c $Configuration --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
Write-Host "Output: src\Jellyfin.Plugin.TvStation\bin\$Configuration\net10.0\Jellyfin.Plugin.TvStation.dll" -ForegroundColor Green
