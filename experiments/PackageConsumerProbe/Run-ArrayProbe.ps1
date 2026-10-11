[CmdletBinding()]
param([string] $PackageSource, [string] $Version)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw "Supply both -PackageSource and -Version, or neither for a self-contained run."
}
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$consumerProject = Join-Path $PSScriptRoot "ArrayConsumer/ArrayConsumer.csproj"
$runId = "array-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/$runId"
$packageCache = Join-Path $workRoot "packages"
$history = Join-Path $workRoot "history"
$database = Join-Path $workRoot "database"
$intermediate = (Join-Path $workRoot "consumer-obj") + [IO.Path]::DirectorySeparatorChar
$output = (Join-Path $workRoot "consumer-bin") + [IO.Path]::DirectorySeparatorChar
$consumerText = Get-Content -LiteralPath $consumerProject -Raw
foreach ($forbidden in @("<Import", "<ProjectReference", "<Analyzer", "<AdditionalFiles")) {
    if ($consumerText.Contains($forbidden, [StringComparison]::Ordinal)) { throw "Consumer contains forbidden manual wiring '$forbidden'." }
}
New-Item -ItemType Directory -Path $packageCache | Out-Null
Push-Location $repositoryRoot
try {
    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        $PackageSource = Join-Path $workRoot "feed"
        New-Item -ItemType Directory -Path $PackageSource | Out-Null
        $Version = "0.0.0-array-e2e.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$PID"
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version
        if (@(Get-ChildItem -LiteralPath $PackageSource -Filter *.nupkg -File).Count -ne 10) { throw "Expected ten dependency packages." }
    }
    $properties = @(
        "-p:DurableGraphPackageVersion=$Version",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:BaseIntermediateOutputPath=$intermediate",
        "-p:BaseOutputPath=$output"
    )
    Invoke-DotNet (@("restore", $consumerProject, "--source", $PackageSource, "--packages", $packageCache) + $properties)
    $assembly = Join-Path $output "Debug/net10.0/Atelia.ArrayConsumer.dll"
    $hashes = @{}
    foreach ($stage in @(
        @{ Number = 1; Count = 4; Expected = "ArraySeed:True:FourRanks:True:GenericJaggedCycles:True:FrozenDelta:True:RepresentationIds:True:ReorderedRegistration:True" },
        @{ Number = 2; Count = 5; Expected = "ArrayUpgrade:True:SharedOwnerOnce:True:ForcedBaseThenDelta:True:HistoricalExact:True:ColdReopen:True:IndependentRepresentationUpgrade:True:DeltaInheritsRepresentation:True" }
    )) {
        $stageProperties = $properties + "-p:HistoryVersion=$($stage.Number)"
        Invoke-DotNet (@("clean", $consumerProject) + $stageProperties)
        Invoke-DotNet (@("build", $consumerProject, "--no-restore") + $stageProperties)
        # A second build verifies the accepted immutable history through packaged CI mode.
        Invoke-DotNet (@("build", $consumerProject, "--no-restore", "-p:DurableGraphSchemaHistoryMode=Verify") + $stageProperties)
        $files = @(Get-ChildItem -LiteralPath $history -Filter *.dgschema -File)
        if ($files.Count -ne $stage.Count) { throw "Stage $($stage.Number) expected $($stage.Count) history files, found $($files.Count)." }
        foreach ($name in $hashes.Keys) {
            if (-not (Test-Path -LiteralPath (Join-Path $history $name) -PathType Leaf)) { throw "A later build deleted immutable history '$name'." }
        }
        foreach ($file in $files) {
            $text = Get-Content -LiteralPath $file.FullName -Raw
            if (-not $hashes.ContainsKey($file.Name) -and -not $text.StartsWith("// durable-graph-schema-history:9`n", [StringComparison]::Ordinal)) { throw "Expected canonical v9 history." }
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            if ($hashes.ContainsKey($file.Name) -and $hashes[$file.Name] -ne $hash) { throw "A later build rewrote immutable history '$($file.Name)'." }
            $hashes[$file.Name] = $hash
        }
        $actual = (& dotnet $assembly $database | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -ne $stage.Expected) { throw "Array stage $($stage.Number) failed; output was '$actual'." }
        Write-Host $actual
    }
    Write-Host "Array package consumer probe passed. Artifacts: $workRoot"
}
finally { Pop-Location }
