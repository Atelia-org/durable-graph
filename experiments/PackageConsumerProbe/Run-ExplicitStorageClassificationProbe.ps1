[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageSource,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$DgSourceRevision,
    [string]$WorkDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
[xml]$pins = Get-Content -LiteralPath (Join-Path $repositoryRoot 'eng/StorageDependency.props') -Raw
$storageVersion = $pins.Project.PropertyGroup.StoragePackageVersion
$storageRevision = $pins.Project.PropertyGroup.StorageSourceRevision
if ($Version -ceq $storageVersion) { throw 'Use independent DG G and storage S versions.' }
$sourceRoot = Join-Path $PSScriptRoot 'ExplicitStorageClassificationConsumer'
$projectName = 'ExplicitStorageClassificationConsumer.csproj'
$assemblyName = 'DurableGraph.ExplicitStorageClassificationConsumer'
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object { $_.Extension -in '.cs', '.csproj' })
$projectText = Get-Content -LiteralPath (Join-Path $sourceRoot $projectName) -Raw
foreach ($forbidden in @('<Import', '<ProjectReference', '<Analyzer', '<AdditionalFiles', 'InternalsVisibleTo')) {
    if ($projectText.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) { throw "Forbidden consumer wiring: $forbidden" }
}
if (!$WorkDirectory) {
    $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dg-explicit-storage-' + [Guid]::NewGuid().ToString('N'))
}
$workRoot = [IO.Path]::GetFullPath($WorkDirectory).TrimEnd('\', '/')
foreach ($boundary in @($repositoryRoot, [IO.Path]::GetFullPath((Join-Path $repositoryRoot '../atelia-storage')))) {
    $boundary = $boundary.TrimEnd('\', '/')
    if ($workRoot.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $workRoot.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use a fresh work directory outside the DG and storage repositories.'
    }
}
if (Test-Path -LiteralPath $workRoot) { throw 'Use a fresh directory; retain previous receipts, history and databases.' }
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$cache = Join-Path $workRoot 'packages'
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination (Join-Path $workRoot 'global.json')
foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    '<Project />' | Set-Content -LiteralPath (Join-Path $workRoot $name) -Encoding utf8NoBOM
}
$receipt = [ordered]@{
    Protocol = 'DG.ExplicitStorageClassificationProbe.v1'; Passed = $false
    DgVersion = $Version; DgSourceRevision = $DgSourceRevision; StorageVersion = $storageVersion; StorageRevision = $storageRevision
    PackageSource = $PackageSource; WorkDirectory = $workRoot; Packages = @(); Lanes = @(); Failure = $null
}
function Save-Receipt {
    $receipt | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath (Join-Path $workRoot 'result.json') -Encoding utf8NoBOM
}
function Images([string]$Directory, [string]$Filter = '*') {
    if (Test-Path -LiteralPath $Directory) {
        @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter $Filter | Sort-Object FullName | ForEach-Object {
            [ordered]@{ File = [IO.Path]::GetRelativePath($Directory, $_.FullName); Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
        })
    }
}
function Require-Images([string]$Directory, $Expected, [string]$Filter = '*') {
    if ((@($Expected) | ConvertTo-Json -Compress) -cne (@(Images $Directory $Filter) | ConvertTo-Json -Compress)) {
        throw "File set or bytes changed: $Directory"
    }
}
function Zip-Hash([string]$Package, [string]$Entry) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $item = $zip.GetEntry($Entry)
        if (!$item) { throw "Missing package entry $Entry" }
        $stream = $item.Open()
        try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
}
function Build-Consumer([string]$Lane, [string]$Shape, [string]$Mode, [string[]]$Properties, [bool]$Reject = $false) {
    $log = Join-Path $workRoot "$Lane-$Shape-$Mode.log"
    & dotnet build $project -c Release --no-restore -t:Rebuild -m:1 -nr:false -v:normal -p:UseSharedCompilation=false `
        "-p:ShapeStage=$Shape" "-p:DurableGraphSchemaHistoryMode=$Mode" @Properties *> $log
    $exit = $LASTEXITCODE
    $output = (Get-Content -LiteralPath $log -Raw).Replace('\', '/')
    $packagedGenerator = (Join-Path $cache "atelia.durablegraph/$($Version.ToLowerInvariant())/analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll").Replace('\', '/')
    if ($output.IndexOf($packagedGenerator, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or $output -notmatch '(?i)/analyzer:') {
        throw 'The actual compiler did not consume the packaged Generator.'
    }
    $laneReceipt.Builds += [ordered]@{ Shape = $Shape; Mode = $Mode; ExitCode = $exit; Log = $log; Sha256 = (Get-FileHash -LiteralPath $log).Hash }
    Save-Receipt
    Write-Host "$Lane $Shape $Mode Rebuild: $exit; $log"
    if ($Reject) {
        if ($exit -eq 0) { throw 'The H01 package fixture unexpectedly compiled.' }
        $sarifPath = Join-Path $laneRoot 'obj/diagnostics.sarif'
        $sarif = Get-Content -LiteralPath $sarifPath -Raw | ConvertFrom-Json
        $errors = @($sarif.runs.results | Where-Object level -ceq 'error')
        if ($errors.Count -ne 1 -or $errors[0].ruleId -cne 'DG0024') { throw 'Expected exactly DG0024, not a compiler/generator crash or another error.' }
        $location = $errors[0].locations[0].physicalLocation
        $invalidText = Get-Content -LiteralPath (Join-Path $laneRoot 'Model.Invalid.cs') -Raw
        $attributeOffset = $invalidText.IndexOf('DurableField(1)', [StringComparison]::Ordinal)
        $prefix = $invalidText.Substring(0, $attributeOffset)
        $line = ($prefix -split "`n").Count
        $column = $attributeOffset - $prefix.LastIndexOf("`n")
        $sourcePath = [Uri]::UnescapeDataString($location.artifactLocation.uri).Replace('\', '/')
        if (!$sourcePath.EndsWith('/Model.Invalid.cs', [StringComparison]::Ordinal) -or
            $location.region.startLine -ne $line -or $location.region.startColumn -ne $column) {
            throw 'DG0024 must point at the actual DurableField attribute in Model.Invalid.cs.'
        }
        $frozenSarif = Join-Path $laneRoot 'Invalid.sarif'
        Copy-Item -LiteralPath $sarifPath -Destination $frozenSarif
        $laneReceipt.Diagnostic = [ordered]@{ Rule = 'DG0024'; File = $sourcePath; Line = $line; Column = $column
            Sarif = $frozenSarif; Sha256 = (Get-FileHash -LiteralPath $frozenSarif).Hash }
    } else {
        if ($exit -ne 0) { throw "Build failed; see $log" }
        $tool = (Join-Path $cache "atelia.durablegraph/$($Version.ToLowerInvariant())/tools/net10.0/Atelia.DurableGraph.Build.dll").Replace('\', '/')
        if ($output -notmatch ('(?i)\s' + $Mode.ToLowerInvariant() + '\s+--manifest') -or
            ($output.IndexOf($tool, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
             $output.IndexOf($tool.Replace('/tools/', '/build/../tools/'), [StringComparison]::OrdinalIgnoreCase) -lt 0)) {
            throw 'The actual Rebuild did not execute the selected packaged Build CLI.'
        }
    }
    $generated = Join-Path $laneRoot 'obj/Release/net10.0/generated'
    $stateFile = if ($Lane -ceq 'Default') { 'DurableStates.g.cs' } else { 'DurableGenericStates.g.cs' }
    $otherStateFile = if ($Lane -ceq 'Default') { 'DurableGenericStates.g.cs' } else { 'DurableStates.g.cs' }
    $states = @(Get-ChildItem -LiteralPath $generated -Recurse -File -Filter $stateFile)
    $manifests = @(Get-ChildItem -LiteralPath $generated -Recurse -File -Filter 'DurableGraphSchemaHistoryCandidates.g.cs')
    if ($states.Count -ne 1 -or @(Get-ChildItem -LiteralPath $generated -Recurse -File -Filter $otherStateFile).Count -ne 0 -or
        $manifests.Count -ne 1) { throw 'The legal control did not exercise exactly the intended generation route.' }
    $stateText = Get-Content -LiteralPath $states[0].FullName -Raw
    $manifest = Get-Content -LiteralPath $manifests[0].FullName -Raw
    $controlId = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('probe.explicit.control'))
    $subjectId = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('probe.explicit.subject'))
    if (!$stateText.Contains('LegalControl', [StringComparison]::Ordinal) -or !$manifest.Contains('// schema-id-base64:' + $controlId, [StringComparison]::Ordinal)) {
        throw 'The independent legal control lost its generated projection or candidate.'
    }
    if ($Reject -and ($stateText.Contains('Subject', [StringComparison]::Ordinal) -or $manifest.Contains('// schema-id-base64:' + $subjectId, [StringComparison]::Ordinal))) {
        throw 'The rejected H01 declaration retained its own current projection/binding or candidate.'
    }
    $frozenGenerated = Join-Path $laneRoot "$Shape-$Mode-generated"
    [IO.Directory]::CreateDirectory($frozenGenerated) | Out-Null
    $frozenStates = Join-Path $frozenGenerated $stateFile
    $frozenManifest = Join-Path $frozenGenerated $manifests[0].Name
    Copy-Item -LiteralPath $states[0].FullName -Destination $frozenStates
    Copy-Item -LiteralPath $manifests[0].FullName -Destination $frozenManifest
    $laneReceipt.Generated += [ordered]@{ Shape = $Shape; Mode = $Mode; StateFile = $frozenStates; ManifestFile = $frozenManifest
        StateSha256 = (Get-FileHash -LiteralPath $frozenStates).Hash; ManifestSha256 = (Get-FileHash -LiteralPath $frozenManifest).Hash }
}
function Consume([string]$Phase) {
    $evidencePath = Join-Path $laneRoot "$Phase.evidence.json"
    $stdout = Join-Path $laneRoot "$Phase.stdout.log"
    $stderr = Join-Path $laneRoot "$Phase.stderr.log"
    & dotnet $assembly $Phase $database $evidencePath > $stdout 2> $stderr
    if ($LASTEXITCODE -ne 0 -or (Get-Content -LiteralPath $stdout -Raw).Trim() -cne "ExplicitStorageClassificationConsumer:$Phase`:Passed") {
        throw "Consumer $Phase failed; see $stdout and $stderr"
    }
    $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    if ($evidence.Protocol -cne 'DG.ExplicitStorageClassificationConsumer.v1' -or !$evidence.Passed -or $evidence.Phase -cne $Phase -or
        $evidence.Route -cne $lane -or $evidence.HostAssembly.Name -cne $assemblyName -or $evidence.HostAssembly.Path -ine $assembly -or
        $evidence.HostAssembly.Sha256 -cne (Get-FileHash -LiteralPath $assembly).Hash -or @($evidence.LoadedAssemblies).Count -ne 10) {
        throw 'Wrong actual consumer process, identity or route.'
    }
    foreach ($package in $receipt.Packages) {
        $loaded = @($evidence.LoadedAssemblies | Where-Object Name -ceq $package.Id)
        if ($loaded.Count -ne 1 -or $loaded[0].Path -ine (Join-Path $binary ($package.Id + '.dll')) -or $loaded[0].Sha256 -cne $package.RuntimeSha256) {
            throw 'Actual loaded package bytes differ from the selected feed.'
        }
    }
    $laneReceipt.Processes += [ordered]@{ Phase = $Phase; ProcessId = $evidence.ProcessId; Evidence = $evidencePath; Sha256 = (Get-FileHash -LiteralPath $evidencePath).Hash }
    Save-Receipt
}

Save-Receipt
try {
    $ids = @('Atelia.Binary', 'Atelia.Data', 'Atelia.DurableGraph', 'Atelia.DurableGraph.Persistence', 'Atelia.DurableGraph.Serialization',
        'Atelia.DurableGraph.Storage', 'Atelia.FrameStore', 'Atelia.Primitives', 'Atelia.Rbf', 'Atelia.VersionStore')
    if (@(Get-ChildItem -LiteralPath $PackageSource -File -Filter '*.nupkg').Count -ne 10) { throw 'Require the complete ten-package feed.' }
    foreach ($id in $ids) {
        $packageVersion = if ($id.StartsWith('Atelia.DurableGraph')) { $Version } else { $storageVersion }
        $commit = if ($id.StartsWith('Atelia.DurableGraph')) { $DgSourceRevision } else { $storageRevision }
        $package = Join-Path $PackageSource "$id.$packageVersion.nupkg"
        $zip = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            $entry = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
            if ($entry.Count -ne 1) { throw 'Require one nuspec per package.' }
            $reader = [IO.StreamReader]::new($entry[0].Open())
            try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
            if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $id -or
                $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $packageVersion -or
                $metadata.SelectSingleNode('*[local-name()="repository"]').GetAttribute('commit') -ine $commit) { throw 'Wrong package identity, version or source commit.' }
        } finally { $zip.Dispose() }
        $receipt.Packages += [ordered]@{ Id = $id; Version = $packageVersion; File = $package; Sha256 = (Get-FileHash -LiteralPath $package).Hash
            RuntimeSha256 = (Zip-Hash $package "lib/net10.0/$id.dll") }
    }
    foreach ($lane in @('Default', 'Family')) {
        $laneRoot = Join-Path $workRoot $lane
        [IO.Directory]::CreateDirectory($laneRoot) | Out-Null
        foreach ($source in $sources) { Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $laneRoot $source.Name) }
        $project = Join-Path $laneRoot $projectName
        $history = Join-Path $laneRoot 'history'
        $database = Join-Path $laneRoot 'database'
        $binary = Join-Path $laneRoot 'bin/Release/net10.0'
        $assembly = Join-Path $binary "$assemblyName.dll"
        $laneReceipt = [ordered]@{ Route = $lane; Builds = @(); Generated = @(); Diagnostic = $null; Processes = @() }
        $receipt.Lanes += $laneReceipt
        $properties = @("-p:DurableGraphPackageVersion=$Version", "-p:DurableGraphSchemaHistoryDirectory=$history", '-p:UseStorageSources=false')
        if ($lane -ceq 'Family') { $properties += '-p:DurableGraphGenerateDefinitions=true' }
        Invoke-DotNet (@('restore', $project, '--source', $PackageSource, '--packages', $cache) + $properties) *> (Join-Path $workRoot "$lane-restore.log")
        $before = @(Images $history '*.dgschema')
        Build-Consumer $lane 'Invalid' 'Publish' $properties $true
        Require-Images $history $before '*.dgschema'
        if ((Test-Path -LiteralPath $assembly) -or @($before).Count -ne 0) { throw 'Negative stage must have no runnable leftover DLL or accepted history.' }
        Build-Consumer $lane 'Corrected' 'Publish' $properties
        $accepted = @(Images $history '*.dgschema')
        if ($accepted.Count -ne 2) { throw 'The corrected subject and independent control must each publish one V1 history.' }
        Build-Consumer $lane 'Corrected' 'Verify' $properties
        Require-Images $history $accepted '*.dgschema'
        $assets = Get-Content -LiteralPath (Join-Path $laneRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        if (@($assets.libraries.Values | Where-Object type -ceq 'project').Count -ne 0 -or
            @($assets.libraries.Keys | Where-Object { $_.StartsWith('Atelia.') }).Count -ne 10 -or
            @($assets.packageFolders.Keys).Count -ne 1 -or [IO.Path]::GetFullPath(@($assets.packageFolders.Keys)[0]).TrimEnd('\', '/') -ine $cache) {
            throw 'Restore must use the isolated package cache and the exact ten-package closure, without project assets.'
        }
        foreach ($package in $receipt.Packages) {
            if (!$assets.libraries.ContainsKey($package.Id + '/' + $package.Version) -or
                (Get-FileHash -LiteralPath (Join-Path $binary ($package.Id + '.dll'))).Hash -cne $package.RuntimeSha256) { throw 'Assets/output differ from the selected packages.' }
        }
        foreach ($relative in @('analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll', 'tools/net10.0/Atelia.DurableGraph.Build.dll',
            'build/Atelia.DurableGraph.props', 'build/Atelia.DurableGraph.targets')) {
            $cached = Join-Path $cache "atelia.durablegraph/$($Version.ToLowerInvariant())/$relative"
            if ((Get-FileHash -LiteralPath $cached).Hash -cne (Zip-Hash (Join-Path $PackageSource "Atelia.DurableGraph.$Version.nupkg") $relative)) {
                throw 'Actual cached Generator/tool/props/targets differ from the package.'
            }
        }
        Consume 'write'
        $databaseBefore = @(Images $database)
        Consume 'read'
        Require-Images $database $databaseBefore
        Require-Images $history $accepted '*.dgschema'
        if (@($laneReceipt.Processes.ProcessId | Select-Object -Unique).Count -ne 2) { throw 'Write/read must run in separate cold processes.' }
        $laneReceipt['AcceptedHistory'] = $accepted
        Save-Receipt
    }
    foreach ($package in $receipt.Packages) {
        if ((Get-FileHash -LiteralPath $package.File).Hash -cne $package.Sha256) { throw 'A selected feed package changed during the run.' }
    }
    $receipt.Passed = $true
    Write-Host "Explicit storage classification package probe passed: $workRoot"
} catch {
    $receipt.Failure = $_.Exception.ToString()
    throw
} finally { Save-Receipt }
