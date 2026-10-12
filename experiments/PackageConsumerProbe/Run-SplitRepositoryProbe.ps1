[CmdletBinding()]
param(
    [string]$PackageSource,
    [string]$Version,
    [string]$StoragePackageSource = $env:DG_STORAGE_PACKAGE_SOURCE,
    [string]$WorkDirectory,
    [ValidateRange(10, 600)][int]$ProcessTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw 'Supply both -PackageSource and -Version to reuse a complete feed, or neither to prepare one.'
}
if ([string]::IsNullOrWhiteSpace($StoragePackageSource)) {
    throw 'Supply -StoragePackageSource with the verified local six-package pin feed and manifest.'
}
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$sourceRoot = Join-Path $PSScriptRoot 'SplitRepositoryConsumer'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object { $_.Extension -in '.cs', '.csproj' })
$sourceProject = Join-Path $sourceRoot 'SplitRepositoryConsumer.csproj'
$projectText = Get-Content -LiteralPath $sourceProject -Raw
foreach ($forbidden in @('<Import', '<ProjectReference', '<Analyzer', '<AdditionalFiles')) {
    if ($projectText.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) { throw "Forbidden manual consumer wiring: $forbidden" }
}
[xml]$projectXml = $projectText
foreach ($include in $projectXml.Project.ItemGroup.Compile.Include) {
    if ([IO.Path]::IsPathRooted($include) -or $include -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'The isolated consumer must compile only its copied local source files.'
    }
}
$runId = 'dg-split-repository-package-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + $PID + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
if (!$WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) $runId }
$workRoot = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $workRoot) { throw 'Use a fresh WorkDirectory; prior evidence, history and package bytes are never overwritten.' }
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$packageCache = Join-Path $workRoot 'packages'
$history = Join-Path $workRoot 'history'
$database = Join-Path $workRoot 'database'
$witness = Join-Path $workRoot 'witness.json'
$receiptPath = Join-Path $workRoot 'result.json'
[IO.Directory]::CreateDirectory($packageCache) | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination (Join-Path $workRoot 'global.json')
foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    '<Project />' | Set-Content -LiteralPath (Join-Path $workRoot $name) -Encoding utf8NoBOM
}
$revision = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot record the DurableGraph source revision.' }
$receipt = [ordered]@{
    SchemaVersion = 1
    Scope = 'DB087 P1d internal split Repository package/history consumer; no public-default or process-crash qualification'
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    CompletedUtc = $null
    ControllerProcessId = $PID
    DurableGraphRevision = $revision
    DgVersion = $Version
    StorageVersion = $null
    StorageRevision = $null
    WorkDirectory = $workRoot
    PackageSource = $PackageSource
    PackageCache = $packageCache
    Inputs = @(@($PSCommandPath, (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1'), (Join-Path $repositoryRoot 'eng/Prepare-Storage.ps1'), (Join-Path $repositoryRoot 'eng/StorageDependency.props')) | ForEach-Object {
        [ordered]@{ Path = $_; Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
    Packages = @()
    CompilerInputs = @()
    Stages = @()
    Processes = @()
    Commands = @()
    Passed = $false
    Failure = $null
}
$script:splitCommandIndex = 0

function Save-Receipt {
    $temporary = $receiptPath + '.tmp'
    [IO.File]::WriteAllText($temporary, ($receipt | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $receiptPath, $true)
}

function Invoke-LoggedDotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $script:splitCommandIndex++
    $log = Join-Path $workRoot ('dotnet-{0:D2}-{1}.log' -f $script:splitCommandIndex, $Arguments[0])
    & dotnet @Arguments 2>&1 | Tee-Object -LiteralPath $log | Out-Host
    $code = $LASTEXITCODE
    $receipt.Commands += [ordered]@{ Arguments = $Arguments; ExitCode = $code; Output = $log }
    Save-Receipt
    if ($code -ne 0) { throw "dotnet $($Arguments[0]) failed with exit $code; see $log." }
}

function Get-HistoryImages {
    @((Get-ChildItem -LiteralPath $history -Filter '*.dgschema' -File | Sort-Object Name) | ForEach-Object {
        [ordered]@{ File = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}

function Require-PreservedHistory {
    param($Prior)
    foreach ($image in $Prior) {
        $path = Join-Path $history $image.File
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $image.Sha256) {
            throw "A later stage changed or deleted accepted history $($image.File)."
        }
    }
}

function Get-StoreImages {
    @((Get-ChildItem -LiteralPath $database -Recurse -File | Sort-Object FullName) | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($database, $_.FullName); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}

function Invoke-Consumer {
    param([string]$Lane, $Stage)
    $readOnly = $Lane -in @('upgrade', 'verify')
    $before = if ($readOnly) { @(Get-StoreImages) } else { @() }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $workRoot
    foreach ($argument in @($Stage.Assembly, $Lane, $database, $witness)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    $evidencePath = Join-Path $workRoot ($Lane + '.evidence.json')
    $entry = [ordered]@{ Lane = $Lane; HistoryVersion = $Stage.HistoryVersion; ProcessId = $process.Id; StartedUtc = [DateTime]::UtcNow.ToString('O'); ExitCode = $null; Evidence = $evidencePath; Passed = $false }
    if ($readOnly) { $entry['ReadOnlyBefore'] = $before }
    $receipt.Processes += $entry
    Save-Receipt
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds($ProcessTimeoutSeconds)
        while (-not $process.WaitForExit(100)) {
            if ([DateTime]::UtcNow -ge $deadline) { throw "Consumer $Lane exceeded $ProcessTimeoutSeconds seconds." }
        }
        $output = $outputTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $output.Trim() -cne ('SplitRepositoryConsumer:' + $Lane + ':Passed')) {
            throw "Consumer $Lane failed; inspect its stdout/stderr in $workRoot."
        }
        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        if ($evidence.Protocol -cne 'DG.SplitRepositoryConsumer.v1' -or $evidence.Passed -isnot [bool] -or !$evidence.Passed -or
            $evidence.Lane -cne $Lane -or $evidence.ProcessId -ne $process.Id -or $evidence.TypedBodyAbi -ne 5) {
            throw "Consumer $Lane did not report its actual successful process and current typed ABI."
        }
        foreach ($name in @('RuntimeAssembly', 'StorageAssembly', 'PersistenceAssembly', 'HostAssembly')) {
            $loaded = $evidence.$name
            if (!$loaded.Path.StartsWith($Stage.BinaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                $loaded.Sha256 -cne (Get-FileHash -LiteralPath $loaded.Path -Algorithm SHA256).Hash) {
                throw "Consumer $Lane loaded $name outside its isolated package output or reported different bytes."
            }
        }
        if ($evidence.HostAssembly.Path -ine $Stage.Assembly -or $evidence.HostAssembly.Sha256 -cne $Stage.AssemblySha256) {
            throw "Consumer $Lane did not execute the recorded independently compiled host."
        }
        $requiredObservations = switch ($Lane) {
            'seed' { @('RingAndSharedList', 'ActualRefsVerified', 'CompleteStorageVerified') }
            'upgrade' { @('ReadOnlyFilesUnchanged', 'ExactHistoricalDto', 'ReadOnlyUpgrade', 'SameSUnchangedListShared', 'RewriteWidgetsIndependent') }
            'rewrite' { @('HistoricalForkUsesStateS', 'LatestCatalogRemainsIndependent', 'RetainedListHeadAndH', 'SameSPairShared', 'CrossSPairIndependent') }
            'continue' { @('SingleListDelta', 'ExactPrior', 'IdsPreserved', 'ActualWireH', 'RetainedWidgetHeads') }
            'verify' { @('ReadOnlyFilesUnchanged', 'OldTagPreserved', 'AccurateSchemaPins', 'CompleteFramesPreserved', 'CrossSPairIndependent') }
        }
        foreach ($check in $requiredObservations) {
            if ($evidence.Observations.$check -isnot [bool] -or !$evidence.Observations.$check) { throw "Consumer $Lane failed its $check observation." }
        }
        if ($Lane -eq 'rewrite' -and $evidence.Observations.RewrittenLiveWidgetBases -ne 2) { throw 'Both historical live Widgets must rewrite as actual Base records.' }
        if ($readOnly) {
            $after = @(Get-StoreImages)
            $entry['ReadOnlyAfter'] = $after
            if (($before | ConvertTo-Json -Depth 4 -Compress) -cne ($after | ConvertTo-Json -Depth 4 -Compress)) {
                throw "Read-only consumer $Lane changed the complete repository file set or bytes."
            }
            $entry['ReadOnlyFilesUnchanged'] = $true
        }
        $entry['EvidenceSha256'] = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
        $entry['Observations'] = $evidence.Observations
        $entry['Passed'] = $true
        Write-Host $output.Trim()
    } finally {
        if (!$process.HasExited) {
            $process.Kill($true)
            if (!$process.WaitForExit(30000)) { throw "Consumer $Lane could not be stopped after timeout." }
        }
        $entry['ExitCode'] = $process.ExitCode
        $entry['ExitedUtc'] = [DateTime]::UtcNow.ToString('O')
        $entry['Stdout'] = Join-Path $workRoot ($Lane + '.stdout.log')
        $entry['Stderr'] = Join-Path $workRoot ($Lane + '.stderr.log')
        [IO.File]::WriteAllText($entry.Stdout, [string]($outputTask.GetAwaiter().GetResult()), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($entry.Stderr, [string]($errorTask.GetAwaiter().GetResult()), [Text.UTF8Encoding]::new($false))
        $process.Dispose()
        Save-Receipt
    }
}

Save-Receipt
Start-Transcript -LiteralPath (Join-Path $workRoot 'run.log') | Out-Null
try {
    $storage = & (Join-Path $repositoryRoot 'eng/Prepare-Storage.ps1') -StoragePackageSource $StoragePackageSource
    if (!$PackageSource) {
        $PackageSource = Join-Path $workRoot 'feed'
        $Version = '0.0.0-split-repository-e2e.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.' + $PID
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version -StoragePackageSource $StoragePackageSource *>&1 |
            Tee-Object -LiteralPath (Join-Path $workRoot 'package-pack.log') | Out-Host
    }
    if ($Version -ceq $storage.Version) { throw 'DG version G must differ from storage version S.' }
    $PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
    $storageManifest = Join-Path $storage.PackageSource ('manifest.' + $storage.Version + '.json')
    $receipt.DgVersion = $Version
    $receipt.StorageVersion = $storage.Version
    $receipt.StorageRevision = $storage.Revision
    $receipt.PackageSource = $PackageSource
    $receipt['StorageManifest'] = $storageManifest
    $receipt['StorageManifestSha256'] = (Get-FileHash -LiteralPath $storageManifest -Algorithm SHA256).Hash
    $expected = @{}
    foreach ($id in @('Atelia.DurableGraph', 'Atelia.DurableGraph.Persistence', 'Atelia.DurableGraph.Storage', 'Atelia.DurableGraph.Serialization')) { $expected[$id] = $Version }
    foreach ($package in $storage.Packages) {
        $expected[$package.id] = $package.version
        if ((Get-FileHash -LiteralPath (Join-Path $PackageSource $package.file) -Algorithm SHA256).Hash -ine $package.sha256) {
            throw "The ten-package feed changed pinned storage bytes: $($package.id)."
        }
    }
    if (@(Get-ChildItem -LiteralPath $PackageSource -Filter '*.nupkg' -File).Count -ne 10) { throw 'Expected precisely six pinned storage and four DG packages.' }
    $packageDllHashes = @{}
    foreach ($id in ($expected.Keys | Sort-Object)) {
        $file = Join-Path $PackageSource ($id + '.' + $expected[$id] + '.nupkg')
        $archive = [IO.Compression.ZipFile]::OpenRead($file)
        try {
            $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
            if ($nuspecEntries.Count -ne 1) { throw "Expected exactly one nuspec: $file" }
            $reader = [IO.StreamReader]::new($nuspecEntries[0].Open())
            try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
            if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $id -or
                $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $expected[$id]) { throw "Wrong feed package identity/version: $file" }
            $packageRepository = $metadata.SelectSingleNode('*[local-name()="repository"]')
            $dll = $archive.GetEntry('lib/net10.0/' + $id + '.dll')
            if ($null -eq $dll) { throw "Missing net10.0 runtime library in $file." }
            $stream = $dll.Open()
            try { $packageDllHashes[$id] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        } finally { $archive.Dispose() }
        $receipt.Packages += [ordered]@{ Id = $id; Version = $expected[$id]; File = $file; Sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash; RuntimeDllSha256 = $packageDllHashes[$id]; SourceRevision = $packageRepository.GetAttribute('commit'); RepositoryUrl = $packageRepository.GetAttribute('url') }
    }
    $feedXml = [Security.SecurityElement]::Escape($PackageSource)
    $nugetConfig = Join-Path $workRoot 'probe.nuget.config'
    "<configuration><packageSources><clear/><add key='probe' value='$feedXml'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources><packageSourceMapping><clear/><packageSource key='probe'><package pattern='Atelia.*'/></packageSource><packageSource key='nuget.org'><package pattern='*'/></packageSource></packageSourceMapping></configuration>" |
        Set-Content -LiteralPath $nugetConfig -Encoding utf8NoBOM
    $priorHistory = @()
    Push-Location $workRoot
    try {
        foreach ($historyVersion in @(1, 2)) {
            $consumerRoot = Join-Path $workRoot ('stage-v' + $historyVersion)
            [IO.Directory]::CreateDirectory($consumerRoot) | Out-Null
            foreach ($source in $sourceFiles) { Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $consumerRoot $source.Name) }
            $project = Join-Path $consumerRoot 'SplitRepositoryConsumer.csproj'
            $properties = @("-p:DurableGraphPackageVersion=$Version", "-p:DurableGraphSchemaHistoryDirectory=$history", "-p:HistoryVersion=$historyVersion", '-p:UseStorageSources=false')
            Invoke-LoggedDotNet (@('restore', $project, '--configfile', $nugetConfig, '--disable-parallel', '--packages', $packageCache) + $properties)
            Invoke-LoggedDotNet (@('build', $project, '-c', 'Release', '--no-restore', '-m:1', '-nr:false', '-p:DurableGraphSchemaHistoryMode=Publish') + $properties)
            Require-PreservedHistory $priorHistory
            $accepted = @(Get-HistoryImages)
            if ($accepted.Count -ne $historyVersion) { throw "Stage V$historyVersion expected $historyVersion actual accepted history files." }
            foreach ($image in $accepted) {
                $text = Get-Content -LiteralPath (Join-Path $history $image.File) -Raw
                if (!$text.StartsWith("// durable-graph-schema-history:9`n", [StringComparison]::Ordinal)) { throw 'Expected canonical history v9 from the packaged tool.' }
            }
            Invoke-LoggedDotNet (@('build', $project, '-c', 'Release', '--no-restore', '-m:1', '-nr:false', '-p:DurableGraphSchemaHistoryMode=Verify') + $properties)
            Require-PreservedHistory $accepted
            if (@(Get-HistoryImages).Count -ne $accepted.Count) { throw 'History Verify changed the accepted file set.' }
            $assetsPath = Join-Path $consumerRoot 'obj/project.assets.json'
            $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
            $folders = @($assets.packageFolders.Keys)
            if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ine $packageCache.TrimEnd('\', '/')) { throw 'Assets did not use only the fresh isolated package cache.' }
            if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0) { throw 'Consumer assets contain a source ProjectReference.' }
            $ateliaKeys = @($assets.libraries.Keys | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::Ordinal) })
            if ($ateliaKeys.Count -ne 10) { throw 'The restored Atelia package closure is not the selected ten packages.' }
            foreach ($id in $expected.Keys) {
                if (!$assets.libraries.ContainsKey($id + '/' + $expected[$id])) { throw "Wrong actual package identity/version in assets: $id" }
            }
            $manifests = @(Get-ChildItem -LiteralPath (Join-Path $consumerRoot 'obj') -Recurse -Filter 'DurableGraphSchemaHistoryCandidates.g.cs' -File)
            $references = @(Get-ChildItem -LiteralPath (Join-Path $consumerRoot 'obj') -Recurse -Filter 'DurableGraphSchemaHistoryReferences.g.cs' -File)
            if ($manifests.Count -ne 1 -or $references.Count -ne 1) { throw 'Packaged Generator did not emit its real candidate and reference manifests.' }
            $binaryRoot = Join-Path $consumerRoot 'bin/Release/net10.0'
            $binaries = @()
            foreach ($id in ($expected.Keys | Sort-Object)) {
                $cached = Join-Path $packageCache ($id.ToLowerInvariant() + '/' + $expected[$id].ToLowerInvariant() + '/lib/net10.0/' + $id + '.dll')
                $copied = Join-Path $binaryRoot ($id + '.dll')
                if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash -cne $packageDllHashes[$id] -or
                    (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash -cne $packageDllHashes[$id]) { throw "Restored/output DLL differs from the actual selected package: $id" }
                $binaries += [ordered]@{ Path = $copied; Sha256 = $packageDllHashes[$id] }
            }
            $assembly = Join-Path $binaryRoot 'Atelia.DurableGraph.Persistence.Tests.dll'
            $stage = [ordered]@{ HistoryVersion = $historyVersion; ConsumerRoot = $consumerRoot; BinaryRoot = $binaryRoot; Assembly = $assembly; AssemblySha256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash; Assets = $assetsPath; AssetsSha256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash; History = $accepted; HistoryVerify = 'unchanged'; Binaries = $binaries; Sources = @($sourceFiles | ForEach-Object { [ordered]@{ File = $_.Name; Sha256 = (Get-FileHash -LiteralPath (Join-Path $consumerRoot $_.Name) -Algorithm SHA256).Hash } }) }
            $receipt.Stages += $stage
            if ($historyVersion -eq 1) {
                $runtimeRoot = Join-Path $packageCache ('atelia.durablegraph/' + $Version.ToLowerInvariant())
                foreach ($relative in @('analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll', 'tools/net10.0/Atelia.DurableGraph.Build.dll', 'build/Atelia.DurableGraph.props', 'build/Atelia.DurableGraph.targets')) {
                    $path = Join-Path $runtimeRoot $relative
                    $receipt.CompilerInputs += [ordered]@{ Path = $path; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
                }
                Invoke-Consumer -Lane 'seed' -Stage $stage
            } else {
                foreach ($lane in @('upgrade', 'rewrite', 'continue', 'verify')) { Invoke-Consumer -Lane $lane -Stage $stage }
            }
            $priorHistory = $accepted
        }
    } finally { Pop-Location }
    if (@($receipt.Processes.ProcessId | Select-Object -Unique).Count -ne 5) { throw 'Expected five independently launched consumer processes.' }
    Require-PreservedHistory $priorHistory
    $receipt['Witness'] = $witness
    $receipt['WitnessSha256'] = (Get-FileHash -LiteralPath $witness -Algorithm SHA256).Hash
    $receipt['NuGetConfig'] = $nugetConfig
    $receipt['NuGetConfigSha256'] = (Get-FileHash -LiteralPath $nugetConfig -Algorithm SHA256).Hash
    $receipt.Passed = $true
    Write-Host "Split Repository package consumer passed. Artifacts: $workRoot"
} catch {
    $receipt.Failure = $_.Exception.ToString()
    throw
} finally {
    $receipt.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    Save-Receipt
    Stop-Transcript | Out-Null
}
