[CmdletBinding()]
param(
    [string]$PackageSource,
    [string]$Version,
    [string]$StoragePackageSource = $env:DG_STORAGE_PACKAGE_SOURCE,
    [string]$WorkDirectory,
    [string]$PackProvenancePath,
    [ValidateRange(10, 600)][int]$ProcessTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw 'Supply both -PackageSource and -Version to reuse a complete feed, or neither to prepare one.'
}
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$storageRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '../atelia-storage'))
if ([string]::IsNullOrWhiteSpace($StoragePackageSource)) {
    throw 'Supply -StoragePackageSource or DG_STORAGE_PACKAGE_SOURCE with a manifest-verified feed of the exact six-package pin.'
}
$StoragePackageSource = (Resolve-Path -LiteralPath $StoragePackageSource).Path
$consumerAssemblyName = 'DurableGraph.PublicHistoryConsumer'
$sourceRoot = Join-Path $PSScriptRoot 'SplitRepositoryConsumer'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object { $_.Extension -in '.cs', '.csproj' })
$sourceProject = Join-Path $sourceRoot 'SplitRepositoryConsumer.csproj'
$projectText = Get-Content -LiteralPath $sourceProject -Raw
foreach ($forbidden in @('<Import', '<ProjectReference', '<Analyzer', '<AdditionalFiles')) {
    if ($projectText.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) { throw "Forbidden manual consumer wiring: $forbidden" }
}
[xml]$projectXml = $projectText
if ([string]$projectXml.Project.PropertyGroup.AssemblyName -cne $consumerAssemblyName) {
    throw 'The public carrier must use its ordinary, non-friend assembly identity.'
}
foreach ($include in $projectXml.Project.ItemGroup.Compile.Include) {
    if ([IO.Path]::IsPathRooted($include) -or $include -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'The isolated consumer must compile only its copied local source files.'
    }
}
$runId = 'dg-split-repository-package-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + $PID + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
if (!$WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) $runId }
$workRoot = [IO.Path]::GetFullPath($WorkDirectory).TrimEnd('\', '/')
foreach ($guardRoot in @($repositoryRoot, $storageRepositoryRoot)) {
    $absolute = [IO.Path]::GetFullPath($guardRoot).TrimEnd('\', '/')
    if ($workRoot.Equals($absolute, [StringComparison]::OrdinalIgnoreCase) -or
        $workRoot.StartsWith($absolute + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The fresh consumer work directory must be outside both DG and storage repositories.'
    }
}
# Reject aliased ancestors rather than silently claiming a repository-external boundary.
$ancestor = [IO.Path]::GetDirectoryName($workRoot)
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor -PathType Container) {
        if (((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Use a work directory without symlink/junction ancestors for this isolated gate.'
        }
    }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
if (Test-Path -LiteralPath $workRoot) { throw 'Use a fresh WorkDirectory; prior evidence, history and package bytes are never overwritten.' }
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$resolvedWorkDirectory = (Resolve-Path -LiteralPath $workRoot).Path
if ($resolvedWorkDirectory.TrimEnd('\', '/') -ine $workRoot) { throw 'Unexpected work-directory resolution.' }
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
    SchemaVersion = 2
    Scope = 'DB087 finite non-friend public Repository accepted-history, Shared admission, same-shape private new-root/cold-continuation, complete Event-to-Shared promotion and fresh-Shared-owner List Delta PackageReference gate; five PIDs with final independent-PID read-only verification, no fresh-PID-writer/same-key-reshape/process-crash/performance qualification'
    ConsumerAssemblyName = $consumerAssemblyName
    PublicCarrierUsesFriendIdentity = $false
    RepositoryRoot = $repositoryRoot
    ResolvedWorkDirectory = $resolvedWorkDirectory
    DirectoryBuildIsolation = @(@('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props') | ForEach-Object {
        $path = Join-Path $workRoot $_
        [ordered]@{ Path = $path; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; Content = '<Project />' }
    })
    SourceProvenance = $null
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
    Inputs = @(@($PSCommandPath, (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1'), (Join-Path $repositoryRoot 'eng/Prepare-Storage.ps1'), (Join-Path $repositoryRoot 'eng/StorageDependency.props')) + @($sourceFiles.FullName) | ForEach-Object {
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
    [IO.File]::WriteAllText($temporary, ($receipt | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $receiptPath, $true)
}

function Invoke-LoggedDotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $script:splitCommandIndex++
    $log = Join-Path $workRoot ('dotnet-{0:D2}-{1}.log' -f $script:splitCommandIndex, $Arguments[0])
    & dotnet @Arguments 2>&1 | Tee-Object -LiteralPath $log | Out-Host
    $code = $LASTEXITCODE
    $command = [ordered]@{ Arguments = $Arguments; ExitCode = $code; Output = $log; OutputSha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash }
    $receipt.Commands += $command
    Save-Receipt
    if ($code -ne 0) { throw "dotnet $($Arguments[0]) failed with exit $code; see $log." }
    return $command
}

function Get-PackSourceProvenance {
    # SDK globs can compile untracked source. Do not call tracked-only evidence a
    # HEAD package when those inputs are outside the recorded closure.
    $untracked = @(& git -C $repositoryRoot ls-files --others --exclude-standard -- src eng Directory.Build.props Directory.Build.targets Directory.Packages.props global.json .editorconfig LICENSE)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check untracked package-source inputs.' }
    if ($untracked.Count -ne 0) { throw 'Tracked-only pack provenance requires no untracked inputs in its production scope.' }
    $paths = @(& git -C $repositoryRoot ls-files -- src eng Directory.Build.props Directory.Build.targets Directory.Packages.props global.json .editorconfig LICENSE)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked package-source inputs.' }
    $paths = @($paths | Where-Object { $_ -cne 'src/PROJECT-STATE.md' } | Sort-Object -Unique)
    $inputImages = @($paths | ForEach-Object {
        $path = Join-Path $repositoryRoot $_
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "A tracked production input is missing: $_" }
        [ordered]@{ Path = $_; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    })
    $head = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record package-source HEAD.' }
    $relevant = @(& git -C $repositoryRoot diff --name-status HEAD -- @paths)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot establish production-input status against HEAD.' }
    $whole = @(& git -C $repositoryRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot establish whole-worktree status.' }
    [ordered]@{
        SchemaVersion = 1
        Scope = 'DG.PublicAcceptedHistory.PackSource.v1'
        RepositoryHead = $head
        ProductionInputsMatchHead = $relevant.Count -eq 0
        IncludesUncommittedProductionSource = $relevant.Count -ne 0
        WholeWorktreeClean = $whole.Count -eq 0
        RelevantStatus = $relevant
        WholeWorktreeStatus = $whole
        InputScope = 'Conservative tracked src/** except PROJECT-STATE.md, eng/** and root build/config/license inputs; includes pack assets, not a claim that every file is compiled.'
        Inputs = $inputImages
        CapturedUtc = [DateTime]::UtcNow.ToString('O')
    }
}

function Require-SamePackInputs {
    param($Before, $After)
    if ($Before.RepositoryHead -cne $After.RepositoryHead -or
        ($Before.Inputs | ConvertTo-Json -Depth 4 -Compress) -cne ($After.Inputs | ConvertTo-Json -Depth 4 -Compress)) {
        throw 'Tracked package-source inputs or HEAD changed across the controlled pack operation.'
    }
}

function Read-PackProvenance {
    param([string]$Path, $Current)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'A reused feed requires its controlled pack-source provenance; a nuspec commit alone is insufficient.' }
    $proof = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($proof.SchemaVersion -ne 1 -or $proof.Scope -cne 'DG.PublicAcceptedHistory.PackSource.v1' -or
        $proof.DgVersion -cne $Version -or $proof.RepositoryHead -notmatch '^[0-9a-fA-F]{40}$' -or
        $proof.ProductionInputsMatchHead -isnot [bool] -or $proof.IncludesUncommittedProductionSource -isnot [bool] -or
        $proof.ProductionInputsMatchHead -eq $proof.IncludesUncommittedProductionSource) {
        throw 'Reused-feed source provenance does not identify this gate, version and source status.'
    }
    if (($proof.Inputs | ConvertTo-Json -Depth 4 -Compress) -cne ($Current.Inputs | ConvertTo-Json -Depth 4 -Compress)) {
        throw 'Reused-feed provenance does not match the present tracked production-input bytes.'
    }
    & git -C $repositoryRoot cat-file -e ($proof.RepositoryHead + '^{commit}')
    if ($LASTEXITCODE -ne 0) { throw 'The recorded package-source commit is unavailable in this repository.' }
    $changed = @(& git -C $repositoryRoot diff --name-only $proof.RepositoryHead -- @($proof.Inputs.Path))
    if ($LASTEXITCODE -ne 0 -or ($changed.Count -eq 0) -ne $proof.ProductionInputsMatchHead) {
        throw 'Reused-feed production-input/HEAD status is not supported by the current source bytes.'
    }
    return $proof
}

function Require-ActualCompilerConsumption {
    param($Command, [string]$Mode, [string]$ConsumerRoot)
    $output = (Get-Content -LiteralPath $Command.Output -Raw).Replace('\', '/')
    $runtime = Join-Path $packageCache ('atelia.durablegraph/' + $Version.ToLowerInvariant())
    $generator = (Join-Path $runtime 'analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll').Replace('\', '/')
    $toolSuffix = ($runtime.Replace('\', '/') + '/build/../tools/net10.0/Atelia.DurableGraph.Build.dll')
    $canonicalTool = (Join-Path $runtime 'tools/net10.0/Atelia.DurableGraph.Build.dll').Replace('\', '/')
    if ($output.IndexOf($generator, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $output -notmatch '(?i)/analyzer:' -or $output -notmatch '(?i)CoreCompile') {
        throw "The $Mode build log does not show the actual package Generator in compilation."
    }
    if (($output.IndexOf($toolSuffix, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
         $output.IndexOf($canonicalTool, [StringComparison]::OrdinalIgnoreCase) -lt 0) -or
        $output -notmatch ('(?i)\s' + [Regex]::Escape($Mode.ToLowerInvariant()) + '\s+--manifest') -or
        $output.IndexOf($ConsumerRoot.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "The $Mode build log does not show actual execution of the packaged accepted-history tool."
    }
    [ordered]@{ Mode = $Mode; Log = $Command.Output; LogSha256 = $Command.OutputSha256; GeneratorPath = $generator; BuildToolPath = $canonicalTool; ActualCompilerAndToolObserved = $true }
}

function Require-FrozenCompilerInputs {
    foreach ($input in $receipt.CompilerInputs) {
        if ((Get-FileHash -LiteralPath $input.Path -Algorithm SHA256).Hash -cne $input.PackageEntrySha256) {
            throw "A compiler/tool input changed after its actual package entry was verified: $($input.Entry)"
        }
    }
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
        $actual = @($evidence.LoadedAssemblies)
        if ($actual.Count -ne 10 -or @($actual.Name | Sort-Object -Unique).Count -ne 10) {
            throw "Consumer $Lane must report its ten actually loaded DG/storage assemblies."
        }
        foreach ($id in $expected.Keys) {
            $loaded = @($actual | Where-Object { $_.Name -ceq $id })
            $expectedPath = Join-Path $Stage.BinaryRoot ($id + '.dll')
            if ($loaded.Count -ne 1 -or $loaded[0].Path -ine $expectedPath -or
                $loaded[0].Sha256 -cne $packageDllHashes[$id] -or
                (Get-FileHash -LiteralPath $loaded[0].Path -Algorithm SHA256).Hash -cne $packageDllHashes[$id]) {
                throw "Consumer $Lane did not actually load the selected package bytes for $id from its stage."
            }
        }
        $entry['LoadedAssemblies'] = $actual
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
            'rewrite' { @('HistoricalForkUsesStateS', 'LatestCatalogRemainsIndependent', 'RetainedListHeadAndH', 'SameSPairShared', 'CrossSPairIndependent', 'EventCandidateDiscardKeepsStateRewrite') }
            'continue' { @('UnconfiguredSaveRejected', 'SingleListDelta', 'ExactPrior', 'IdsPreserved', 'ActualWireH', 'RetainedWidgetHeads',
                'PrivateFrozenCatalog', 'PrivateColdListDelta', 'PrivateSharedRootUnchanged', 'PrivateSharedSaveRejected',
                'CompletePromotion', 'PromotionRefsAdvanced', 'PromotionEventRetainsState', 'SharedColdOwnerListDelta', 'PromotionRetainedOrigins') }
            'verify' { @('ReadOnlyFilesUnchanged', 'OldTagPreserved', 'AccurateSchemaPins', 'CompleteFramesPreserved', 'CrossSPairIndependent', 'PrivateColdValues', 'PromotedSharedColdValues') }
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
    $packBefore = Get-PackSourceProvenance
    $receipt['CurrentSourceProvenance'] = $packBefore
    $storage = & (Join-Path $repositoryRoot 'eng/Prepare-Storage.ps1') -StoragePackageSource $StoragePackageSource
    $createdFeed = !$PackageSource
    if ($createdFeed) {
        $PackageSource = Join-Path $workRoot 'feed'
        $Version = '0.0.0-public-history-e2e.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.' + $PID
        if (Test-Path -LiteralPath $PackageSource) { throw 'The controlled pack feed must be fresh; package versions are not overwritten.' }
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version -StoragePackageSource $StoragePackageSource *>&1 |
            Tee-Object -LiteralPath (Join-Path $workRoot 'package-pack.log') | Out-Host
    }
    $packAfter = Get-PackSourceProvenance
    Require-SamePackInputs $packBefore $packAfter
    $provenanceFile = if ($PackProvenancePath) { [IO.Path]::GetFullPath($PackProvenancePath) } else { Join-Path $PackageSource ('dg-source-provenance.' + $Version + '.json') }
    $packProof = if ($createdFeed) { $packBefore } else { Read-PackProvenance -Path $provenanceFile -Current $packAfter }
    if ($createdFeed) {
        $packProof['DgVersion'] = $Version
        $packProof['PackCompletedUtc'] = [DateTime]::UtcNow.ToString('O')
        $packProof['Packages'] = @()
    }
    $receipt.SourceProvenance = $packProof
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
            $expectedCommit = if ($id.StartsWith('Atelia.DurableGraph', [StringComparison]::Ordinal)) { $packProof.RepositoryHead } else { $storage.Revision }
            if (!$packageRepository -or $packageRepository.GetAttribute('commit') -ine $expectedCommit) {
                throw "Package source commit does not match controlled DG provenance or the storage pin: $id"
            }
            $dll = $archive.GetEntry('lib/net10.0/' + $id + '.dll')
            if ($null -eq $dll) { throw "Missing net10.0 runtime library in $file." }
            $stream = $dll.Open()
            try { $packageDllHashes[$id] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        } finally { $archive.Dispose() }
        $receipt.Packages += [ordered]@{ Id = $id; Version = $expected[$id]; File = $file; Sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash; RuntimeDllSha256 = $packageDllHashes[$id]; SourceRevision = $packageRepository.GetAttribute('commit'); RepositoryUrl = $packageRepository.GetAttribute('url') }
    }
    if ($createdFeed) {
        $packProof.Packages = @($receipt.Packages | Where-Object { $_.Id.StartsWith('Atelia.DurableGraph', [StringComparison]::Ordinal) } | ForEach-Object {
            [ordered]@{ Id = $_.Id; Version = $_.Version; Sha256 = $_.Sha256; RuntimeDllSha256 = $_.RuntimeDllSha256; SourceRevision = $_.SourceRevision }
        })
        if (Test-Path -LiteralPath $provenanceFile) { throw 'Pack provenance is immutable for this new version.' }
        [IO.File]::WriteAllText($provenanceFile, ($packProof | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    } else {
        foreach ($package in @($receipt.Packages | Where-Object { $_.Id.StartsWith('Atelia.DurableGraph', [StringComparison]::Ordinal) })) {
            $recorded = @($packProof.Packages | Where-Object { $_.Id -ceq $package.Id })
            if ($recorded.Count -ne 1 -or $recorded[0].Version -cne $package.Version -or $recorded[0].Sha256 -cne $package.Sha256 -or
                $recorded[0].RuntimeDllSha256 -cne $package.RuntimeDllSha256 -or $recorded[0].SourceRevision -ine $package.SourceRevision) {
                throw "The reused package bytes are not the controlled provenance output: $($package.Id)"
            }
        }
    }
    $receipt['PackProvenancePath'] = $provenanceFile
    $receipt['PackProvenanceSha256'] = (Get-FileHash -LiteralPath $provenanceFile -Algorithm SHA256).Hash
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
            $null = Invoke-LoggedDotNet (@('restore', $project, '--configfile', $nugetConfig, '--disable-parallel', '--packages', $packageCache) + $properties)
            Require-FrozenCompilerInputs
            $publish = Invoke-LoggedDotNet (@('build', $project, '-c', 'Release', '--no-restore', '-t:Rebuild', '-m:1', '-nr:false', '-v:normal', '-p:UseSharedCompilation=false', '-p:DurableGraphSchemaHistoryMode=Publish') + $properties)
            $publishConsumption = Require-ActualCompilerConsumption -Command $publish -Mode 'Publish' -ConsumerRoot $consumerRoot
            Require-PreservedHistory $priorHistory
            $accepted = @(Get-HistoryImages)
            if ($accepted.Count -ne $historyVersion) { throw "Stage V$historyVersion expected $historyVersion actual accepted history files." }
            foreach ($image in $accepted) {
                $text = Get-Content -LiteralPath (Join-Path $history $image.File) -Raw
                if (!$text.StartsWith("// durable-graph-schema-history:9`n", [StringComparison]::Ordinal)) { throw 'Expected canonical history v9 from the packaged tool.' }
            }
            $verify = Invoke-LoggedDotNet (@('build', $project, '-c', 'Release', '--no-restore', '-t:Rebuild', '-m:1', '-nr:false', '-v:normal', '-p:UseSharedCompilation=false', '-p:DurableGraphSchemaHistoryMode=Verify') + $properties)
            $verifyConsumption = Require-ActualCompilerConsumption -Command $verify -Mode 'Verify' -ConsumerRoot $consumerRoot
            Require-FrozenCompilerInputs
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
            $assembly = Join-Path $binaryRoot ($consumerAssemblyName + '.dll')
            $stage = [ordered]@{ HistoryVersion = $historyVersion; ConsumerRoot = $consumerRoot; BinaryRoot = $binaryRoot; Assembly = $assembly; AssemblySha256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash; Assets = $assetsPath; AssetsSha256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash; History = $accepted; HistoryVerify = 'unchanged'; CompilerConsumption = @($publishConsumption, $verifyConsumption); CandidateManifest = $manifests[0].FullName; CandidateManifestSha256 = (Get-FileHash -LiteralPath $manifests[0].FullName -Algorithm SHA256).Hash; ReferenceManifest = $references[0].FullName; ReferenceManifestSha256 = (Get-FileHash -LiteralPath $references[0].FullName -Algorithm SHA256).Hash; Binaries = $binaries; Sources = @($sourceFiles | ForEach-Object { [ordered]@{ File = $_.Name; Sha256 = (Get-FileHash -LiteralPath (Join-Path $consumerRoot $_.Name) -Algorithm SHA256).Hash } }) }
            $receipt.Stages += $stage
            if ($historyVersion -eq 1) {
                $runtimeRoot = Join-Path $packageCache ('atelia.durablegraph/' + $Version.ToLowerInvariant())
                foreach ($relative in @('analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll', 'tools/net10.0/Atelia.DurableGraph.Build.dll', 'build/Atelia.DurableGraph.props', 'build/Atelia.DurableGraph.targets')) {
                    $path = Join-Path $runtimeRoot $relative
                    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $PackageSource ('Atelia.DurableGraph.' + $Version + '.nupkg')))
                    try {
                        $entry = $archive.GetEntry($relative)
                        if ($null -eq $entry) { throw "Missing packaged compiler input: $relative" }
                        $inputStream = $entry.Open()
                        try { $packageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream)) } finally { $inputStream.Dispose() }
                    } finally { $archive.Dispose() }
                    $cacheHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
                    if ($cacheHash -cne $packageHash) { throw "Restored compiler input differs from its actual package entry: $relative" }
                    $receipt.CompilerInputs += [ordered]@{ Package = 'Atelia.DurableGraph'; Entry = $relative; Path = $path; PackageEntrySha256 = $packageHash; Sha256 = $cacheHash }
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
    Require-SamePackInputs $packAfter (Get-PackSourceProvenance)
    Require-FrozenCompilerInputs
    foreach ($package in $receipt.Packages) {
        if ((Get-FileHash -LiteralPath $package.File -Algorithm SHA256).Hash -cne $package.Sha256) {
            throw "A selected feed package changed during consumption: $($package.Id)"
        }
    }
    foreach ($input in $receipt.Inputs) {
        if ((Get-FileHash -LiteralPath $input.Path -Algorithm SHA256).Hash -cne $input.Sha256) {
            throw "A runner/consumer/preparation input changed during the gate: $($input.Path)"
        }
    }
    $receipt.Passed = $true
    Write-Host "Public accepted-history Repository package consumer passed. Artifacts: $workRoot"
} catch {
    $receipt.Failure = $_.Exception.ToString()
    throw
} finally {
    $receipt.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    Save-Receipt
    Stop-Transcript | Out-Null
}
