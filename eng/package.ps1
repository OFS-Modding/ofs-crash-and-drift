param([Parameter(Mandatory)][string]$ManagerPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1')
$manifest = Get-Content (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
$output = Join-Path $root "artifacts/$($manifest.id)-$($manifest.version).ofmod"
& ([IO.Path]::GetFullPath($ManagerPath)) mod pack (Join-Path $root 'artifacts/dist') $output
if ($LASTEXITCODE -ne 0) { throw 'Crash & Drift packaging failed.' }
[ordered]@{ path=$output; bytes=(Get-Item $output).Length; sha256=(Get-FileHash $output -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json
