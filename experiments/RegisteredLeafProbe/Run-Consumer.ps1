param(
    [Parameter(Mandatory)][string]$StorageSourceRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$storagePath = (Resolve-Path -LiteralPath $StorageSourceRoot).Path
$project = Join-Path $PSScriptRoot 'Consumer/Consumer.csproj'
if (-not $SkipBuild) {
    dotnet build $project -c $Configuration -p:UseStorageSources=true "-p:StorageSourceRoot=$storagePath" -m:1 -nr:false --nologo
    if ($LASTEXITCODE) { throw 'Consumer build failed' }
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh output directory for independent stores and receipts.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$exe = Join-Path $PSScriptRoot "Consumer/bin/$Configuration/net10.0/Atelia.Consumer.dll"
foreach ($stage in 'seed','resume','verify','missing') {
    $store = Join-Path $root 'functional'
    dotnet $exe $stage $store (Join-Path $root "$stage.json")
    if ($LASTEXITCODE) { throw "Probe failed at $stage" }
}
