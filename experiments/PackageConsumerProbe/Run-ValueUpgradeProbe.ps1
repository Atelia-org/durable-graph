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
$consumerProject = Join-Path $PSScriptRoot "ValueUpgradeConsumer/ValueUpgradeConsumer.csproj"
$runId = "value-upgrade-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
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
        $Version = "0.0.0-value-e2e.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$PID"
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version
        $packages = @(Get-ChildItem -LiteralPath $PackageSource -Filter *.nupkg -File)
        if ($packages.Count -ne 10) { throw "Expected 9 dependency packages, found $($packages.Count)." }
    }

    $properties = @(
        "-p:DurableGraphPackageVersion=$Version",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:BaseIntermediateOutputPath=$intermediate",
        "-p:BaseOutputPath=$output"
    )
    Invoke-DotNet (@("restore", $consumerProject, "--source", $PackageSource, "--packages", $packageCache) + $properties)
    $consumerAssembly = Join-Path $output "Debug/net10.0/Atelia.ValueUpgradeConsumer.dll"
    $oldHistoryHashes = @{}
    $stages = @(
        @{ Number = 1; Count = 5; OmitPoint = $false;
           Expected = "ValueSeed:True:OwnerDelta:True:NestedInline:True:ExactHistory:True" },
        @{ Number = 2; Count = 10; OmitPoint = $true;
           Expected = "MissingValueRule:True:ExactDecodeAvailable:True:NoInvalidOwnerCallback:True" },
        @{ Number = 2; Count = 10; OmitPoint = $false;
           Expected = "ValueUpgrade:True:OpenOwnerAndPair:True:ReusedLeaf:True:ScopedContexts:True:ForcedBaseThenDelta:True" },
        @{ Number = 3; Count = 12; OmitPoint = $false;
           Expected = "ValueHistory:True:DeletedInlineDomain:True:RetainedValueRule:True:AdjacentContexts:True:StableResave:True" }
    )
    foreach ($stage in $stages) {
        $stageProperties = $properties + @(
            "-p:HistoryVersion=$($stage.Number)",
            "-p:OmitPointRule=$($stage.OmitPoint.ToString().ToLowerInvariant())"
        )
        Invoke-DotNet (@("clean", $consumerProject) + $stageProperties)
        Invoke-DotNet (@("build", $consumerProject, "--no-restore") + $stageProperties)
        $historyFiles = @(Get-ChildItem -LiteralPath $history -Filter *.dgschema -File)
        if ($historyFiles.Count -ne $stage.Count) {
            throw "Stage $($stage.Number) expected $($stage.Count) Schema history files; found $($historyFiles.Count)."
        }
        foreach ($oldName in $oldHistoryHashes.Keys) {
            if (-not (Test-Path -LiteralPath (Join-Path $history $oldName) -PathType Leaf)) {
                throw "A later consumer deleted immutable history '$oldName'."
            }
        }
        foreach ($file in $historyFiles) {
            $content = Get-Content -LiteralPath $file.FullName -Raw
            if (-not $oldHistoryHashes.ContainsKey($file.Name) -and -not $content.StartsWith("// durable-graph-schema-history:9`n", [StringComparison]::Ordinal)) {
                throw "Newly published Schema history is not canonical format v9."
            }
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            if ($oldHistoryHashes.ContainsKey($file.Name) -and $oldHistoryHashes[$file.Name] -ne $hash) {
                throw "A later consumer rewrote immutable history '$($file.Name)'."
            }
            $oldHistoryHashes[$file.Name] = $hash
        }
        $databaseHashes = @{}
        if ($stage.OmitPoint) {
            foreach ($file in Get-ChildItem -LiteralPath $database -File -Recurse) {
                $databaseHashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            }
        }
        $actual = (& dotnet $consumerAssembly $database | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -ne $stage.Expected) {
            throw "Value Upgrade stage $($stage.Number), OmitPoint=$($stage.OmitPoint), failed; output was '$actual'."
        }
        if ($stage.OmitPoint) {
            $after = @(Get-ChildItem -LiteralPath $database -File -Recurse)
            if ($after.Count -ne $databaseHashes.Count) { throw "Rejected Load changed the repository file set." }
            foreach ($file in $after) {
                if (-not $databaseHashes.ContainsKey($file.FullName) -or
                    $databaseHashes[$file.FullName] -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
                    throw "Rejected Load changed repository bytes in '$($file.FullName)'."
                }
            }
        }
        Write-Host $actual
    }
    Write-Host "Value Upgrade package consumer probe passed. Artifacts: $workRoot"
}
finally {
    Pop-Location
}
