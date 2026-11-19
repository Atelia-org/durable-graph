param(
    [Parameter(Mandatory)][string]$StorageSourceRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_TieredCompilation = '0'
$storagePath = (Resolve-Path -LiteralPath $StorageSourceRoot).Path
$project = Join-Path $PSScriptRoot 'Consumer/Consumer.csproj'
if (-not $SkipBuild) {
    dotnet build $project -c $Configuration -p:UseStorageSources=true "-p:StorageSourceRoot=$storagePath" -m:1 -nr:false --nologo
    if ($LASTEXITCODE) { throw 'Consumer build failed' }
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh output directory for independent stores and receipts.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$exe = Join-Path $PSScriptRoot "Consumer/bin/$Configuration/net10.0/Atelia.Consumer.dll"
foreach ($stage in 'seed','resume','verify','missing','bench-1','bench-2','bench-3','diagnose') {
    $store = if ($stage.StartsWith('bench-') -or $stage -eq 'diagnose') { Join-Path $root $stage } else { Join-Path $root 'functional' }
    dotnet $exe $stage $store (Join-Path $root "$stage.json")
    if ($LASTEXITCODE) { throw "Probe failed at $stage" }
}
python (Join-Path $PSScriptRoot 'summarize.py') $root
if ($LASTEXITCODE) { throw 'Results did not pass equivalence checks.' }
