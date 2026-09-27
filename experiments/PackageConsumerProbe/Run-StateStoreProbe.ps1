[CmdletBinding()]
param([string] $PackageSource, [string] $Version)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw "Supply both -PackageSource and -Version, or neither for a self-contained run."
}

. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$consumerProject = Join-Path $PSScriptRoot "StateStoreConsumer/StateStoreConsumer.csproj"
$runId = "state-store-run-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/$runId"
$feed = $PackageSource
$packageCache = Join-Path $workRoot "packages"
$history = Join-Path $workRoot "history"
$intermediate = (Join-Path $workRoot "consumer-obj") + [IO.Path]::DirectorySeparatorChar
$output = (Join-Path $workRoot "consumer-bin") + [IO.Path]::DirectorySeparatorChar
$packageVersion = $Version
$consumerText = Get-Content -LiteralPath $consumerProject -Raw
foreach ($forbidden in @("<Import", "<ProjectReference", "<Analyzer", "<AdditionalFiles")) {
    if ($consumerText.Contains($forbidden, [StringComparison]::Ordinal)) {
        throw "Consumer contains forbidden manual wiring '$forbidden'."
    }
}

New-Item -ItemType Directory -Path $packageCache | Out-Null
Push-Location $repositoryRoot
try {
    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        $feed = Join-Path $workRoot "feed"
        $packageVersion = "0.0.0-state-e2e.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$PID"
        New-Item -ItemType Directory -Path $feed | Out-Null
        # The local feed contains the complete product dependency closure. Upstream files are unchanged.
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $feed -Version $packageVersion
        $packages = @(Get-ChildItem -LiteralPath $feed -Filter *.nupkg -File)
        if ($packages.Count -ne 9) { throw "Expected 9 dependency packages, found $($packages.Count)." }
    }

    $consumerProperties = @(
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:BaseIntermediateOutputPath=$intermediate",
        "-p:BaseOutputPath=$output"
    )
    Invoke-DotNet (@("restore", $consumerProject, "--source", $feed, "--packages", $packageCache) + $consumerProperties)
    Invoke-DotNet (@("build", $consumerProject, "--no-restore") + $consumerProperties)
    $historyFiles = @(Get-ChildItem -LiteralPath $history -Filter *.dgschema -File)
    if ($historyFiles.Count -ne 2) { throw "Expected two generated Schema history files, found $($historyFiles.Count)." }

    $consumerAssembly = Join-Path $output "Debug/net10.0/Atelia.StateStoreConsumer.dll"
    $consumerOutput = (& dotnet $consumerAssembly (Join-Path $workRoot "database") | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $consumerOutput -ne "PersistedSchema:True:InitialState:True:RawDelta:True:ColdTypedRead:True:SharedString:True:ConflictBeforeAppend:True:DecodedRevision:True") {
        throw "Packaged StateStore exercise failed; output was '$consumerOutput'."
    }
    Write-Host $consumerOutput

    # Publish V1 from source, then consume that real history while compiling the upgraded model.
    Invoke-DotNet (@("clean", $consumerProject, "-p:RestoreVersion=1") + $consumerProperties)
    Invoke-DotNet (@("build", $consumerProject, "--no-restore", "-p:RestoreVersion=1") + $consumerProperties)
    $historyFiles = @(Get-ChildItem -LiteralPath $history -Filter *.dgschema -File)
    if ($historyFiles.Count -ne 3) { throw "Expected original two plus World V1 history, found $($historyFiles.Count)." }
    $upgradeDatabase = Join-Path $workRoot "upgraded-database"
    $seedOutput = (& dotnet $consumerAssembly $upgradeDatabase | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $seedOutput -ne "HistoricalWorldSeeded:True") {
        throw "Packaged V1 historical seed exercise failed; output was '$seedOutput'."
    }
    Write-Host $seedOutput
    Invoke-DotNet (@("clean", $consumerProject, "-p:RestoreVersion=2") + $consumerProperties)
    Invoke-DotNet (@("build", $consumerProject, "--no-restore", "-p:RestoreVersion=2") + $consumerProperties)
    $historyFiles = @(Get-ChildItem -LiteralPath $history -Filter *.dgschema -File)
    if ($historyFiles.Count -ne 8) { throw "Expected original two plus World V1/V2 and four graph model histories, found $($historyFiles.Count)." }
    $restoreOutput = (& dotnet $consumerAssembly $upgradeDatabase | Out-String).Trim().Replace("`r`n", "`n")
    $expectedRestore = $consumerOutput + "`nHistoricalUpgrade:True:ConstructorFree:True:ReadonlyHydrate:True:ForcedBase:True:UnchangedResave:True:NormalDelta:True:ReopenedWorld:True"
    $expectedRestore += "`nEventHistoryContinuousCommit:True"
    $expectedRestore += "`nInitialGraph:True:SharedDerived:True:ReadonlyCycles:True:ChildOnlyDelta:True:UnreachableCycleRemoved:True:HistoricalGraphPreserved:True"
    if ($LASTEXITCODE -ne 0 -or $restoreOutput -ne $expectedRestore) {
        throw "Packaged upgrade/restore/resave exercise failed; output was '$restoreOutput'."
    }
    Write-Host $restoreOutput
    Write-Host "StateStore package consumer probe passed. Artifacts: $workRoot"
}
finally {
    Pop-Location
}
