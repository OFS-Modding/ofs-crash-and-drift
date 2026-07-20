param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root '.packages/OFS.Sdk.0.1.0.nupkg'
$expected = '7be96eb3496dfca4c71e5c3f5338e19e045c85389c768e9e71d0cdb22d2bc554'
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
if (-not (Test-Path -LiteralPath $destination)) {
    Invoke-WebRequest 'https://github.com/OFS-Modding/ofs-sdk/releases/download/v0.1.0/OFS.Sdk.0.1.0.nupkg' `
        -OutFile $destination
}
if ((Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'OFS SDK package digest mismatch.'
}
