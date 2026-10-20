[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $PackageSource,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Version,
    [string] $WorkDirectory
)

$ErrorActionPreference = 'Stop'
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$sourceRoot = Join-Path $PSScriptRoot 'ApplicationRecipesConsumer'
if (!$WorkDirectory) {
    $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dg-application-recipes-' + [Guid]::NewGuid().ToString('N'))
}
$workRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory).TrimEnd('\', '/')
foreach ($boundary in @($repositoryRoot, $PackageSource, (Join-Path $repositoryRoot '../atelia-storage'))) {
    $boundary = [IO.Path]::GetFullPath($boundary).TrimEnd('\', '/')
    if ($workRoot.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $workRoot.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use a fresh work directory outside the repositories and input feed.'
    }
}
if (Test-Path -LiteralPath $workRoot) { throw 'Use a new directory; previous outputs and evidence are retained.' }
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$consumerRoot = Join-Path $workRoot 'consumer'
$project = Join-Path $consumerRoot 'ApplicationRecipesConsumer.csproj'
$cache = Join-Path $workRoot 'packages'
$history = Join-Path $workRoot 'history'
$database = Join-Path $workRoot 'database'
$navigation = Join-Path $workRoot 'navigation.json'
$receipt = [ordered]@{
    Protocol = 'DG.ApplicationRecipesProbe.v1'; Passed = $false
    DgVersion = $Version; DgSourceRevision = $null; ConsumerGitHead = $null; ConsumerGitStatus = @()
    PackageSource = $PackageSource; WorkDirectory = $workRoot; StartedUtc = [DateTime]::UtcNow.ToString('O')
    Inputs = @(); Packages = @(); Commands = @(); RuntimeAssemblies = @(); History = @(); Failure = $null
}
function Save-Receipt {
    $receipt | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workRoot 'result.json') -Encoding utf8NoBOM
}
function Zip-Hash([string] $Package, [string] $Entry) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $item = $zip.GetEntry($Entry)
        if (!$item) { throw "Missing package entry: $Entry" }
        $stream = $item.Open()
        try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
}
function History-Images {
    @(Get-ChildItem -LiteralPath $history -File -Filter '*.dgschema' | Sort-Object Name | ForEach-Object {
        [ordered]@{ File = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
}
function Execute([string] $Name, [string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $workRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['NUGET_PACKAGES'] = $cache
    $start.Environment['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $started = [DateTime]::UtcNow.ToString('O')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = !$process.WaitForExit(180000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        $log = Join-Path $workRoot ($Name + '.log')
        [IO.File]::WriteAllText($log, $output + $errorOutput, [Text.UTF8Encoding]::new($false))
        $receipt.Commands += [ordered]@{
            Name = $Name; Executable = 'dotnet'; Arguments = $Arguments; WorkingDirectory = $workRoot; Cache = $cache
            ProcessId = $process.Id; StartedUtc = $started; CompletedUtc = [DateTime]::UtcNow.ToString('O')
            ExitCode = $process.ExitCode; TimedOut = $timedOut; Log = $log; Sha256 = (Get-FileHash -LiteralPath $log).Hash
        }
        Save-Receipt
        if ($timedOut -or $process.ExitCode -ne 0) { throw "dotnet $Name failed; see $log" }
        [pscustomobject]@{ Output = $output; ErrorOutput = $errorOutput }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

Save-Receipt
try {
    # Record the consumer revision separately from the immutable package source revision.
    $receipt.ConsumerGitHead = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record consumer Git HEAD.' }
    $receipt.ConsumerGitStatus = @(& git -C $repositoryRoot status --porcelain -- 'experiments/PackageConsumerProbe/ApplicationRecipesConsumer' 'experiments/PackageConsumerProbe/Run-ApplicationRecipesProbe.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record consumer working-tree status.' }
    [IO.Directory]::CreateDirectory($consumerRoot) | Out-Null
    $sources = @(Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object Extension -in '.cs', '.csproj' | Sort-Object Name)
    if (@($sources | Where-Object Extension -eq '.csproj').Count -ne 1 -or
        @($sources | Where-Object Extension -eq '.cs').Count -eq 0) { throw 'Expected one consumer project and its C# sources.' }
    foreach ($source in $sources) {
        $content = Get-Content -LiteralPath $source.FullName -Raw
        if ($content -match '(?i)<\s*(Import|ProjectReference|Analyzer|AdditionalFiles)\b|InternalsVisibleTo') {
            throw "Forbidden manual wiring or friend declaration in $($source.Name)."
        }
        Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $consumerRoot $source.Name)
        $sha = (Get-FileHash -LiteralPath $source.FullName).Hash
        if ((Get-FileHash -LiteralPath (Join-Path $consumerRoot $source.Name)).Hash -ne $sha) { throw 'Consumer source changed during copying.' }
        $receipt.Inputs += [ordered]@{ File = $source.Name; Source = $source.FullName; Sha256 = $sha }
    }
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $assemblyNames = @($projectXml.SelectNodes('/Project/PropertyGroup/AssemblyName'))
    if ($assemblyNames.Count -ne 1 -or $assemblyNames[0].InnerText -cne 'ApplicationRecipesConsumer') {
        throw 'The consumer must explicitly use AssemblyName ApplicationRecipesConsumer, without a friend identity.'
    }
    foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        '<Project />' | Set-Content -LiteralPath (Join-Path $workRoot $file) -Encoding utf8NoBOM
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination (Join-Path $workRoot 'global.json')
    foreach ($file in @($PSCommandPath, (Join-Path $repositoryRoot 'global.json'))) {
        $receipt.Inputs += [ordered]@{ File = [IO.Path]::GetFileName($file); Source = $file; Sha256 = (Get-FileHash -LiteralPath $file).Hash }
    }

    $dgIds = @('Atelia.DurableGraph', 'Atelia.DurableGraph.Persistence', 'Atelia.DurableGraph.Serialization', 'Atelia.DurableGraph.Storage')
    $storageIds = @('Atelia.Binary', 'Atelia.Data', 'Atelia.FrameStore', 'Atelia.HeadStore', 'Atelia.Primitives', 'Atelia.Rbf')
    $packages = @{}
    $feedFiles = @(Get-ChildItem -LiteralPath $PackageSource -File -Filter '*.nupkg')
    if ($feedFiles.Count -notin 10, 11) { throw 'Expected ten Atelia packages, optionally plus the candidate K4os package.' }
    foreach ($file in $feedFiles) {
        $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
        try {
            $specs = @($zip.Entries | Where-Object FullName -like '*.nuspec')
            if ($specs.Count -ne 1) { throw "Expected one nuspec in $($file.Name)." }
            $reader = [IO.StreamReader]::new($specs[0].Open())
            try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $spec.package.metadata
            $id = [string]$metadata.id
            $packageVersion = [string]$metadata.version
            $sourceRevision = [string]$metadata.repository.commit
            if ($id -notin ($dgIds + $storageIds + @('K4os.Compression.LZ4')) -or $packages.ContainsKey($id)) {
                throw "Unexpected or duplicate feed package: $id"
            }
            if ($id -in $dgIds -and $packageVersion -cne $Version) { throw "Wrong G version for $id." }
            if ($id.StartsWith('Atelia.') -and $sourceRevision -notmatch '^[0-9a-fA-F]{40}$') { throw "Missing package source commit: $id" }
            $package = [ordered]@{ Id = $id; Version = $packageVersion; SourceCommit = $sourceRevision
                File = $file.FullName; Sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash }
            $packages[$id] = $package
            $receipt.Packages += $package
        } finally { $zip.Dispose() }
    }
    foreach ($id in ($dgIds + $storageIds)) { if (!$packages.ContainsKey($id)) { throw "Missing feed package: $id" } }
    $dgSources = @($dgIds | ForEach-Object { $packages[$_].SourceCommit } | Select-Object -Unique)
    if ($dgSources.Count -ne 1) { throw 'The four G packages must have one source commit.' }
    $receipt.DgSourceRevision = $dgSources[0]
    # Do not impose G on storage: NuGet resolves the versions declared in the package dependencies.
    $feedXml = [Security.SecurityElement]::Escape($PackageSource)
    $localThirdParty = if ($packages.ContainsKey('K4os.Compression.LZ4')) { "<package pattern='K4os.Compression.LZ4'/>" } else { '' }
    "<configuration><packageSources><clear/><add key='probe' value='$feedXml'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources><packageSourceMapping><clear/><packageSource key='probe'><package pattern='Atelia.*'/>$localThirdParty</packageSource><packageSource key='nuget.org'><package pattern='*'/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>" |
        Set-Content -LiteralPath (Join-Path $workRoot 'NuGet.Config') -Encoding utf8NoBOM
    $properties = @("-p:DurableGraphPackageVersion=$Version", "-p:DurableGraphSchemaHistoryDirectory=$history", '-p:UseStorageSources=false', '-p:UseSharedCompilation=false')
    $null = Execute 'restore' (@('restore', $project, '--configfile', (Join-Path $workRoot 'NuGet.Config'), '--packages', $cache, '--disable-parallel') + $properties)
    $assetsFile = Join-Path $consumerRoot 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Values | Where-Object type -ne 'package').Count) { throw 'Consumer assets must contain packages only.' }
    $folders = @($assets.packageFolders.Keys)
    if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ine $cache) { throw 'Assets must use only the fresh cache.' }
    $ateliaKeys = @($assets.libraries.Keys | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::OrdinalIgnoreCase) })
    if ($ateliaKeys.Count -ne 10) { throw 'Expected all ten Atelia packages in consumer assets.' }
    foreach ($key in $ateliaKeys) {
        $id, $resolvedVersion = $key.Split('/')
        if (!$packages.ContainsKey($id) -or $packages[$id].Version -cne $resolvedVersion) { throw "Wrong restored identity: $key" }
    }
    foreach ($package in $packages.Values) {
        if (!$assets.libraries.ContainsKey("$($package.Id)/$($package.Version)")) { throw "Feed package was not consumed: $($package.Id)" }
        $cached = Join-Path $cache "$($package.Id.ToLowerInvariant())/$($package.Version.ToLowerInvariant())/$($package.Id.ToLowerInvariant()).$($package.Version.ToLowerInvariant()).nupkg"
        if ((Get-FileHash -LiteralPath $cached).Hash -cne $package.Sha256) { throw "Cache differs from feed: $($package.Id)" }
    }
    $receipt['Assets'] = $assetsFile
    $receipt['AssetsSha256'] = (Get-FileHash -LiteralPath $assetsFile).Hash
    $receipt['ResolvedPackages'] = @($assets.libraries.Keys | Sort-Object)
    foreach ($mode in @('Publish', 'Verify')) {
        $arguments = @('build', $project, '-c', 'Release', '--no-restore', '-m:1', '-nr:false', '-v:normal', "-p:DurableGraphSchemaHistoryMode=$mode")
        if ($mode -eq 'Verify') { $arguments += '-t:Rebuild' }
        $build = Execute "build-$($mode.ToLowerInvariant())" ($arguments + $properties)
        $output = ($build.Output + $build.ErrorOutput).Replace('\', '/')
        $generator = (Join-Path $cache "atelia.durablegraph/$($Version.ToLowerInvariant())/analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll").Replace('\', '/')
        $buildTool = (Join-Path $cache "atelia.durablegraph/$($Version.ToLowerInvariant())/tools/net10.0/Atelia.DurableGraph.Build.dll").Replace('\', '/')
        if ($output.IndexOf($generator, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            ($output.IndexOf($buildTool, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
             $output.IndexOf($buildTool.Replace('/tools/', '/build/../tools/'), [StringComparison]::OrdinalIgnoreCase) -lt 0) -or
            $output -notmatch ('(?i)\s' + $mode + '\s+--manifest')) {
            throw "Build $mode did not show the packaged Generator and Build CLI."
        }
        $images = @(History-Images)
        if ($mode -eq 'Publish') {
            if ($images.Count -eq 0) { throw 'Publish produced no accepted history.' }
            $receipt.History = $images
        } elseif (($images | ConvertTo-Json -Compress) -cne ($receipt.History | ConvertTo-Json -Compress)) {
            throw 'Verify changed the accepted history file set or bytes.'
        }
    }
    $binaryRoot = Join-Path $consumerRoot 'bin/Release/net10.0'
    $dlls = @(Get-ChildItem -LiteralPath $binaryRoot -Filter 'Atelia.*.dll' -File)
    if ($dlls.Count -ne 10) { throw 'Expected ten Atelia runtime assemblies.' }
    foreach ($dll in $dlls) {
        $id = $dll.BaseName
        if (!$packages.ContainsKey($id)) { throw "Unexpected runtime assembly: $id" }
        $hash = (Get-FileHash -LiteralPath $dll.FullName).Hash
        if ($hash -cne (Zip-Hash $packages[$id].File "lib/net10.0/$($dll.Name)")) { throw "Runtime output differs from package: $id" }
        $receipt.RuntimeAssemblies += [ordered]@{ Id = $id; Version = $packages[$id].Version; File = $dll.FullName; Sha256 = $hash }
    }
    $assembly = Join-Path $binaryRoot 'ApplicationRecipesConsumer.dll'
    $receipt['ConsumerAssembly'] = [ordered]@{ File = $assembly; Sha256 = (Get-FileHash -LiteralPath $assembly).Hash }
    foreach ($lane in @('seed', 'verify')) {
        $run = Execute $lane @($assembly, $lane, $database, $navigation)
        if ($run.Output.Trim() -cne "ApplicationRecipes:$($lane):Passed" -or ![string]::IsNullOrWhiteSpace($run.ErrorOutput)) {
            throw "Unexpected $lane output; see $lane.log"
        }
    }
    $receipt['Navigation'] = [ordered]@{ File = $navigation; Sha256 = (Get-FileHash -LiteralPath $navigation).Hash }
    $receipt.Passed = $true
    Write-Host "Application recipes package probe passed. Artifacts: $workRoot"
} catch {
    $receipt.Failure = $_.Exception.ToString()
    throw
} finally {
    $receipt['CompletedUtc'] = [DateTime]::UtcNow.ToString('O')
    Save-Receipt
}
