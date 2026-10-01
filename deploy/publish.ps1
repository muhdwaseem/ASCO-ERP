# Builds a self-contained hosting package in deploy\out:
#   - the React app (npm run build) copied into wwwroot, served by the API from the same origin
#   - the ASCO API (dotnet publish, Release) referencing C-ERP's core from $env:CErpSrc
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File deploy\publish.ps1
# Add -Runtime linux-x64 to build for a Linux server instead of this Windows PC.
param([string]$Runtime = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot "out"

Push-Location $root
try {
    Write-Host "1/3  Building the front end..." -ForegroundColor Cyan
    if (-not (Test-Path node_modules)) { npm ci }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Front-end build failed." }

    Write-Host "2/3  Publishing the API..." -ForegroundColor Cyan
    if (Test-Path $out) { Get-ChildItem $out -Exclude "*.db*", "keys" | Remove-Item -Recurse -Force }
    $args = @("publish", "server/Asco.Api/Asco.Api.csproj", "-c", "Release", "-o", $out, "--artifacts-path", "artifacts", "-nologo", "-v", "q")
    if ($Runtime) { $args += @("-r", $Runtime, "--self-contained", "true") }
    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "API publish failed." }

    Write-Host "3/3  Copying the front end into wwwroot..." -ForegroundColor Cyan
    $www = Join-Path $out "wwwroot"
    if (Test-Path $www) { Remove-Item $www -Recurse -Force }
    Copy-Item dist $www -Recurse
    Copy-Item (Join-Path $PSScriptRoot "run-hosted.cmd") $out -Force
    Write-Host "Done: $out  - start it with run-hosted.cmd" -ForegroundColor Green
}
finally { Pop-Location }
