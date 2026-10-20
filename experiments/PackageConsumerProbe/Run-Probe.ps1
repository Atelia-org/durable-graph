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

function Get-ProbePackageEntryHash {
    param([Parameter(Mandatory)][string] $Package, [Parameter(Mandatory)][string] $Entry)
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $entriesFound = @($zip.Entries | Where-Object FullName -CEQ $Entry)
        if ($entriesFound.Count -ne 1) { throw "Expected exactly one '$Entry' in '$Package'." }
        $stream = $entriesFound[0].Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [Convert]::ToHexString($sha.ComputeHash($stream)) }
        finally { $sha.Dispose(); $stream.Dispose() }
    } finally { $zip.Dispose() }
}

# Kept narrow so a local tamper witness can exercise the same byte verifier without
# invoking restore/build or modifying either the feed or the global NuGet cache.
function Assert-ProbePackageFile {
    param([Parameter(Mandatory)][string] $Package, [Parameter(Mandatory)][string] $Entry,
        [Parameter(Mandatory)][string] $File)
    $expected = Get-ProbePackageEntryHash -Package $Package -Entry $Entry
    $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash
    if ($actual -cne $expected) { throw "Package entry '$Entry' differs from actual file '$File'." }
    return [ordered]@{ File = $File; Package = $Package; Entry = $Entry; Sha256 = $actual }
}

function Invoke-ProbeBuildEvidence {
    param([Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][ValidateSet('Publish', 'Verify')][string] $Mode,
        [Parameter(Mandatory)][string] $Log)
    $text = (& dotnet @Arguments 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    $text | Set-Content -LiteralPath $Log -Encoding utf8NoBOM
    Write-Host $text
    if ($exitCode -ne 0) { throw "dotnet build $Mode failed with exit code $exitCode; see '$Log'." }
    $lines = $text.Replace('\', '/') -split '\r?\n'
    $generator = (Join-Path $runtimeCacheDirectory 'analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll').Replace('\', '/')
    $tool = (Join-Path $runtimeCacheDirectory 'tools/net10.0/Atelia.DurableGraph.Build.dll').Replace('\', '/')
    $toolViaTargets = $tool.Replace('/tools/', '/build/../tools/')
    $compilerLines = @($lines | Where-Object {
        $_ -match '(?i)csc\.(dll|exe)\b' -and $_ -match '(?i)/analyzer:' -and
        $_.Contains($generator, [StringComparison]::OrdinalIgnoreCase)
    })
    $toolLines = @($lines | Where-Object {
        ($_.Contains($tool, [StringComparison]::OrdinalIgnoreCase) -or
         $_.Contains($toolViaTargets, [StringComparison]::OrdinalIgnoreCase)) -and
        $_ -match ('(?i)\s' + $Mode + '\s+--manifest')
    })
    if ($compilerLines.Count -eq 0 -or $toolLines.Count -eq 0) {
        throw "Build $Mode did not show the actual cached Generator and Build invocation; see '$Log'."
    }
    $evidence.BuildLogs += [ordered]@{ Mode = $Mode; File = $Log; Sha256 = (Get-FileHash -LiteralPath $Log).Hash }
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
$evidence = [ordered]@{ Feed = @(); ResolvedAtelia = @(); Files = @(); BuildLogs = @(); Passed = $false }

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
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $feedIndex = @{}
    foreach ($file in $packages) {
        $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
        try {
            $specs = @($zip.Entries | Where-Object FullName -like '*.nuspec')
            if ($specs.Count -ne 1) { throw "Expected one nuspec in '$($file.Name)'." }
            $reader = [IO.StreamReader]::new($specs[0].Open())
            try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $id = [string]$spec.package.metadata.id
            $resolvedVersion = [string]$spec.package.metadata.version
            if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($resolvedVersion)) {
                throw "Missing nuspec identity in '$($file.Name)'."
            }
            $identity = "$id/$resolvedVersion"
            if ($feedIndex.ContainsKey($identity)) { throw "Duplicate feed package identity: $identity" }
            $entry = [ordered]@{ Id = $id; Version = $resolvedVersion; File = $file.FullName
                Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
            $feedIndex[$identity] = $entry
            $evidence.Feed += $entry
        } finally { $zip.Dispose() }
    }
    if ($packages.Count -ne 10) { throw "Expected the complete ten-package feed, found $($packages.Count)." }
    foreach ($requiredId in @('Atelia.DurableGraph', 'Atelia.DurableGraph.Serialization',
        'Atelia.DurableGraph.Storage', 'Atelia.DurableGraph.Persistence')) {
        $versions = @($feedIndex.Values | Where-Object { $_.Id -ieq $requiredId })
        if ($versions.Count -ne 1 -or $versions[0].Version -ine $packageVersion) {
            throw "Feed must contain exactly one '$requiredId' package at requested G version '$packageVersion'."
        }
    }
    $runtimeIdentity = "Atelia.DurableGraph/$packageVersion"
    if (!$feedIndex.ContainsKey($runtimeIdentity)) { throw "Feed does not contain '$runtimeIdentity'." }
    $package = Get-Item -LiteralPath $feedIndex[$runtimeIdentity].File
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
    $evidence['PackageReadmeSha256'] = Get-ProbePackageEntryHash -Package $package.FullName -Entry 'README.md'
    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        # Only a feed produced by this run claims this checkout's PACKAGE.md bytes.
        # An explicitly supplied valid feed may come from a different source revision.
        $sourceReadme = Join-Path $repositoryRoot 'src/DurableGraph/PACKAGE.md'
        if ((Get-FileHash -LiteralPath $sourceReadme).Hash -cne $evidence.PackageReadmeSha256) {
            throw 'The locally produced Runtime package README differs from src/DurableGraph/PACKAGE.md.'
        }
        $evidence['SourceReadme'] = $sourceReadme
    }

    Invoke-DotNet @(
        "restore", $consumerProject,
        "--source", $feed,
        "--packages", $packageCache,
        "-p:DurableGraphPackageVersion=$packageVersion"
    )

    $assetsFile = Join-Path $consumerRoot 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Values | Where-Object type -ne 'package').Count -ne 0) {
        throw 'Consumer assets must contain packages only.'
    }
    $cacheRoot = [IO.Path]::GetFullPath($packageCache).TrimEnd('\', '/')
    $folders = @($assets.packageFolders.Keys)
    if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ine $cacheRoot) {
        throw 'Consumer assets must use only this run''s fresh package cache.'
    }
    $resolvedAtelia = @{}
    foreach ($identity in $assets.libraries.Keys) {
        $id, $resolvedVersion = $identity.Split('/')
        if (!$id.StartsWith('Atelia.', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (!$feedIndex.ContainsKey($identity)) { throw "Restored Atelia identity is not in the selected feed: $identity" }
        $library = $assets.libraries[$identity]
        $directory = [IO.Path]::GetFullPath((Join-Path $cacheRoot $library.path))
        if (!$directory.StartsWith($cacheRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Restored package escapes the task cache: $identity"
        }
        $cachedPackage = Join-Path $directory "$($id.ToLowerInvariant()).$($resolvedVersion.ToLowerInvariant()).nupkg"
        if ((Get-FileHash -LiteralPath $cachedPackage).Hash -cne $feedIndex[$identity].Sha256) {
            throw "Cached package differs from selected feed: $identity"
        }
        $resolvedAtelia[$id] = [ordered]@{ Id = $id; Version = $resolvedVersion; Directory = $directory
            Package = $feedIndex[$identity].File; Sha256 = $feedIndex[$identity].Sha256 }
        $evidence.ResolvedAtelia += $resolvedAtelia[$id]
    }
    foreach ($requiredId in @('Atelia.DurableGraph', 'Atelia.DurableGraph.Serialization')) {
        if (!$resolvedAtelia.ContainsKey($requiredId)) { throw "Required Runtime closure package was not restored: $requiredId" }
    }
    if ($resolvedAtelia['Atelia.DurableGraph'].Version -ine $packageVersion) { throw 'Consumer restored the wrong Runtime version.' }
    $runtimeCacheDirectory = $resolvedAtelia['Atelia.DurableGraph'].Directory
    foreach ($requiredEntry in $requiredEntries) {
        $evidence.Files += Assert-ProbePackageFile -Package $package.FullName -Entry $requiredEntry -File (Join-Path $runtimeCacheDirectory $requiredEntry)
    }
    $evidence['AssetsFile'] = $assetsFile
    $evidence['AssetsSha256'] = (Get-FileHash -LiteralPath $assetsFile).Hash
    $evidence['ResolvedPackages'] = @($assets.libraries.Keys | Sort-Object)

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
    Invoke-ProbeBuildEvidence -Mode Publish -Log (Join-Path $workRoot 'build-publish.log') -Arguments @(
        "build", $consumerProject, "--no-restore",
        '-v:normal', '-p:UseSharedCompilation=false', '-p:DurableGraphSchemaHistoryMode=Publish',
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
    Invoke-ProbeBuildEvidence -Mode Verify -Log (Join-Path $workRoot 'build-verify.log') -Arguments @(
        "build", $consumerProject, "--no-restore",
        '-t:Rebuild', '-v:normal', '-p:UseSharedCompilation=false', '-p:DurableGraphSchemaHistoryMode=Verify',
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

    # The Runtime-only consumer does not restore all ten feed packages. Compare the
    # actual runtime assets of its resolved Atelia closure, rather than a feed count.
    $targets = @($assets.targets.Values)
    if ($targets.Count -ne 1) { throw 'This non-RID consumer must have exactly one assets target.' }
    $expectedDlls = @{}
    foreach ($resolved in $resolvedAtelia.Values) {
        $identity = "$($resolved.Id)/$($resolved.Version)"
        $runtimeAssets = $targets[0][$identity].runtime
        if ($null -eq $runtimeAssets) { continue }
        foreach ($entry in $runtimeAssets.Keys) {
            if (![IO.Path]::GetFileName($entry).EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) { continue }
            $name = [IO.Path]::GetFileName($entry)
            if ($expectedDlls.ContainsKey($name)) { throw "Duplicate Atelia output asset: $name" }
            $expectedDlls[$name] = [ordered]@{ Package = $resolved.Package; Entry = $entry }
            $evidence.Files += Assert-ProbePackageFile -Package $resolved.Package -Entry $entry -File (Join-Path $resolved.Directory $entry)
        }
    }
    $outputRoot = Split-Path -Parent $consumerAssembly
    $actualDlls = @(Get-ChildItem -LiteralPath $outputRoot -Filter 'Atelia.*.dll' -File |
        Where-Object Name -ne 'Atelia.Consumer.dll')
    if ($actualDlls.Count -ne $expectedDlls.Count) { throw 'Output Atelia DLL set differs from the actual restored runtime closure.' }
    foreach ($dll in $actualDlls) {
        if (!$expectedDlls.ContainsKey($dll.Name)) { throw "Unexpected output assembly: $($dll.Name)" }
        $expected = $expectedDlls[$dll.Name]
        $evidence.Files += Assert-ProbePackageFile -Package $expected.Package -Entry $expected.Entry -File $dll.FullName
    }
    foreach ($requiredEntry in $requiredEntries) {
        $null = Assert-ProbePackageFile -Package $package.FullName -Entry $requiredEntry -File (Join-Path $runtimeCacheDirectory $requiredEntry)
    }

    $evidence.Passed = $true

    Write-Host "Package consumer probe passed. Artifacts: $workRoot"
}
finally {
    $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $workRoot 'package-evidence.json') -Encoding utf8NoBOM
    Pop-Location
}
