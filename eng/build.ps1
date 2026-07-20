param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'resolve-sdk.ps1')
dotnet build (Join-Path $root 'OFS.CrashAndDriftMod.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Crash & Drift build failed.' }
