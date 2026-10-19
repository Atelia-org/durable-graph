[CmdletBinding()]
param([string] $PackageSource, [string] $Version)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw 'Supply both -PackageSource and -Version, or neither for a self-contained run.'
}

. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')

function Invoke-DotNetExpectFailure {
    param(
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $ExpectedText
    )

    $output = (& dotnet @Arguments 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    Write-Host $output

    if ($exitCode -eq 0) {
        throw "dotnet $($Arguments -join ' ') unexpectedly succeeded."
    }

    if (-not $output.Contains($ExpectedText, [StringComparison]::Ordinal)) {
        throw "Expected failed command output to contain '$ExpectedText'."
    }
}

function Invoke-ConsumerClean {
    param(
        [Parameter(Mandatory)][string] $Project,
        [Parameter(Mandatory)][string] $PackageVersion,
        [Parameter(Mandatory)][string] $HistoryDirectory,
        [Parameter(Mandatory)][int] $ProbeVersion
    )

    Invoke-DotNet @(
        "clean", $Project,
        "-p:DurableGraphPackageVersion=$PackageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$HistoryDirectory",
        "-p:ProbeVersion=$ProbeVersion"
    )
}

function Get-HistoryFiles {
    param([Parameter(Mandatory)][string] $HistoryDirectory)

    if (-not (Test-Path -LiteralPath $HistoryDirectory)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $HistoryDirectory -Filter *.dgschema -File)
}

function Assert-HistoryCount {
    param(
        [Parameter(Mandatory)][string] $HistoryDirectory,
        [Parameter(Mandatory)][int] $Expected
    )

    $actual = @(Get-HistoryFiles $HistoryDirectory).Count

    if ($actual -ne $Expected) {
        throw "Expected $Expected Schema-history file(s), found $actual."
    }
}

$probeRoot = $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $probeRoot "../..")).Path
$consumerSource = Join-Path $probeRoot "Consumer"
$runId = "run-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $probeRoot "obj/$runId"
$consumerRoot = Join-Path $workRoot "Consumer"
$consumerProject = Join-Path $consumerRoot "Consumer.csproj"
$feed = if ([string]::IsNullOrWhiteSpace($PackageSource)) { Join-Path $workRoot "feed" } else { (Resolve-Path -LiteralPath $PackageSource).Path }
$packageCache = Join-Path $workRoot "packages"
$history = Join-Path $workRoot "history"
$packageVersion = if ([string]::IsNullOrWhiteSpace($Version)) { "0.0.0-e2e.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$PID" } else { $Version }
$consumerProjectText = Get-Content -LiteralPath (Join-Path $consumerSource "Consumer.csproj") -Raw

foreach ($forbiddenConsumerWiring in @(
    "<Import",
    "<ProjectReference",
    "<Analyzer",
    "<AdditionalFiles"
)) {
    if ($consumerProjectText.Contains($forbiddenConsumerWiring, [StringComparison]::Ordinal)) {
        throw "Consumer project contains forbidden manual wiring '$forbiddenConsumerWiring'."
    }
}

New-Item -ItemType Directory -Path $consumerRoot, $packageCache | Out-Null
# Each run gets only the fixture inputs. Old generator names beneath the source
# project's obj survive dotnet clean and must never enter this run's manifest search.
foreach ($file in @('Consumer.csproj', 'Program.cs', 'Domain.None.cs', 'Domain.V1.cs', 'Domain.V2.cs',
    'Domain.Binary.cs', 'Domain.Delta.cs', 'Domain.Preparation.cs', 'Domain.ReferenceCapture.cs', 'Domain.StringDecoding.cs')) {
    Copy-Item -LiteralPath (Join-Path $consumerSource $file) -Destination (Join-Path $consumerRoot $file)
}
# Stop parent repo MSBuild discovery: this is an ordinary package consumer, not a
# source-tree build. Keep the existing assembly name used by the runtime assertion.
'<Project><PropertyGroup><AssemblyName>Atelia.Consumer</AssemblyName></PropertyGroup></Project>' |
    Set-Content -LiteralPath (Join-Path $consumerRoot 'Directory.Build.props') -Encoding utf8NoBOM
'<Project />' | Set-Content -LiteralPath (Join-Path $consumerRoot 'Directory.Build.targets') -Encoding utf8NoBOM

Push-Location $repositoryRoot

try {
    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $feed -Version $packageVersion
    }
    $packages = @(Get-ChildItem -LiteralPath $feed -Filter *.nupkg -File -ErrorAction Stop)
    if ($packages.Count -ne 10) { throw "Expected the complete ten-package feed, found $($packages.Count)." }

    $package = Get-Item -LiteralPath (Join-Path $feed "Atelia.DurableGraph.$packageVersion.nupkg")
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)

    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        $requiredEntries = @(
            "lib/net10.0/Atelia.DurableGraph.dll",
            "analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll",
            "build/Atelia.DurableGraph.props",
            "build/Atelia.DurableGraph.targets",
            "README.md",
            "tools/net10.0/Atelia.DurableGraph.Build.dll",
            "tools/net10.0/Atelia.DurableGraph.Build.deps.json",
            "tools/net10.0/Atelia.DurableGraph.Build.runtimeconfig.json"
        )

        foreach ($requiredEntry in $requiredEntries) {
            if ($entries -notcontains $requiredEntry) {
                throw "Package is missing '$requiredEntry'."
            }
        }

        if ($entries -contains "tools/net10.0/Atelia.DurableGraph.Build.exe") {
            throw "Package contains a platform-specific build-tool apphost."
        }
    }
    finally {
        $archive.Dispose()
    }

    Invoke-DotNet @(
        "restore", $consumerProject,
        "--source", $feed,
        "--packages", $packageCache,
        "-p:DurableGraphPackageVersion=$packageVersion"
    )

    Invoke-DotNetExpectFailure -Arguments @(
        "build", $consumerProject, "--no-restore",
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:ProbeVersion=2"
    ) -ExpectedText "DG0014"
    Assert-HistoryCount $history 0

    Invoke-ConsumerClean $consumerProject $packageVersion $history 1
    New-Item -ItemType Directory -Path $history -Force | Out-Null
    $legacyHistoryFile = Join-Path $history "legacy.dgsnapshot"
    Set-Content -LiteralPath $legacyHistoryFile -Value "legacy Schema-history fixture" -NoNewline
    Invoke-DotNetExpectFailure -Arguments @(
        "build", $consumerProject, "--no-restore",
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:ProbeVersion=1"
    ) -ExpectedText "DurableGraph Schema-history directory contains legacy .dgsnapshot files; regenerate them as .dgschema because legacy history is not accepted."
    Remove-Item -LiteralPath $legacyHistoryFile
    Assert-HistoryCount $history 0

    Invoke-DotNetExpectFailure -Arguments @(
        "build", $consumerProject, "--no-restore",
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:DurableGraphSnapshotHistoryDirectory=$history",
        "-p:ProbeVersion=1"
    ) -ExpectedText "DurableGraphSnapshotHistoryDirectory has been removed; use DurableGraphSchemaHistoryDirectory and .dgschema files."

    $legacyDefaultDirectory = Join-Path $consumerRoot "DurableGraphSnapshots"
    $legacyDefaultFile = Join-Path $legacyDefaultDirectory "legacy.dgsnapshot"
    New-Item -ItemType Directory -Path $legacyDefaultDirectory | Out-Null
    Set-Content -LiteralPath $legacyDefaultFile -Value "legacy default Schema-history fixture" -NoNewline
    try {
        Invoke-DotNetExpectFailure -Arguments @(
            "build", $consumerProject, "--no-restore",
            "-p:DurableGraphPackageVersion=$packageVersion",
            "-p:DurableGraphSchemaHistoryDirectory=$history",
            "-p:ProbeVersion=1"
        ) -ExpectedText "The legacy default DurableGraphSnapshots directory contains Schema-history files; move regenerated .dgschema history to DurableGraphSchemaHistory."
    }
    finally {
        Remove-Item -LiteralPath $legacyDefaultFile -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $legacyDefaultDirectory -ErrorAction SilentlyContinue
    }

    Invoke-ConsumerClean $consumerProject $packageVersion $history 1
    Invoke-DotNet @(
        "build", $consumerProject, "--no-restore",
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:ProbeVersion=1"
    )
    Assert-HistoryCount $history 1

    Invoke-ConsumerClean $consumerProject $packageVersion $history 2
    Invoke-DotNet @(
        "build", $consumerProject, "--no-restore",
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:ProbeVersion=2"
    )
    Assert-HistoryCount $history 2

    $consumerAssembly = Join-Path $consumerRoot "bin/Debug/net10.0/Atelia.Consumer.dll"
    $consumerOutput = (& dotnet $consumerAssembly | Out-String).Trim()

    if ($LASTEXITCODE -ne 0 -or $consumerOutput -ne "7:True:2") {
        throw "Generated Schema-history exercise failed; output was '$consumerOutput'."
    }

    $hashesBeforeVerify = @{}

    foreach ($file in Get-HistoryFiles $history) {
        $hashesBeforeVerify[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    Invoke-DotNet @(
        "build", $consumerProject, "--no-restore",
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:ProbeVersion=0"
    )
    $generatedRoot = Join-Path $consumerRoot "obj/Debug/net10.0/generated"
    $emptyManifests = @(Get-ChildItem -LiteralPath $generatedRoot -Recurse -File |
        Where-Object Name -eq "DurableGraphSchemaHistoryCandidates.g.cs")

    if ($emptyManifests.Count -ne 1 -or
        (Get-Content -LiteralPath $emptyManifests[0].FullName -Raw) -ne "// durable-graph-schema-history-manifest:9`n") {
        throw "Removing all durable types without cleaning did not replace the old candidate with an empty manifest."
    }

    foreach ($file in Get-HistoryFiles $history) {
        $zeroTypeHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash

        if ($hashesBeforeVerify[$file.Name] -ne $zeroTypeHash) {
            throw "Zero-type publication changed '$($file.Name)'."
        }
    }

    Invoke-ConsumerClean $consumerProject $packageVersion $history 2
    Invoke-DotNet @(
        "build", $consumerProject, "--no-restore",
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:ProbeVersion=2",
        "-p:ContinuousIntegrationBuild=true"
    )

    foreach ($file in Get-HistoryFiles $history) {
        $verifiedHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash

        if ($hashesBeforeVerify[$file.Name] -ne $verifiedHash) {
            throw "CI verification rewrote '$($file.Name)'."
        }
    }

    $versionTwoFiles = @(Get-HistoryFiles $history |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw).Contains("// version:2`n", [StringComparison]::Ordinal) })

    if ($versionTwoFiles.Count -ne 1) {
        throw "Expected exactly one V2 history file, found $($versionTwoFiles.Count)."
    }

    $versionTwoFile = $versionTwoFiles[0]
    $heldVersionTwo = Join-Path $workRoot $versionTwoFile.Name
    Move-Item -LiteralPath $versionTwoFile.FullName -Destination $heldVersionTwo

    try {
        Invoke-ConsumerClean $consumerProject $packageVersion $history 2
        Invoke-DotNetExpectFailure -Arguments @(
            "build", $consumerProject, "--no-restore",
            "-p:DurableGraphPackageVersion=$packageVersion",
            "-p:DurableGraphSchemaHistoryDirectory=$history",
            "-p:ProbeVersion=2",
            "-p:ContinuousIntegrationBuild=true"
        ) -ExpectedText "history is missing schema"
        Assert-HistoryCount $history 1
    }
    finally {
        Move-Item -LiteralPath $heldVersionTwo -Destination $versionTwoFile.FullName
    }

    Invoke-ConsumerClean $consumerProject $packageVersion $history 2
    Invoke-DotNet @(
        "build", $consumerProject, "--no-restore",
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:ProbeVersion=2"
    )

    foreach ($file in Get-HistoryFiles $history) {
        $publishedHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash

        if ($hashesBeforeVerify[$file.Name] -ne $publishedHash) {
            throw "Repeated local publication changed '$($file.Name)'."
        }
    }

    $bodyHistory = Join-Path $workRoot "body-history"
    Invoke-ConsumerClean $consumerProject $packageVersion $bodyHistory 3
    Invoke-DotNet @(
        "build", $consumerProject, "--no-restore",
        "-p:DurableGraphPackageVersion=$packageVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$bodyHistory",
        "-p:ProbeVersion=3"
    )
    Assert-HistoryCount $bodyHistory 7
    $bodyOutput = (& dotnet $consumerAssembly | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $bodyOutput -ne "DurableState:012154:True:ReferenceCapture:True:StringDecoding:True:PreparedDeltaBody:True:PreparedBaseBody:True:CapturePreparation:True:GeneratedReaders:True:GeneratedModel:True:ReadonlyRestore:True") {
        throw "Packaged static binary body failed; output was '$bodyOutput'."
    }

    Write-Host "Package consumer probe passed. Artifacts: $workRoot"
}
finally {
    Pop-Location
}
