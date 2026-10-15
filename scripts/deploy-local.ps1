# Builds the plugin and installs it into the local test Jellyfin container (dev/docker-compose.yml).
param(
    [string]$Configuration = "Release",
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\Jellyfin.Plugin.TvStation\Jellyfin.Plugin.TvStation.csproj"
$version = "1.1.0.0"
$pluginDir = Join-Path $root "dev\config\plugins\TvStation_$version"

Write-Host "Building $Configuration..." -ForegroundColor Cyan
dotnet build $proj -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$dll = Join-Path $root "src\Jellyfin.Plugin.TvStation\bin\$Configuration\net10.0\Jellyfin.Plugin.TvStation.dll"
New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item $dll $pluginDir -Force
Write-Host "Installed to $pluginDir" -ForegroundColor Green

if (-not $NoRestart) {
    Write-Host "Restarting container tvstation-jf..." -ForegroundColor Cyan
    docker restart tvstation-jf | Out-Null
    Start-Sleep -Seconds 6
    docker logs --since 30s tvstation-jf 2>&1 | Select-String -Pattern "TvStation|Loaded plugin|plugin" | Select-Object -Last 15
}
