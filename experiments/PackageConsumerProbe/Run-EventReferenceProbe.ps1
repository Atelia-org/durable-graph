[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageSource,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $ExpectedSourceCommit,
    [string] $WorkDirectory
)

$ErrorActionPreference = 'Stop'
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if ($ExpectedSourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExpectedSourceCommit must be the full committed DG source SHA.' }
if (!$WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dg-event-reference-' + [Guid]::NewGuid().ToString('N')) }
$workRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory).TrimEnd('\', '/')
foreach ($boundary in @($repositoryRoot, $PackageSource)) {
    $boundary = [IO.Path]::GetFullPath($boundary).TrimEnd('\', '/')
    if ($workRoot.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $workRoot.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use a fresh work directory outside the repository and input feed.'
    }
}
if (Test-Path -LiteralPath $workRoot) { throw 'Use a new work directory; previous evidence is retained.' }
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$cache = Join-Path $workRoot 'packages'
$history = Join-Path $workRoot 'history'
$generated = Join-Path $workRoot 'generated'
$project = Join-Path $workRoot 'EventReferenceConsumer.csproj'
$binaryRoot = Join-Path $workRoot 'bin/Release/net10.0'
$receipt = [ordered]@{ Passed = $false; Version = $Version; ExpectedSourceCommit = $ExpectedSourceCommit
    PackageSource = $PackageSource; WorkDirectory = $workRoot; Commands = @(); Packages = @()
    CompilerInputs = @(); CompilerConsumption = @(); Lanes = @(); Failure = $null }

function Save-Receipt {
    $receipt | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workRoot 'result.json') -Encoding utf8NoBOM
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
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(300000)) { throw "dotnet $Name timed out after 300 seconds." }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        $log = Join-Path $workRoot ($Name + '.log')
        [IO.File]::WriteAllText($log, $output, [Text.UTF8Encoding]::new($false))
        $receipt.Commands += [ordered]@{ Name = $Name; Arguments = $Arguments; ExitCode = $process.ExitCode
            ProcessId = $process.Id; Log = $log; Sha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash }
        Save-Receipt
        if ($process.ExitCode -ne 0) { throw "dotnet $Name failed; see $log" }
        return $output
    } finally {
        if (!$process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}
function Entry-Hash([string] $Package, [string] $Entry) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $item = $archive.GetEntry($Entry)
        if ($null -eq $item) { throw "Missing package entry '$Entry' in '$Package'." }
        $stream = $item.Open()
        try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
}
function History-Snapshot {
    return @((Get-ChildItem -LiteralPath $history -Filter '*.dgschema' -File | Sort-Object Name) | ForEach-Object {
        "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }) -join "`n"
}
function Require-Consumption([string] $Output, [string] $Mode) {
    $normalized = $Output.Replace('\', '/')
    $runtimeRoot = (Join-Path $cache ('atelia.durablegraph/' + $Version.ToLowerInvariant())).Replace('\', '/')
    $generator = $runtimeRoot + '/analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll'
    $tool = $runtimeRoot + '/tools/net10.0/Atelia.DurableGraph.Build.dll'
    $toolAlternate = $runtimeRoot + '/build/../tools/net10.0/Atelia.DurableGraph.Build.dll'
    if ($normalized.IndexOf($generator, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $normalized -notmatch '(?i)/analyzer:' -or $normalized -notmatch '(?i)CoreCompile') {
        throw "The $Mode build did not show actual use of the packaged Generator."
    }
    if (($normalized.IndexOf($tool, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
         $normalized.IndexOf($toolAlternate, [StringComparison]::OrdinalIgnoreCase) -lt 0) -or
        $normalized -notmatch ('(?i)\s' + [Regex]::Escape($Mode) + '\s+--manifest')) {
        throw "The $Mode build did not execute the packaged history tool."
    }
    $receipt.CompilerConsumption += [ordered]@{ Mode = $Mode; GeneratorPath = $generator; BuildToolPath = $tool; Observed = $true }
}

Save-Receipt
try {
    foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        '<Project />' | Set-Content -LiteralPath (Join-Path $workRoot $file) -Encoding utf8NoBOM
    }
    $sourceRoot = Join-Path $PSScriptRoot 'EventReferenceConsumer'
    foreach ($file in @('EventReferenceConsumer.csproj', 'Models.cs', 'Program.cs')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination (Join-Path $workRoot $file)
    }
    $consumerText = Get-Content -LiteralPath $project -Raw
    foreach ($forbidden in @('<Import', '<ProjectReference', '<Analyzer', '<AdditionalFiles')) {
        if ($consumerText.Contains($forbidden, [StringComparison]::Ordinal)) { throw "Forbidden manual package wiring '$forbidden'." }
    }
    $feedXml = [Security.SecurityElement]::Escape($PackageSource)
    "<configuration><packageSources><clear/><add key='probe' value='$feedXml'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources><packageSourceMapping><clear/><packageSource key='probe'><package pattern='Atelia.*'/></packageSource><packageSource key='nuget.org'><package pattern='*'/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>" |
        Set-Content -LiteralPath (Join-Path $workRoot 'NuGet.Config') -Encoding utf8NoBOM
    $properties = @("-p:DurableGraphPackageVersion=$Version", "-p:DurableGraphSchemaHistoryDirectory=$history", '-p:UseStorageSources=false',
        '-p:UseSharedCompilation=false', '-p:EmitCompilerGeneratedFiles=true', "-p:CompilerGeneratedFilesOutputPath=$generated")
    $null = Execute 'restore' (@('restore', $project, '--configfile', (Join-Path $workRoot 'NuGet.Config'), '--packages', $cache, '--disable-parallel') + $properties)
    $assetsPath = Join-Path $workRoot 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count) { throw 'Consumer assets contain a source ProjectReference.' }
    $folders = @($assets.packageFolders.Keys)
    if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ine $cache.TrimEnd('\', '/')) {
        throw 'Consumer assets did not use only the fresh package cache.'
    }
    $dgIds = @('Atelia.DurableGraph', 'Atelia.DurableGraph.Persistence', 'Atelia.DurableGraph.Serialization', 'Atelia.DurableGraph.Storage')
    $ateliaKeys = @($assets.libraries.Keys | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::Ordinal) })
    if ($ateliaKeys.Count -ne 10) { throw 'Expected the four DG and six storage package closure.' }
    $packageDllHashes = @{}
    foreach ($key in $ateliaKeys) {
        $id, $packageVersion = $key.Split('/')
        if ($dgIds -contains $id -and $packageVersion -cne $Version) { throw "Wrong DG package version: $key" }
        $package = Join-Path $PackageSource ($id + '.' + $packageVersion + '.nupkg')
        $packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        $cachedRoot = Join-Path $cache ($id.ToLowerInvariant() + '/' + $packageVersion.ToLowerInvariant())
        $cachedPackage = Join-Path $cachedRoot ($id.ToLowerInvariant() + '.' + $packageVersion.ToLowerInvariant() + '.nupkg')
        if ((Get-FileHash -LiteralPath $cachedPackage -Algorithm SHA256).Hash -cne $packageHash) { throw "Restored nupkg differs from feed: $key" }
        $archive = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            $nuspec = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
            if ($nuspec.Count -ne 1) { throw "Expected one nuspec: $key" }
            $reader = [IO.StreamReader]::new($nuspec[0].Open())
            try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $sourceCommit = [string]$metadata.package.metadata.repository.commit
        } finally { $archive.Dispose() }
        if ($dgIds -contains $id -and $sourceCommit -ine $ExpectedSourceCommit) { throw "DG package source commit differs from the approved source: $key" }
        $packageDllHashes[$id] = Entry-Hash $package ('lib/net10.0/' + $id + '.dll')
        if ((Get-FileHash -LiteralPath (Join-Path $cachedRoot ('lib/net10.0/' + $id + '.dll')) -Algorithm SHA256).Hash -cne $packageDllHashes[$id]) {
            throw "Restored runtime DLL differs from its nupkg: $key"
        }
        $receipt.Packages += [ordered]@{ Identity = $key; File = $package; Sha256 = $packageHash; SourceCommit = $sourceCommit; RuntimeSha256 = $packageDllHashes[$id] }
    }
    foreach ($id in $dgIds) { if (!$assets.libraries.ContainsKey($id + '/' + $Version)) { throw "Missing DG package: $id" } }
    $runtimePackage = Join-Path $PackageSource ('Atelia.DurableGraph.' + $Version + '.nupkg')
    $runtimeRoot = Join-Path $cache ('atelia.durablegraph/' + $Version.ToLowerInvariant())
    foreach ($entry in @('analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll', 'tools/net10.0/Atelia.DurableGraph.Build.dll',
        'build/Atelia.DurableGraph.props', 'build/Atelia.DurableGraph.targets')) {
        $path = Join-Path $runtimeRoot $entry
        $hash = Entry-Hash $runtimePackage $entry
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $hash) { throw "Restored compiler input differs from package: $entry" }
        $receipt.CompilerInputs += [ordered]@{ Entry = $entry; Path = $path; Sha256 = $hash }
    }
    $build = @('build', $project, '-c', 'Release', '--no-restore', '-t:Rebuild', '-m:1', '-nr:false', '-v:normal') + $properties
    $publish = Execute 'publish' ($build + '-p:DurableGraphSchemaHistoryMode=Publish')
    Require-Consumption $publish 'publish'
    if (@(Get-ChildItem -LiteralPath $history -Filter '*.dgschema' -File).Count -ne 2) { throw 'Expected exactly the two consumer-owned schema histories.' }
    $accepted = History-Snapshot
    $verify = Execute 'verify-build' ($build + '-p:DurableGraphSchemaHistoryMode=Verify')
    Require-Consumption $verify 'verify'
    if ((History-Snapshot) -cne $accepted) { throw 'Verify changed accepted schema history.' }
    $references = @(Get-ChildItem -LiteralPath $generated -Recurse -Filter 'DurableGraphSchemaHistoryReferences.g.cs' -File)
    $referenceText = if ($references.Count -eq 1) { Get-Content -LiteralPath $references[0].FullName -Raw } else { '' }
    $eventSchema = 'Atelia.DurableGraph.EventRef'
    $encodedSchema = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($eventSchema))
    if ($references.Count -ne 1 -or (!$referenceText.Contains($eventSchema, [StringComparison]::Ordinal) -and
        !$referenceText.Contains($encodedSchema, [StringComparison]::Ordinal))) {
        throw 'The consumer Generator did not import the package EventRef schema history.'
    }
    foreach ($id in $packageDllHashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $binaryRoot ($id + '.dll')) -Algorithm SHA256).Hash -cne $packageDllHashes[$id]) {
            throw "Consumer output runtime DLL differs from package: $id"
        }
    }
    $assembly = Join-Path $binaryRoot 'Atelia.EventReferenceConsumer.dll'
    $database = Join-Path $workRoot 'database'
    foreach ($lane in @('seed', 'verify')) {
        $evidencePath = Join-Path $workRoot ($lane + '.json')
        $actual = Execute $lane @($assembly, $lane, $database, $evidencePath)
        if ($actual -notmatch ('EventReference:' + $lane + ':Passed:\d+')) { throw "Consumer lane did not report success: $lane" }
        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        if (!$evidence.Passed -or $evidence.Lane -cne $lane) { throw "Invalid consumer evidence: $lane" }
        foreach ($loaded in $evidence.LoadedAssemblies) {
            if (!$packageDllHashes.ContainsKey($loaded.Name) -or
                [IO.Path]::GetFullPath($loaded.Path) -ine (Join-Path $binaryRoot ($loaded.Name + '.dll')) -or
                $loaded.Sha256 -cne $packageDllHashes[$loaded.Name] -or
                (Get-FileHash -LiteralPath $loaded.Path -Algorithm SHA256).Hash -cne $loaded.Sha256) {
                throw "Consumer loaded a runtime outside the selected package bytes: $($loaded.Name)"
            }
        }
        foreach ($id in $dgIds) {
            if (@($evidence.LoadedAssemblies | Where-Object { $_.Name -ceq $id }).Count -ne 1) { throw "Consumer did not actually load $id" }
        }
        $receipt.Lanes += [ordered]@{ Name = $lane; Evidence = $evidencePath; Sha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
            ProcessId = $evidence.ProcessId; Checks = $evidence.Checks; LoadedAssemblies = $evidence.LoadedAssemblies }
    }
    if ($receipt.Lanes[0].ProcessId -eq $receipt.Lanes[1].ProcessId) { throw 'Cold verification reused the seed process.' }
    foreach ($input in $receipt.CompilerInputs) {
        if ((Get-FileHash -LiteralPath $input.Path -Algorithm SHA256).Hash -cne $input.Sha256) { throw 'A compiler input changed during the probe.' }
    }
    $receipt['Assets'] = $assetsPath
    $receipt['AssetsSha256'] = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash
    $receipt['HistorySnapshot'] = $accepted
    $receipt['ReferenceManifest'] = $references[0].FullName
    $receipt.Passed = $true
    Write-Host "EventReference package probe passed. Artifacts: $workRoot"
} catch {
    $receipt.Failure = $_.Exception.ToString()
    throw
} finally { Save-Receipt }
