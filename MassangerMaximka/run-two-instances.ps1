# Run two messenger instances for local testing
# Usage: .\run-two-instances.ps1

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir
$projectPath = Join-Path $scriptDir "MassangerMaximka\MassangerMaximka.csproj"
$exePath = Join-Path $scriptDir "MassangerMaximka\bin\Debug\net9.0-windows10.0.19041.0\win10-x64\MassangerMaximka.exe"

# Kill existing instances to avoid port conflicts
Get-Process -Name "MassangerMaximka" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# Build once
Write-Host "Building..." -ForegroundColor Cyan
dotnet build $projectPath -f net9.0-windows10.0.19041.0 -v q
if ($LASTEXITCODE -ne 0) { exit 1 }
if (-not (Test-Path $exePath)) {
    Write-Host "Build succeeded, but exe not found: $exePath" -ForegroundColor Red
    exit 1
}

Write-Host "Starting Instance 1 (port 45680)..." -ForegroundColor Green
Start-Process "cmd.exe" -WorkingDirectory $scriptDir -ArgumentList "/k", "set HEX_TCP_PORT=45680 && `"$exePath`""

Start-Sleep -Seconds 3

Write-Host "Starting Instance 2 (port 45681)..." -ForegroundColor Green
Start-Process "cmd.exe" -WorkingDirectory $scriptDir -ArgumentList "/k", "set HEX_TCP_PORT=45681 && `"$exePath`""

Write-Host "`nBoth instances started." -ForegroundColor Yellow
Write-Host "Auto-discovery should find peers automatically in a few seconds." -ForegroundColor Yellow
Write-Host "Fallback connect: Instance 2 -> 127.0.0.1:45680, Instance 1 -> 127.0.0.1:45681" -ForegroundColor Yellow
