[CmdletBinding()]
param(
    [string] $PackageSource,
    [string] $Version
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw "Supply both -PackageSource and -Version, or neither for a self-contained run."
}

. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$consumerProject = Join-Path $PSScriptRoot "HistoryCapabilityConsumer/HistoryCapabilityConsumer.csproj"
$runId = "history-capability-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/$runId"
$packageCache = Join-Path $workRoot "packages"
$history = Join-Path $workRoot "history"
$database = Join-Path $workRoot "database"
$intermediate = (Join-Path $workRoot "consumer-obj") + [IO.Path]::DirectorySeparatorChar
$output = (Join-Path $workRoot "consumer-bin") + [IO.Path]::DirectorySeparatorChar
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
        $PackageSource = Join-Path $workRoot "feed"
        New-Item -ItemType Directory -Path $PackageSource | Out-Null
        $Version = "0.0.0-history-e2e.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$PID"
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version
        $packages = @(Get-ChildItem -LiteralPath $PackageSource -Filter *.nupkg -File)
        if ($packages.Count -ne 10) { throw "Expected 10 dependency packages, found $($packages.Count)." }
    }

    $properties = @(
        "-p:DurableGraphPackageVersion=$Version",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:BaseIntermediateOutputPath=$intermediate",
        "-p:BaseOutputPath=$output"
    )
    Invoke-DotNet (@("restore", $consumerProject, "--source", $PackageSource, "--packages", $packageCache) + $properties)
    $consumerAssembly = Join-Path $output "Debug/net10.0/Atelia.HistoryCapabilityConsumer.dll"
    $oldHistoryHashes = @{}
    $stages = @(
        @{ Number = 1; ReadersOnly = "false"; Count = 2;
           Expected = "HistorySeed:True:LegacyContentChange:True:LegacyCycle:True" },
        @{ Number = 2; ReadersOnly = "true"; Count = 4;
           Expected = "ExactReadersWithoutUpgrade:True:EditableLoadRequiresUpgrade:True" },
        @{ Number = 2; ReadersOnly = "false"; Count = 4;
           Expected = "AllSourceUpgraded:True:AbstractShellNotAllocated:True:BadOrphanRejected:True:LegacyRemoved:True:HistoricalRevisionPreserved:True" },
        @{ Number = 3; ReadersOnly = "false"; Count = 4;
           Expected = "DeletedShellNewRevision:True:DeletedShellOldRevisionRejected:True:HistoryFilesAreNotReaders:True" }
    )
    foreach ($stage in $stages) {
        $stageProperties = $properties + @("-p:HistoryVersion=$($stage.Number)", "-p:ReadersOnly=$($stage.ReadersOnly)")
        Invoke-DotNet (@("clean", $consumerProject) + $stageProperties)
        Invoke-DotNet (@("build", $consumerProject, "--no-restore") + $stageProperties)
        $historyFiles = @(Get-ChildItem -LiteralPath $history -Filter *.dgschema -File)
        if ($historyFiles.Count -ne $stage.Count) {
            throw "Stage $($stage.Number) expected $($stage.Count) Schema history files; found $($historyFiles.Count)."
        }
        foreach ($file in $historyFiles) {
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            if ($oldHistoryHashes.ContainsKey($file.Name) -and $oldHistoryHashes[$file.Name] -ne $hash) {
                throw "A later consumer rewrote immutable history '$($file.Name)'."
            }
            $oldHistoryHashes[$file.Name] = $hash
        }
        $actual = (& dotnet $consumerAssembly $database | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -ne $stage.Expected) {
            throw "History capability stage $($stage.Number) failed; output was '$actual'."
        }
        Write-Host $actual
    }
    Write-Host "History capability package consumer probe passed. Artifacts: $workRoot"
}
finally {
    Pop-Location
}
