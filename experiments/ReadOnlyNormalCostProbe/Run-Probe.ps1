[CmdletBinding()]
param(
    [ValidateSet('all', 'history', 'ancestors', 'schema', 'catalog')]
    [string] $Axis = 'all',
    [ValidateRange(-1, 100000)]
    [int] $Scale = -1,
    [string] $OutputDirectory,
    [string] $StorageSourceRoot,
    [string] $FixtureTempDirectory,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$testProject = Join-Path $repositoryRoot 'tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj'
if ([string]::IsNullOrWhiteSpace($StorageSourceRoot)) {
    $StorageSourceRoot = Join-Path $repositoryRoot '../atelia-storage'
}
$StorageSourceRoot = (Resolve-Path -LiteralPath $StorageSourceRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $StorageSourceRoot 'src/Rbf/Rbf.csproj') -PathType Leaf)) {
    throw "StorageSourceRoot is not an atelia-storage source checkout: '$StorageSourceRoot'."
}
if ([string]::IsNullOrWhiteSpace($FixtureTempDirectory)) {
    $FixtureTempDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp/db085-normal-20260929'
}
$FixtureTempDirectory = [IO.Path]::GetFullPath($FixtureTempDirectory)
[void](New-Item -ItemType Directory -Path $FixtureTempDirectory -Force)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "obj/$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Path $OutputDirectory -Force)
$names = @(
    'DURABLEGRAPH_DB085_MEASURE', 'DURABLEGRAPH_DB085_MEASURE_OUTPUT',
    'DURABLEGRAPH_DB085_MEASURE_AXIS', 'DURABLEGRAPH_DB085_MEASURE_SCALE', 'TEMP', 'TMP'
)
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $repositoryRoot
try {
    [Environment]::SetEnvironmentVariable('TEMP', $FixtureTempDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('TMP', $FixtureTempDirectory, 'Process')
    $properties = @('-c', 'Release', '-m:1', '-nr:false', '-p:ShouldUnsetParentConfigurationAndPlatform=false', '-p:UseStorageSources=true', "-p:StorageSourceRoot=$StorageSourceRoot")
    if (-not $NoBuild) {
        & dotnet build DurableGraph.slnx @properties
        if ($LASTEXITCODE -ne 0) { throw 'DurableGraph build failed.' }
    }
    $axes = if ($Axis -eq 'all') { @('history', 'ancestors', 'schema', 'catalog') } else { @($Axis) }
    [Environment]::SetEnvironmentVariable('DURABLEGRAPH_DB085_MEASURE', '1', 'Process')
    if ($Scale -ge 0) { [Environment]::SetEnvironmentVariable('DURABLEGRAPH_DB085_MEASURE_SCALE', "$Scale", 'Process') }
    else { Remove-Item -LiteralPath Env:DURABLEGRAPH_DB085_MEASURE_SCALE -ErrorAction SilentlyContinue }
    foreach ($selectedAxis in $axes) {
        $suffix = if ($Scale -ge 0) { "-$Scale" } else { '' }
        $destination = Join-Path $OutputDirectory "$selectedAxis$suffix.jsonl"
        [Environment]::SetEnvironmentVariable('DURABLEGRAPH_DB085_MEASURE_AXIS', $selectedAxis, 'Process')
        [Environment]::SetEnvironmentVariable('DURABLEGRAPH_DB085_MEASURE_OUTPUT', $destination, 'Process')
        # One test host per axis (or explicitly requested axis/scale). Cells inside that
        # invocation share process/JIT state. A fresh process does not cool the OS cache.
        & dotnet test $testProject @properties --no-build --no-restore --filter 'FullyQualifiedName=Atelia.DurableGraph.Persistence.Tests.DB085ReadOnlyCostMeasurements.Measure_read_only_open_query_and_first_graph_read' --logger 'console;verbosity=minimal'
        if ($LASTEXITCODE -ne 0) { throw "DB085 measurement failed for '$selectedAxis'." }
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Missing measurement output '$destination'." }
        Write-Host "DB085 measurements: $destination"
    }
}
finally {
    foreach ($name in $names) {
        if ($null -eq $previous[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    }
    Pop-Location
}
