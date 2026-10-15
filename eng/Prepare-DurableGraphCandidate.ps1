[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')][string] $Version,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $StoragePackageSource = $env:DG_STORAGE_PACKAGE_SOURCE
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'Choose a new candidate directory. Existing directories and package versions are never overwritten.'
}

function Get-Example([string] $Language, [string] $Marker) {
    $pattern = '(?ms)^```' + [regex]::Escape($Language) + '\r?\n(.*?)^```'
    $blocks = @([regex]::Matches($readme, $pattern) | Where-Object { $_.Groups[1].Value.Contains($Marker) })
    if ($blocks.Count -ne 1) { throw "Expected one README $Language block containing '$Marker'." }
    return $blocks[0].Groups[1].Value
}

function Read-Package([string] $Path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($entries.Count -ne 1) { throw "Expected one nuspec: $Path" }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $entries[0].Open()
        try {
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $document = [Xml.XmlDocument]::new()
                $document.XmlResolver = $null
                $document.Load($reader)
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
        $metadata = $document.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if (!$metadata) { throw "Missing package metadata: $Path" }
        $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
        return [pscustomobject]@{
            Id = $metadata.SelectSingleNode('*[local-name()="id"]').InnerText
            Version = $metadata.SelectSingleNode('*[local-name()="version"]').InnerText
            Revision = $(if ($repository) { $repository.GetAttribute('commit') } else { '' })
            Metadata = $metadata
            Signed = $null -ne $archive.GetEntry('.signature.p7s')
            Path = $Path
        }
    }
    finally { $archive.Dispose() }
}

# README owns the sample, complete consumer configuration, and shortest commands.
# Fail before packing if those sources have drifted or become ambiguous.
$readme = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'README.md')
$projectText = Get-Example 'xml' '<Project Sdk='
$consumerConfig = Get-Example 'xml' '<add key="candidate" value="feed"'
$commands = Get-Example 'powershell' 'dotnet restore QuickStart/QuickStart.csproj --configfile ./NuGet.Config'
$models = Get-Example 'csharp' 'public partial class Character'
$program = Get-Example 'csharp' 'string path = Path.GetFullPath(args[0]);'
$projectXml = [xml]$projectText
if (@($projectXml.SelectNodes('/Project/PropertyGroup/DurableGraphPackageVersion')).Count -ne 0) {
    throw 'The README project must leave the candidate G unset; the exporter writes it exactly once.'
}
$propertyGroups = @($projectXml.SelectNodes('/Project/PropertyGroup'))
if ($propertyGroups.Count -eq 0) { throw 'README project needs a PropertyGroup.' }
$references = @($projectXml.SelectNodes('/Project/ItemGroup/PackageReference'))
if ($references.Count -ne 2 -or @($references | Where-Object {
    $_.GetAttribute('Version') -cne '$(DurableGraphPackageVersion)' -or
    $_.GetAttribute('Include') -cnotin @('Atelia.DurableGraph', 'Atelia.DurableGraph.Persistence')
}).Count -ne 0) { throw 'README must share one G property between its two ordinary package references.' }
$versionProperty = $projectXml.CreateElement('DurableGraphPackageVersion')
$versionProperty.InnerText = $Version
[void]$propertyGroups[0].AppendChild($versionProperty)
$configXml = [xml]$consumerConfig
$sources = @($configXml.SelectNodes('/configuration/packageSources/add'))
$mappings = @($configXml.SelectNodes('/configuration/packageSourceMapping/packageSource'))
if ($sources.Count -ne 1 -or $sources[0].GetAttribute('key') -cne 'candidate' -or
    $sources[0].GetAttribute('value') -cne 'feed' -or
    !$configXml.SelectSingleNode('/configuration/packageSources/clear') -or
    !$configXml.SelectSingleNode('/configuration/packageSourceMapping/clear') -or
    !$configXml.SelectSingleNode('/configuration/fallbackPackageFolders/clear') -or
    $mappings.Count -ne 1 -or $mappings[0].GetAttribute('key') -cne 'candidate' -or
    @($mappings[0].SelectNodes('package')).Count -ne 1 -or
    $mappings[0].SelectSingleNode('package').GetAttribute('pattern') -cne '*') {
    throw 'README must provide a complete local-only relative feed configuration.'
}
$expectedCommands = @(
    'dotnet restore QuickStart/QuickStart.csproj --configfile ./NuGet.Config',
    'dotnet run --project QuickStart/QuickStart.csproj --no-restore -- ./quickstart-world',
    'dotnet run --project QuickStart/QuickStart.csproj --no-restore -- ./quickstart-world'
)
$actualCommands = @($commands -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and !$_.StartsWith('#') })
if (($actualCommands -join "`n") -cne ($expectedCommands -join "`n")) {
    throw 'README shortest commands changed. Keep exporter and original-command acceptance aligned.'
}

$sourceRevision = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceRevision -cnotmatch '^[0-9a-f]{40}$') { throw 'Cannot identify DurableGraph source HEAD.' }
$sourceStatus = @(& git -C $repositoryRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect DurableGraph source status.' }
$sourceWasClean = $sourceStatus.Count -eq 0
$pin = [xml](Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'StorageDependency.props'))
$storageVersions = @($pin.SelectNodes('/Project/PropertyGroup/StoragePackageVersion') | ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$' })
$storageRevisions = @($pin.SelectNodes('/Project/PropertyGroup/StorageSourceRevision') | ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ -match '^[0-9a-fA-F]{40}$' })
if ($storageVersions.Count -ne 1 -or $storageRevisions.Count -ne 1) { throw 'Expected one explicit storage version and revision pin.' }
$storageVersion = $storageVersions[0]
$storageRevision = $storageRevisions[0]
if ($Version -ceq $storageVersion) { throw 'DurableGraph G and storage S must have distinct versions.' }

# Keep producer restoration and packing outside the portable consumer directory.
# Never use the caller's global package cache to supply a supposedly complete feed.
$workRoot = Join-Path $repositoryRoot ('.artifacts/candidate-preparation/' + $Version + '.' + [Guid]::NewGuid().ToString('N'))
$packFeed = Join-Path $workRoot 'pack-feed'
$packageCache = Join-Path $workRoot 'packages'
[void][IO.Directory]::CreateDirectory($workRoot)
. (Join-Path $repositoryRoot 'experiments/PackageConsumerProbe/PackageProbeSupport.ps1')
$previousPackageCache = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = $packageCache
    Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $packFeed -Version $Version -StoragePackageSource $StoragePackageSource
    $sdk = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the packing SDK.' }

    $packages = @(Get-ChildItem -LiteralPath $packFeed -Filter '*.nupkg' -File | ForEach-Object { Read-Package $_.FullName })
    if ($packages.Count -ne 10 -or @($packages.Id | Sort-Object -Unique).Count -ne 10) { throw 'Expected ten distinct Atelia package identities.' }
    foreach ($package in $packages) {
        if ($package.Id -clike 'Atelia.DurableGraph*') {
            if ($package.Version -cne $Version -or $package.Revision -ine $sourceRevision) { throw "Unexpected G identity or source: $($package.Id)" }
        } elseif ($package.Id -clike 'Atelia.*') {
            if ($package.Version -cne $storageVersion -or $package.Revision -ine $storageRevision) { throw "Unexpected S identity or source: $($package.Id)" }
        } else { throw "Unexpected producer package: $($package.Id)" }
    }
    if (@($packages | Where-Object { $_.Id -clike 'Atelia.DurableGraph*' }).Count -ne 4) { throw 'Expected four G and six S packages.' }

    # This is the present net10.0/no-RID QuickStart closure, not a dependency solver.
    # Take K4os's exact version from the selected Binary package rather than a new pin.
    $binary = @($packages | Where-Object { $_.Id -ceq 'Atelia.Binary' })
    if ($binary.Count -ne 1) { throw 'Expected the selected Atelia.Binary package.' }
    $thirdParty = @($binary[0].Metadata.SelectNodes('*[local-name()="dependencies"]/*[local-name()="group"][@targetFramework="net10.0"]/*[local-name()="dependency"][@id="K4os.Compression.LZ4"]'))
    if ($thirdParty.Count -ne 1 -or $thirdParty[0].GetAttribute('version') -cnotmatch '^\[(\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?)\]$') {
        throw 'Expected one exact net10.0 K4os.Compression.LZ4 dependency in Binary; review the sample closure before exporting changed dependencies.'
    }
    $thirdPartyVersion = $Matches[1]
    $thirdPartyId = $thirdParty[0].GetAttribute('id')
    $selectedVersions = @{}
    foreach ($package in $packages) { $selectedVersions.Add($package.Id, $package.Version) }
    $selectedVersions.Add($thirdPartyId, $thirdPartyVersion)
    foreach ($package in $packages) {
        foreach ($dependency in $package.Metadata.SelectNodes('*[local-name()="dependencies"]/*[local-name()="group"][@targetFramework="net10.0"]/*[local-name()="dependency"]')) {
            $id = $dependency.GetAttribute('id')
            $range = $dependency.GetAttribute('version')
            if (!$selectedVersions.ContainsKey($id) -or $range -cnotin @($selectedVersions[$id], "[$($selectedVersions[$id])]")) {
                throw "The present sample closure does not cover $($package.Id) -> $id $range. Review the changed dependencies instead of exporting an incomplete feed."
            }
        }
    }
    $lowerId = $thirdPartyId.ToLowerInvariant()
    $lowerVersion = $thirdPartyVersion.ToLowerInvariant()
    $thirdPartyPath = Join-Path $packageCache "$lowerId/$lowerVersion/$lowerId.$lowerVersion.nupkg"
    if (!(Test-Path -LiteralPath $thirdPartyPath -PathType Leaf)) {
        $thirdPartyPath = Join-Path $workRoot "$thirdPartyId.$thirdPartyVersion.nupkg"
        $url = "https://api.nuget.org/v3-flatcontainer/$lowerId/$lowerVersion/$lowerId.$lowerVersion.nupkg"
        Invoke-WebRequest -Uri $url -OutFile $thirdPartyPath
    }
    $originalPackage = Read-Package $thirdPartyPath
    if ($originalPackage.Id -cne $thirdPartyId -or $originalPackage.Version -cne $thirdPartyVersion -or !$originalPackage.Signed -or
        !$originalPackage.Metadata.SelectSingleNode('*[local-name()="license" or local-name()="licenseUrl"]')) {
        throw 'Expected the original signed nuget.org K4os package with its license metadata. No third-party repacking is supported.'
    }

    # Do not overwrite or clean any existing destination, including a partial prior run.
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Candidate output appeared during preparation; choose a fresh directory.' }
    [void](New-Item -ItemType Directory -Path $OutputDirectory)
    $consumerFeed = Join-Path $OutputDirectory 'feed'
    $projectRoot = Join-Path $OutputDirectory 'QuickStart'
    [void][IO.Directory]::CreateDirectory($consumerFeed)
    [void][IO.Directory]::CreateDirectory($projectRoot)
    foreach ($package in $packages) {
        [IO.File]::Copy($package.Path, (Join-Path $consumerFeed ([IO.Path]::GetFileName($package.Path))), $false)
    }
    [IO.File]::Copy($thirdPartyPath, (Join-Path $consumerFeed "$thirdPartyId.$thirdPartyVersion.nupkg"), $false)
    $utf8 = [Text.UTF8Encoding]::new($false)
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = $utf8
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $true
    $settings.NewLineChars = "`n"
    $writer = [Xml.XmlWriter]::Create((Join-Path $projectRoot 'QuickStart.csproj'), $settings)
    try { $projectXml.Save($writer) }
    finally { $writer.Dispose() }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'NuGet.Config'), $consumerConfig, $utf8)
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Models.cs'), $models, $utf8)
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), $program, $utf8)

    [string[]]$filenames = @(Get-ChildItem -LiteralPath $consumerFeed -Filter '*.nupkg' -File | ForEach-Object { $_.Name })
    [Array]::Sort($filenames, [StringComparer]::OrdinalIgnoreCase)
    $hashes = foreach ($filename in $filenames) {
        (Get-FileHash -LiteralPath (Join-Path $consumerFeed $filename) -Algorithm SHA256).Hash.ToLowerInvariant() + '  feed/' + $filename
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'packages.sha256'), ($hashes -join "`n") + "`n", $utf8)
    $sourceDescription = if ($sourceWasClean) { 'clean working tree at preparation start' } else { 'DIRTY working tree at preparation start; package commit metadata does not prove the modified source equals that commit' }
    $deliveryReadme = @(
        '# DurableGraph candidate QuickStart',
        '',
        "DurableGraph G: $Version (four packages). Storage S: $storageVersion (six packages).",
        "DurableGraph source HEAD / nuspec commit: $sourceRevision; $sourceDescription.",
        "Storage source / verified nuspec commit: $storageRevision. Storage provenance is checked by eng/Prepare-Storage.ps1 before packing.",
        "Original third-party package: $thirdPartyId $thirdPartyVersion from nuget.org. Its signature entry and license metadata remain in the original nupkg; this export does not perform signature trust-chain validation.",
        '',
        "Prepared using SDK $sdk. The supplied example targets net10.0, framework-dependent, with no explicit RID. It requires an already installed compatible SDK/runtime; the current acceptance baseline is SDK 10.0.201 (Roslyn 5.3.0). Other SDK feature bands have not been qualified.",
        'The local feed covers this supplied example only. It does not include SDK installation, arbitrary RID/self-contained publishing, additional application dependencies, or the DurableGraph repository test/build dependencies. A local package source does not suppress every possible SDK/certificate/audit network operation.',
        '',
        'The QuickStart saves State directly; no Event type or preceding Event is required. Creation saves S0, and the two independent runs save S1 then S2 (Hp=99 then Hp=98). Move this whole directory together and run the following source-README commands in this directory:',
        '',
        '```powershell',
        $commands.TrimEnd(),
        '```',
        '',
        'NuGet.Config selects only the relative feed/ directory and clears inherited source mapping and fallback package folders. It does not suppress arbitrary parent MSBuild settings. Use an ordinary independent application directory; do not add source ProjectReferences, Analyzer paths, or manual Imports.',
        'The G property is written once in QuickStart.csproj and used by both top-level PackageReferences. Storage is selected through the original package dependencies; consumers do not maintain a second S pin.',
        'For an existing application, use these packages/configuration and pin this G in its model projects; retaining the sample program is unnecessary. Each model project directly references Atelia.DurableGraph.',
        'packages.sha256 records package bytes for copy-integrity troubleshooting; it is not a version selector or a source-authenticity trust chain. Commit your model source and generated DurableGraphSchemaHistory files, and keep quickstart-world outside source control.',
        'This is a local candidate, not a claim of public publication. Never replace package bytes under an existing ID/version, in this directory or another feed; new content needs a new G. Rerunning the sample performs another business operation and is not transparent recovery/retry.',
        ''
    ) -join "`n"
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'README.md'), $deliveryReadme, $utf8)
    Write-Host "Portable candidate ready: $OutputDirectory"
    Write-Host "G=$Version; S=$storageVersion; original packages=$($filenames.Count); producer evidence=$workRoot"
}
finally { $env:NUGET_PACKAGES = $previousPackageCache }
