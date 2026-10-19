[CmdletBinding()]
param(
    [string] $PackageSource,
    [string] $Version,
    [string] $StoragePackageSource = $env:DG_STORAGE_PACKAGE_SOURCE,
    [string] $WorkDirectory
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw 'Supply both -PackageSource and -Version, or neither to prepare a fresh DG feed.'
}
if ([string]::IsNullOrWhiteSpace($StoragePackageSource)) {
    throw 'Supply -StoragePackageSource with the verified local six-package pin feed.'
}
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourceRoot = Join-Path $PSScriptRoot 'MetaBootstrapSchemaConsumer'
$runId = 'dg-meta-schema-package-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + $PID + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
if (!$WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) $runId }
$workRoot = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $workRoot) { throw 'Use a fresh work directory; prior evidence and package bytes are never overwritten.' }
[void](New-Item -ItemType Directory -Path $workRoot)
$consumerRoot = Join-Path $workRoot 'consumer'
$packageCache = Join-Path $workRoot 'packages'
$history = Join-Path $workRoot 'history'
$database = Join-Path $workRoot 'database'
$witness = Join-Path $workRoot 'witness.json'
[void](New-Item -ItemType Directory -Path $consumerRoot)
[void](New-Item -ItemType Directory -Path $packageCache)
foreach ($name in @('MetaBootstrapSchemaConsumer.csproj', 'Model.cs', 'Program.cs')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $consumerRoot $name)
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination (Join-Path $workRoot 'global.json')
# Explicit boundaries also protect custom work directories located under another repository.
foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    '<Project />' | Set-Content -LiteralPath (Join-Path $workRoot $name) -Encoding utf8NoBOM
}
$consumerProject = Join-Path $consumerRoot 'MetaBootstrapSchemaConsumer.csproj'
$consumerText = Get-Content -LiteralPath $consumerProject -Raw
foreach ($forbidden in @('<Import', '<ProjectReference', '<Analyzer', '<AdditionalFiles')) {
    if ($consumerText.Contains($forbidden, [StringComparison]::Ordinal)) { throw "Forbidden consumer source wiring: $forbidden" }
}

function Store-Snapshot {
    @((Get-ChildItem -LiteralPath $database -Recurse -File | Sort-Object FullName) | ForEach-Object {
        $_.FullName.Substring($database.Length) + '|' + $_.Length + '|' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n"
}

$script:metaProbeCommandIndex = 0
function Invoke-LoggedDotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    $script:metaProbeCommandIndex++
    $name = 'dotnet-{0:D2}-{1}' -f $script:metaProbeCommandIndex, $Arguments[0]
    $log = Join-Path $workRoot ($name + '.log')
    & dotnet @Arguments 2>&1 | Tee-Object -LiteralPath $log | Out-Host
    $code = $LASTEXITCODE
    [ordered]@{ Arguments = $Arguments; ExitCode = $code; Output = $log } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $workRoot ($name + '.json')) -Encoding utf8NoBOM
    if ($code -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $code; output: $log" }
}

Start-Transcript -LiteralPath (Join-Path $workRoot 'run.log') | Out-Null
try {
    $storage = & (Join-Path $repositoryRoot 'eng/Prepare-Storage.ps1') -StoragePackageSource $StoragePackageSource
    if (!$PackageSource) {
        $PackageSource = Join-Path $workRoot 'feed'
        $Version = '0.0.0-meta-schema-e2e.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.' + $PID
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version -StoragePackageSource $StoragePackageSource *>&1 |
            Tee-Object -LiteralPath (Join-Path $workRoot 'package-pack.log') | Out-Host
    }
    $PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
    if (@(Get-ChildItem -LiteralPath $PackageSource -Filter '*.nupkg' -File).Count -ne 10) { throw 'Expected six pinned storage and four new DG packages.' }
    foreach ($package in $storage.Packages) {
        $actual = Get-FileHash -LiteralPath (Join-Path $PackageSource $package.file) -Algorithm SHA256
        if ($actual.Hash -ine $package.sha256) { throw "The downstream feed changed pinned bytes: $($package.id)" }
    }
    $feedXml = [Security.SecurityElement]::Escape($PackageSource)
    $nugetConfig = Join-Path $workRoot 'probe.nuget.config'
    "<configuration><packageSources><clear/><add key='probe' value='$feedXml'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources><packageSourceMapping><clear/><packageSource key='probe'><package pattern='Atelia.*'/></packageSource><packageSource key='nuget.org'><package pattern='*'/></packageSource></packageSourceMapping></configuration>" |
        Set-Content -LiteralPath $nugetConfig -Encoding utf8NoBOM
    $properties = @(
        "-p:DurableGraphPackageVersion=$Version",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        '-p:UseStorageSources=false'
    )
    Push-Location $workRoot
    try {
        Invoke-LoggedDotNet (@('restore', $consumerProject, '--configfile', $nugetConfig, '--disable-parallel', '--packages', $packageCache) + $properties)
        Invoke-LoggedDotNet (@('build', $consumerProject, '-c', 'Release', '--no-restore', '-m:1', '-nr:false') + $properties)
        $schemas = @(Get-ChildItem -LiteralPath $history -Filter '*.dgschema' -File)
        if ($schemas.Count -ne 4) { throw 'Expected exactly the independently generated Base, Point, Key and Root histories.' }
        $accepted = @{}
        foreach ($schema in $schemas) { $accepted[$schema.Name] = (Get-FileHash -LiteralPath $schema.FullName -Algorithm SHA256).Hash }
        Invoke-LoggedDotNet (@('build', $consumerProject, '-c', 'Release', '--no-restore', '-m:1', '-nr:false', '-p:DurableGraphSchemaHistoryMode=Verify') + $properties)
        foreach ($schema in $schemas) {
            if ((Get-FileHash -LiteralPath $schema.FullName -Algorithm SHA256).Hash -ne $accepted[$schema.Name]) { throw 'History Verify changed accepted consumer schemas.' }
        }
        $assetsPath = Join-Path $consumerRoot 'obj/project.assets.json'
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        $folders = @($assets.packageFolders.Keys)
        if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ine $packageCache.TrimEnd('\', '/')) {
            throw 'Consumer assets did not use only the fresh isolated package cache.'
        }
        if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0) { throw 'Consumer assets contain a source project dependency.' }
        $expected = @{}
        foreach ($id in @('Atelia.DurableGraph', 'Atelia.DurableGraph.Persistence', 'Atelia.DurableGraph.Storage', 'Atelia.DurableGraph.Serialization')) { $expected[$id] = $Version }
        foreach ($package in $storage.Packages) { $expected[$package.id] = $storage.Version }
        $actualAtelia = @($assets.libraries.Keys | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::Ordinal) })
        if ($actualAtelia.Count -ne $expected.Count) { throw 'The restored Atelia dependency closure differs from the selected ten packages.' }
        foreach ($id in $expected.Keys) {
            if (!$assets.libraries.ContainsKey($id + '/' + $expected[$id])) { throw "Wrong actual package identity/version: $id" }
        }
        $generated = @(Get-ChildItem -LiteralPath (Join-Path $consumerRoot 'obj') -Recurse -Filter '*.cs' -File | Where-Object {
            (Get-Content -LiteralPath $_.FullName -Raw).Contains('p1.closure.root', [StringComparison]::Ordinal)
        })
        if ($generated.Count -eq 0) { throw 'No packaged-generator output for the independent Root model was emitted.' }
        $binaryRoot = Join-Path $consumerRoot 'bin/Release/net10.0'
        foreach ($id in $expected.Keys) {
            $cached = Join-Path $packageCache ($id.ToLowerInvariant() + '/' + $expected[$id].ToLowerInvariant() + '/lib/net10.0/' + $id + '.dll')
            $copied = Join-Path $binaryRoot ($id + '.dll')
            if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash) {
                throw "Loaded binary input differs from the restored package: $id"
            }
        }
        $assembly = Join-Path $binaryRoot 'Atelia.DurableGraph.Persistence.Tests.dll'
        foreach ($lane in @('seed', 'read', 'continue', 'verify')) {
            $before = if ($lane -in @('read', 'verify')) { Store-Snapshot } else { $null }
            $actual = (& dotnet $assembly $lane $database $witness 2>&1 |
                Tee-Object -LiteralPath (Join-Path $workRoot ($lane + '.stdout.log')) | Out-String).Trim()
            if ($LASTEXITCODE -ne 0 -or $actual -cne ('MetaBootstrapSchema:' + $lane + ':Passed')) {
                throw "Meta Schema consumer lane '$lane' failed: $actual"
            }
            if ($null -ne $before -and $before -cne (Store-Snapshot)) { throw 'A read-only package process changed the repository files.' }
            $evidence = Get-Content -LiteralPath (Join-Path $workRoot ($lane + '.evidence.json')) -Raw | ConvertFrom-Json
            if ($evidence.CommitHandleBytes -ne 12 -or $evidence.SourceSelection -cne 'HeadStoreStrictPrecedingState') {
                throw 'Consumer must select exact published Commit handles and qualify the strict preceding State from HeadStore history.'
            }
            foreach ($name in @('EmployeeAssembly', 'PersistenceAssembly', 'RuntimeAssembly')) {
                $loaded = $evidence.$name
                if (!$loaded.Path.StartsWith($binaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                    $loaded.Sha256 -cne (Get-FileHash -LiteralPath $loaded.Path -Algorithm SHA256).Hash) { throw 'A process loaded a binary outside the isolated package output.' }
            }
            Write-Host $actual
        }
        $processes = @(@('seed', 'read', 'continue', 'verify') | ForEach-Object {
            (Get-Content -LiteralPath (Join-Path $workRoot ($_ + '.evidence.json')) -Raw | ConvertFrom-Json).ProcessId
        })
        if (@($processes | Select-Object -Unique).Count -ne 4) { throw 'Expected four independently launched consumer processes.' }
        $typedAbis = @(@('seed', 'read', 'continue', 'verify') | ForEach-Object {
            (Get-Content -LiteralPath (Join-Path $workRoot ($_ + '.evidence.json')) -Raw | ConvertFrom-Json).TypedBodyAbi
        })
        if (@($typedAbis | Select-Object -Unique).Count -ne 1) { throw 'Consumer processes reported different typed-body ABIs.' }
        if ($typedAbis[0] -ne 5) { throw 'The Dictionary slice requires typed-body ABI 5.' }
    }
    finally { Pop-Location }
    $packages = @(Get-ChildItem -LiteralPath $PackageSource -Filter '*.nupkg' -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ File = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $sources = @(@('MetaBootstrapSchemaConsumer.csproj', 'Model.cs', 'Program.cs') | ForEach-Object {
        [ordered]@{ File = $_; Sha256 = (Get-FileHash -LiteralPath (Join-Path $consumerRoot $_) -Algorithm SHA256).Hash }
    })
    $manifest = Join-Path $storage.PackageSource ('manifest.' + $storage.Version + '.json')
    [ordered]@{
        SchemaVersion = 3; Scope = 'DB096 package migration of internal employee Schema closure and List/Array/Dictionary bodies; no public API/crash qualification';
        CommitHandleBytes = 12; SourceSelection = 'HeadStoreStrictPrecedingState';
        DgVersion = $Version; StorageVersion = $storage.Version; StorageRevision = $storage.Revision;
        StorageManifest = $manifest; StorageManifestSha256 = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash;
        WorkDirectory = $workRoot; PackageSource = $PackageSource; PackageCache = $packageCache;
        Assets = $assetsPath; AssetsSha256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash;
        Packages = $packages; Sources = $sources; ProcessIds = $processes; Lanes = @('seed', 'read', 'continue', 'verify');
        TypedBodyAbi = $typedAbis[0]; BusinessObjects = 9; DeltaObjects = 9; CatalogSchemas = 4; CatalogDescriptors = 7;
        DictionaryComparers = @('ScalarDefault', 'CurrentDefault');
        HistoryVerify = 'unchanged'; History = @($schemas | ForEach-Object {
            [ordered]@{ File = $_.Name; Sha256 = $accepted[$_.Name] }
        });
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workRoot 'result.json') -Encoding utf8NoBOM
    Write-Host "Meta Schema package consumer passed. Artifacts: $workRoot"
}
finally { Stop-Transcript | Out-Null }
