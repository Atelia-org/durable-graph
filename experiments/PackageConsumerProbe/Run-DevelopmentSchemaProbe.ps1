[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageSource,
    [Parameter(Mandatory)][string]$Version,
    [string]$WorkDirectory,
    [string]$PackProvenancePath,
    [ValidateRange(10, 600)][int]$ProcessTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
[xml]$storagePins = Get-Content -LiteralPath (Join-Path $repositoryRoot 'eng/StorageDependency.props') -Raw
$storageVersion = $storagePins.SelectSingleNode('/Project/PropertyGroup/StoragePackageVersion').InnerText
$storageRevision = $storagePins.SelectSingleNode('/Project/PropertyGroup/StorageSourceRevision').InnerText
if ($Version -ceq $storageVersion) { throw 'DG G must differ from storage S.' }
$sourceRoot = Join-Path $PSScriptRoot 'DevelopmentSchemaConsumer'
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object { $_.Extension -in '.cs', '.csproj' })
$assemblyName = 'DurableGraph.DevelopmentSchemaConsumer'
$projectText = Get-Content -LiteralPath (Join-Path $sourceRoot 'DevelopmentSchemaConsumer.csproj') -Raw
foreach ($forbidden in @('<Import', '<ProjectReference', '<Analyzer', '<AdditionalFiles')) {
    if ($projectText.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) { throw "Forbidden manual consumer wiring: $forbidden" }
}
[xml]$projectXml = $projectText
if ([string]$projectXml.Project.PropertyGroup.AssemblyName -cne $assemblyName) { throw 'Use the ordinary non-friend consumer identity.' }
foreach ($include in $projectXml.Project.ItemGroup.Compile.Include) {
    if ([IO.Path]::IsPathRooted($include) -or $include -match '(^|[\\/])\.\.([\\/]|$)') { throw 'Compile only the copied local inputs.' }
}
if (!$WorkDirectory) {
    $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dg-development-schema-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + $PID)
}
$workRoot = [IO.Path]::GetFullPath($WorkDirectory).TrimEnd('\', '/')
foreach ($boundary in @($repositoryRoot, [IO.Path]::GetFullPath((Join-Path $repositoryRoot '../atelia-storage')))) {
    $boundary = $boundary.TrimEnd('\', '/')
    if ($workRoot.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $workRoot.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use a fresh work directory outside DG and storage repositories.'
    }
}
$ancestor = [IO.Path]::GetDirectoryName($workRoot)
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Use a work directory without junction/symlink ancestors.'
    }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
if (Test-Path -LiteralPath $workRoot) { throw 'Do not overwrite prior history, database or receipts.' }
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$cache = Join-Path $workRoot 'packages'
$developmentHistory = Join-Path $workRoot 'development-history'
$releaseHistory = Join-Path $workRoot 'release-history'
$database = Join-Path $workRoot 'database'
$witness = Join-Path $workRoot 'witness.json'
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination (Join-Path $workRoot 'global.json')
foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    [IO.File]::WriteAllText((Join-Path $workRoot $name), '<Project />', [Text.UTF8Encoding]::new($false))
}
$receipt = [ordered]@{
    Protocol = 'DG.DevelopmentSchemaProbe.v1'
    Scope = 'Ordinary exact-package MSBuild and public runtime development workflow: fixed shared V1/tag, inherited V1 identity/content, two incompatible V2 Off drafts, only winner B accepted/released; no new-root fork, data migration from another V2 draft, wire/H/performance/crash qualification'
    PackageSource = $PackageSource; DgVersion = $Version; StorageVersion = $storageVersion; StorageRevision = $storageRevision
    WorkDirectory = $workRoot; ControllerProcessId = $PID; StartedUtc = [DateTime]::UtcNow.ToString('O')
    Inputs = @(@($PSCommandPath) + @($sources.FullName) | ForEach-Object { [ordered]@{ Path = $_; Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
    Packages = @(); CompilerInputs = @(); Stages = @(); Processes = @(); Commands = @()
    Passed = $false; Failure = $null
}
$commandIndex = 0
function Save-Receipt {
    [IO.File]::WriteAllText((Join-Path $workRoot 'result.json'), ($receipt | ConvertTo-Json -Depth 25), [Text.UTF8Encoding]::new($false))
}
function History-Images([string]$Directory) {
    @(Get-ChildItem -LiteralPath $Directory -File -Filter '*.dgschema' | Sort-Object Name | ForEach-Object {
        [ordered]@{ File = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Require-History([string]$Directory, $Images) {
    if ((@(History-Images $Directory) | ConvertTo-Json -Compress) -cne ($Images | ConvertTo-Json -Compress)) {
        throw 'An accepted history file set or complete bytes changed.'
    }
}
function Zip-Hash([string]$Package, [string]$Entry) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $item = $zip.GetEntry($Entry)
        if (!$item) { throw "Missing actual package entry: $Entry" }
        $stream = $item.Open()
        try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
}
function Invoke-DevelopmentDotnet([string[]]$Arguments, [bool]$ExpectRejection = $false) {
    $script:commandIndex++
    $log = Join-Path $workRoot ('dotnet-{0:D2}.log' -f $script:commandIndex)
    & dotnet @Arguments *> $log
    $exit = $LASTEXITCODE
    $mode = @($Arguments | Where-Object { $_.StartsWith('-p:DurableGraphSchemaHistoryMode=', [StringComparison]::Ordinal) })
    Write-Host ("dotnet {0} {1}: exit {2}; log {3}" -f $Arguments[0], ($mode -join ''), $exit, $log)
    $entry = [ordered]@{ Arguments = $Arguments; ExitCode = $exit; ExpectedRejection = $ExpectRejection; Log = $log; Sha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash }
    $receipt.Commands += $entry
    Save-Receipt
    if (!$ExpectRejection -and $exit -ne 0) { throw "dotnet failed; see $log." }
    if ($ExpectRejection -and ($exit -eq 0 -or (Get-Content -LiteralPath $log -Raw) -notmatch '\bDG0015\b')) {
        throw 'A Off against accepted V2B must fail specifically with the Generator history mismatch DG0015.'
    }
    return $entry
}
function Compile-Stage([string]$Name, [string]$Shape, [string]$History, [string[]]$Modes, [bool]$Reject = $false) {
    $directory = Join-Path $workRoot $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    foreach ($source in $sources) { Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $directory $source.Name) }
    $project = Join-Path $directory 'DevelopmentSchemaConsumer.csproj'
    $properties = @("-p:DurableGraphPackageVersion=$Version", "-p:ShapeStage=$Shape", "-p:DurableGraphSchemaHistoryDirectory=$History", '-p:UseStorageSources=false')
    $null = Invoke-DevelopmentDotnet (@('restore', $project, '--configfile', $config, '--packages', $cache, '--disable-parallel') + $properties)
    foreach ($mode in $Modes) {
        $before = if (Test-Path -LiteralPath $History) { @(History-Images $History) } else { @() }
        $command = Invoke-DevelopmentDotnet (@('build', $project, '-c', 'Release', '--no-restore', '-t:Rebuild', '-m:1', '-nr:false', '-v:normal',
            '-p:UseSharedCompilation=false', "-p:DurableGraphSchemaHistoryMode=$mode") + $properties) $Reject
        $output = (Get-Content -LiteralPath $command.Log -Raw).Replace('\', '/')
        $generator = (Join-Path $cache ('atelia.durablegraph/' + $Version.ToLowerInvariant() + '/analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll')).Replace('\', '/')
        if ($output.IndexOf($generator, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or $output -notmatch '(?i)/analyzer:') {
            throw 'The actual compiler did not consume the packaged Source Generator.'
        }
        foreach ($image in $before) {
            if ($output.IndexOf($image.File, [StringComparison]::Ordinal) -lt 0 -or $output -notmatch '(?i)/additionalfile:') {
                throw 'The actual compiler did not consume the accepted history AdditionalFiles.'
            }
        }
        if ($mode -ceq 'Off') {
            if ($output -match '(?i)\s(publish|verify)\s+--manifest') { throw 'Off must skip the packaged history publication/verification command.' }
            Require-History $History $before
        } else {
            $tool = (Join-Path $cache ('atelia.durablegraph/' + $Version.ToLowerInvariant() + '/tools/net10.0/Atelia.DurableGraph.Build.dll')).Replace('\', '/')
            if ($output -notmatch ('(?i)\s' + $mode.ToLowerInvariant() + '\s+--manifest') -or
                ($output.IndexOf($tool, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
                $output.IndexOf($tool.Replace('/tools/', '/build/../tools/'), [StringComparison]::OrdinalIgnoreCase) -lt 0)) {
                throw 'The actual Rebuild did not execute the packaged Build CLI.'
            }
            if ($mode -ceq 'Verify') { Require-History $History $before }
        }
    }
    if ($Reject) { return }
    $assetsPath = Join-Path $directory 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0 -or
        @($assets.libraries.Keys | Where-Object { $_.StartsWith('Atelia.') }).Count -ne 10 -or
        @($assets.packageFolders.Keys).Count -ne 1 -or [IO.Path]::GetFullPath(@($assets.packageFolders.Keys)[0]).TrimEnd('\', '/') -ine $cache.TrimEnd('\', '/')) {
        throw 'Actual restore must use only the isolated package cache and exact ten-package closure.'
    }
    $binary = Join-Path $directory 'bin/Release/net10.0'
    foreach ($package in $receipt.Packages) {
        if (!$assets.libraries.ContainsKey($package.Id + '/' + $package.Version) -or
            (Get-FileHash -LiteralPath (Join-Path $binary ($package.Id + '.dll')) -Algorithm SHA256).Hash -cne $package.RuntimeSha256) {
            throw 'Stage assets or actual runtime DLL differ from the selected packages.'
        }
    }
    foreach ($relative in @('analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll', 'tools/net10.0/Atelia.DurableGraph.Build.dll', 'build/Atelia.DurableGraph.props', 'build/Atelia.DurableGraph.targets')) {
        $path = Join-Path $cache ('atelia.durablegraph/' + $Version.ToLowerInvariant() + '/' + $relative)
        $hash = Zip-Hash (Join-Path $PackageSource ("Atelia.DurableGraph.$Version.nupkg")) $relative
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $hash) { throw 'Generator/tool/targets cache bytes differ from the actual package.' }
        if ($Name -ceq 'stage-v1') { $receipt.CompilerInputs += [ordered]@{ Entry = $relative; Path = $path; Sha256 = $hash } }
    }
    $stage = [ordered]@{ Name = $Name; Shape = $Shape; HistoryRoot = $History; History = @(History-Images $History)
        Assembly = (Join-Path $binary ($assemblyName + '.dll')); BinaryRoot = $binary; Assets = $assetsPath
        AssetsSha256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash }
    $stage['AssemblySha256'] = (Get-FileHash -LiteralPath $stage.Assembly -Algorithm SHA256).Hash
    $receipt.Stages += $stage
    Save-Receipt
    return $stage
}
function Store-Images {
    @(Get-ChildItem -LiteralPath $database -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($database, $_.FullName); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Consume([string]$Phase, $Stage) {
    $before = if ($Phase -ceq 'verify') { @(Store-Images) } else { @() }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -CommandType Application | Select-Object -First 1).Source)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.WorkingDirectory = $workRoot
    foreach ($argument in @($Stage.Assembly, $Phase, $database, $witness)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $entry = [ordered]@{ Phase = $Phase; Shape = $Stage.Shape; ProcessId = $process.Id; Passed = $false }
    $receipt.Processes += $entry
    try {
        if (!$process.WaitForExit($ProcessTimeoutSeconds * 1000)) { throw "Phase $Phase timed out." }
        if ($process.ExitCode -ne 0 -or $stdout.GetAwaiter().GetResult().Trim() -cne ("DevelopmentSchemaConsumer:$Phase" + ':Passed')) { throw "Phase $Phase failed." }
        $evidencePath = Join-Path $workRoot ($Phase + '.evidence.json')
        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        if ($evidence.Protocol -cne 'DG.DevelopmentSchemaConsumer.v1' -or $evidence.Passed -isnot [bool] -or !$evidence.Passed -or
            $evidence.Phase -cne $Phase -or $evidence.Shape -cne $Stage.Shape -or $evidence.ProcessId -ne $process.Id -or
            $evidence.HostAssembly.Name -cne $assemblyName -or $evidence.HostAssembly.Path -ine $Stage.Assembly -or
            $evidence.HostAssembly.Sha256 -cne $Stage.AssemblySha256 -or @($evidence.LoadedAssemblies).Count -ne 10) { throw 'Wrong actual consumer process, assembly, shape or protocol.' }
        foreach ($package in $receipt.Packages) {
            $loaded = @($evidence.LoadedAssemblies | Where-Object { $_.Name -ceq $package.Id })
            if ($loaded.Count -ne 1 -or $loaded[0].Path -ine (Join-Path $Stage.BinaryRoot ($package.Id + '.dll')) -or
                $loaded[0].Sha256 -cne $package.RuntimeSha256) { throw 'Actual loaded assemblies do not match the selected ten packages.' }
        }
        $checks = switch ($Phase) {
            'seed' { @('FixedV1Baseline', 'RingAndAlias') }
            'draft-a' { @('BaselineIdentityImported', 'V1UpgradeWithOff', 'PrivateColdValues', 'SharedV1Unchanged', 'CompleteFramesPreserved') }
            'draft-b' { @('BaselineIdentityImported', 'V1UpgradeWithOff', 'PrivateColdValues', 'SharedV1Unchanged', 'CompleteFramesPreserved') }
            'final' { @('WinnerBOnlyPromoted', 'BaselineIdentityImported', 'FinalFromV1', 'ColdSharedContinuation', 'CompleteFramesPreserved') }
            'verify' { @('FixedBaselinePreserved', 'WinnerBColdValues', 'IncompatibleDraftRejected', 'ReadOnlyFilesUnchanged', 'CompleteFramesPreserved') }
        }
        foreach ($check in $checks) { if ($evidence.Observations.$check -isnot [bool] -or !$evidence.Observations.$check) { throw "Missing actual $check evidence." } }
        if ($Phase -ceq 'verify' -and ($before | ConvertTo-Json -Compress) -cne (@(Store-Images) | ConvertTo-Json -Compress)) { throw 'Readonly PID changed the database file set/bytes.' }
        $entry['Evidence'] = $evidencePath; $entry['EvidenceSha256'] = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
        $entry['Observations'] = $evidence.Observations; $entry['LoadedAssemblies'] = $evidence.LoadedAssemblies; $entry.Passed = $true
        Write-Host $stdout.GetAwaiter().GetResult().Trim()
    } finally {
        if (!$process.HasExited) { $process.Kill($true); if (!$process.WaitForExit(30000)) { throw 'Cannot stop timed-out phase.' } }
        $entry['ExitCode'] = $process.ExitCode
        [IO.File]::WriteAllText((Join-Path $workRoot ($Phase + '.stdout.log')), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $workRoot ($Phase + '.stderr.log')), $stderr.GetAwaiter().GetResult())
        $process.Dispose(); Save-Receipt
    }
}

Save-Receipt
try {
    if (!$PackProvenancePath) { $PackProvenancePath = Join-Path $PackageSource ("dg-source-provenance.$Version.json") }
    $proof = Get-Content -LiteralPath $PackProvenancePath -Raw | ConvertFrom-Json
    if ($proof.DgVersion -cne $Version -or !$proof.ProductionInputsMatchHead -or $proof.IncludesUncommittedProductionSource) { throw 'Reuse a controlled committed-production package candidate.' }
    $receipt['PackProvenance'] = [ordered]@{ Path = $PackProvenancePath; Sha256 = (Get-FileHash -LiteralPath $PackProvenancePath -Algorithm SHA256).Hash; SourceHead = $proof.RepositoryHead }
    $ids = @('Atelia.Binary', 'Atelia.Data', 'Atelia.DurableGraph', 'Atelia.DurableGraph.Persistence', 'Atelia.DurableGraph.Serialization',
        'Atelia.DurableGraph.Storage', 'Atelia.FrameStore', 'Atelia.Primitives', 'Atelia.Rbf', 'Atelia.HeadStore')
    if (@(Get-ChildItem -LiteralPath $PackageSource -File -Filter '*.nupkg').Count -ne 10) { throw 'Require the existing exact complete ten-package feed.' }
    foreach ($id in $ids) {
        $packageVersion = if ($id.StartsWith('Atelia.DurableGraph')) { $Version } else { $storageVersion }
        $packagePath = Join-Path $PackageSource ("$id.$packageVersion.nupkg")
        $sha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
        $zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $nuspec = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
            if ($nuspec.Count -ne 1) { throw 'Require one actual nuspec.' }
            $reader = [IO.StreamReader]::new($nuspec[0].Open())
            try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
            $commit = if ($id.StartsWith('Atelia.DurableGraph')) { $proof.RepositoryHead } else { $storageRevision }
            if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $id -or
                $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $packageVersion -or
                $metadata.SelectSingleNode('*[local-name()="repository"]').GetAttribute('commit') -ine $commit) { throw 'Wrong package identity/version/source revision.' }
        } finally { $zip.Dispose() }
        if ($id.StartsWith('Atelia.DurableGraph')) {
            $recorded = @($proof.Packages | Where-Object { $_.Id -ceq $id })
            if ($recorded.Count -ne 1 -or $recorded[0].Sha256 -cne $sha) { throw 'DG package bytes differ from their controlled pack provenance.' }
        }
        $receipt.Packages += [ordered]@{ Id = $id; Version = $packageVersion; File = $packagePath; Sha256 = $sha; RuntimeSha256 = (Zip-Hash $packagePath ('lib/net10.0/' + $id + '.dll')) }
    }
    $config = Join-Path $workRoot 'probe.nuget.config'
    $feedXml = [Security.SecurityElement]::Escape($PackageSource)
    [IO.File]::WriteAllText($config, "<configuration><packageSources><clear/><add key='probe' value='$feedXml'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources><packageSourceMapping><clear/><packageSource key='probe'><package pattern='Atelia.*'/></packageSource><packageSource key='nuget.org'><package pattern='*'/></packageSource></packageSourceMapping></configuration>")
    Push-Location $workRoot
    try {
        $v1 = Compile-Stage 'stage-v1' 'V1' $developmentHistory @('Publish', 'Verify')
        $frozenV1 = @(History-Images $developmentHistory)
        if ($frozenV1.Count -ne 1 -or $frozenV1[0].File -notmatch '\.V1\.') { throw 'The frozen development history must contain exactly V1.' }
        Consume 'seed' $v1
        $a = Compile-Stage 'stage-a' 'A' $developmentHistory @('Off')
        Require-History $developmentHistory $frozenV1
        Consume 'draft-a' $a
        $b = Compile-Stage 'stage-b' 'B' $developmentHistory @('Off')
        Require-History $developmentHistory $frozenV1
        Consume 'draft-b' $b
        [IO.Directory]::CreateDirectory($releaseHistory) | Out-Null
        Copy-Item -LiteralPath (Join-Path $developmentHistory $frozenV1[0].File) -Destination (Join-Path $releaseHistory $frozenV1[0].File)
        $final = Compile-Stage 'stage-final-b' 'B' $releaseHistory @('Publish', 'Verify')
        $released = @(History-Images $releaseHistory)
        if ($released.Count -ne 2 -or @($released | Where-Object { $_.File -match '\.V2\.' }).Count -ne 1 -or
            ($released | Where-Object { $_.File -ceq $frozenV1[0].File }).Sha256 -cne $frozenV1[0].Sha256) { throw 'Release must preserve V1 and accept exactly final V2B.' }
        Consume 'final' $final
        Compile-Stage 'stage-rejected-a' 'A' $releaseHistory @('Off') $true
        Require-History $releaseHistory $released
        Consume 'verify' $final
        Require-History $developmentHistory $frozenV1
        Require-History $releaseHistory $released
    } finally { Pop-Location }
    if (@($receipt.Processes.ProcessId | Select-Object -Unique).Count -ne 5 -or
        @($receipt.Commands | Where-Object { $_.Arguments -contains '-t:Rebuild' -and !$_.ExpectedRejection }).Count -ne 6 -or
        @($receipt.Commands | Where-Object { $_.ExpectedRejection }).Count -ne 1) { throw 'Require five fresh PIDs, six successful Rebuilds and one DG0015 rejection.' }
    foreach ($input in $receipt.Inputs) { if ((Get-FileHash -LiteralPath $input.Path -Algorithm SHA256).Hash -cne $input.Sha256) { throw 'A frozen consumer input changed.' } }
    foreach ($package in $receipt.Packages) { if ((Get-FileHash -LiteralPath $package.File -Algorithm SHA256).Hash -cne $package.Sha256) { throw 'A feed package changed.' } }
    $receipt['DevelopmentHistory'] = $frozenV1; $receipt['ReleaseHistory'] = $released
    $receipt.Passed = $true
    Write-Host "Development Schema package workflow passed: $workRoot"
} catch {
    $receipt.Failure = $_.Exception.ToString()
    throw
} finally {
    $receipt['CompletedUtc'] = [DateTime]::UtcNow.ToString('O')
    Save-Receipt
}
