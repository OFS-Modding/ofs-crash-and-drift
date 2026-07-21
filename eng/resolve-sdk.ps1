param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root '.packages/OFS.Sdk.0.2.4.nupkg'
$expected = 'b4ff664d61fd219cc347eb5d1a64197b46da71814fe5b286f4e61bb0f573a70e'
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
if (-not (Test-Path -LiteralPath $destination)) {
    Invoke-WebRequest 'https://github.com/OFS-Modding/ofs-sdk/releases/download/v0.2.4/OFS.Sdk.0.2.4.nupkg' `
        -OutFile $destination
}
if ((Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'OFS SDK package digest mismatch.'
}
