# Builds the plugin and installs it into the local test Jellyfin container (dev/docker-compose.yml).
param(
    [string]$Configuration = "Release",
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\Jellyfin.Plugin.JellyStations\Jellyfin.Plugin.JellyStations.csproj"
$version = "1.18.0.0"
$pluginDir = Join-Path $root "dev\config\plugins\JellyStations_$version"

Write-Host "Building $Configuration..." -ForegroundColor Cyan
dotnet build $proj -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Stage dll + meta.json through package.ps1: Jellyfin drops a plugin folder that has no manifest.
& (Join-Path $PSScriptRoot "package.ps1") -Configuration $Configuration -NoManifest | Out-Null
$stage = Join-Path $root "dist\JellyStations_$version"
New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item (Join-Path $stage "*") $pluginDir -Force
Write-Host "Installed to $pluginDir" -ForegroundColor Green

if (-not $NoRestart) {
    Write-Host "Restarting container jellystations-jf..." -ForegroundColor Cyan
    docker restart jellystations-jf | Out-Null
    Start-Sleep -Seconds 6
    docker logs --since 30s jellystations-jf 2>&1 | Select-String -Pattern "JellyStations|Loaded plugin|plugin" | Select-Object -Last 15
}
