[CmdletBinding()]
param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pin = [xml](Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'StorageDependency.props'))
function Read-PublishedPin([string] $Name, [string] $Pattern) {
    # Read the explicit public defaults, not the MSBuild expressions implementing dev overrides.
    $values = @($pin.SelectNodes("/Project/PropertyGroup/$Name") | ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ -match $Pattern })
    if ($values.Count -ne 1) { throw "Expected one explicit published value for $Name in StorageDependency.props." }
    return $values[0]
}
$versionPattern = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$'
$revisionPattern = '^[0-9a-fA-F]{40}$'
$version = Read-PublishedPin 'StoragePackageVersion' $versionPattern
$revision = Read-PublishedPin 'StorageSourceRevision' $revisionPattern
$segmentVersion = Read-PublishedPin 'StorageRbfSegmentStorePackageVersion' $versionPattern
$segmentRevision = Read-PublishedPin 'StorageRbfSegmentStoreSourceRevision' $revisionPattern
$journalVersion = Read-PublishedPin 'StorageEventJournalPackageVersion' $versionPattern
$journalRevision = Read-PublishedPin 'StorageEventJournalSourceRevision' $revisionPattern
$pinRepositoryUrl = $pin.SelectSingleNode('/Project/PropertyGroup/StorageRepositoryUrl').InnerText.Trim()
if (!$OutputDirectory) {
    # A new public pin gets a new feed; existing same-version package bytes are never overwritten.
    $OutputDirectory = Join-Path $repositoryRoot ".artifacts/storage-feed/base-$version-segments-$segmentVersion-journal-$journalVersion"
}
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$packagePins = @(
    [pscustomobject]@{ Id = 'Atelia.Primitives'; Version = $version; Revision = $revision },
    [pscustomobject]@{ Id = 'Atelia.Data'; Version = $version; Revision = $revision },
    [pscustomobject]@{ Id = 'Atelia.Rbf'; Version = $version; Revision = $revision },
    [pscustomobject]@{ Id = 'Atelia.RbfSegmentStore'; Version = $segmentVersion; Revision = $segmentRevision },
    [pscustomobject]@{ Id = 'Atelia.EventJournal'; Version = $journalVersion; Revision = $journalRevision }
)
$manifestPath = Join-Path $OutputDirectory 'manifest.published.json'
$expectedRepository = $pinRepositoryUrl.TrimEnd('/') -replace '\.git$', ''

function Get-PackageUrl($Package) {
    $lowerId = $Package.Id.ToLowerInvariant()
    $lowerVersion = $Package.Version.ToLowerInvariant()
    return "https://api.nuget.org/v3-flatcontainer/$lowerId/$lowerVersion/$lowerId.$lowerVersion.nupkg"
}

function Assert-PublishedPackage([string] $Path, $Package) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($entries.Count -ne 1) { throw "Expected one nuspec in $Path" }
        # Presence is checked here. This is not full signature trust-chain verification.
        if (!$archive.GetEntry('.signature.p7s')) { throw "Expected a signed nuget.org package: $Path" }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $entries[0].Open()
        try {
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $nuspec = [Xml.XmlDocument]::new()
                $nuspec.XmlResolver = $null
                $nuspec.Load($reader)
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
        $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if (!$metadata) { throw "Missing package metadata: $Path" }
        $actualId = $metadata.SelectSingleNode('*[local-name()="id"]').InnerText
        $actualVersion = $metadata.SelectSingleNode('*[local-name()="version"]').InnerText
        $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
        if ($actualId -cne $Package.Id -or $actualVersion -cne $Package.Version) { throw "Unexpected package identity: $Path" }
        if (!$repository -or $repository.GetAttribute('commit') -ine $Package.Revision) { throw "Package source commit differs from the pin: $Path" }
        $actualRepository = $repository.GetAttribute('url').TrimEnd('/') -replace '\.git$', ''
        if ($actualRepository -ine $expectedRepository) { throw "Package source repository differs from the pin: $Path" }
    }
    finally { $archive.Dispose() }
}

function Read-VerifiedManifest {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 3 -or $manifest.source -cne 'nuget.org') {
        throw "Expected per-package published nuget.org provenance: $manifestPath. Local or single-version receipts cannot be reused."
    }
    $manifestRepository = ([string]$manifest.repositoryUrl).TrimEnd('/') -replace '\.git$', ''
    if ($manifestRepository -ine $expectedRepository) { throw "Storage manifest names a different repository: $manifestPath" }
    if (@($manifest.packages).Count -ne $packagePins.Count) { throw 'Expected exactly five published storage packages in the manifest.' }
    foreach ($package in $packagePins) {
        $recorded = @($manifest.packages | Where-Object { $_.id -ceq $package.Id })
        if ($recorded.Count -ne 1 -or $recorded[0].file -cne "$($package.Id).$($package.Version).nupkg" -or
            $recorded[0].version -cne $package.Version -or $recorded[0].sourceRevision -ine $package.Revision -or
            $recorded[0].url -cne (Get-PackageUrl $package)) { throw "Unexpected storage manifest entry for $($package.Id)." }
        $path = Join-Path $OutputDirectory $recorded[0].file
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing storage package: $path" }
        Assert-PublishedPackage $path $package
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $recorded[0].sha256) { throw "Storage package bytes changed: $path" }
    }
    return $manifest
}

function New-FeedResult($Manifest) {
    # Version/Revision are retained for callers needing the base trio; Packages is the complete provenance.
    return [pscustomobject]@{
        PackageSource = $OutputDirectory; Version = $version; Revision = $revision
        RbfSegmentStoreVersion = $segmentVersion; EventJournalVersion = $journalVersion
        Packages = $Manifest.packages
    }
}

if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Read-VerifiedManifest
    Write-Host "Published storage packages ready: base=$version, segments=$segmentVersion, journal=$journalVersion in $OutputDirectory"
    return New-FeedResult $manifest
}

foreach ($package in $packagePins) {
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory "$($package.Id).$($package.Version).nupkg")) {
        throw "Refusing to overwrite existing $($package.Id)/$($package.Version) without published provenance. Use a fresh output directory."
    }
}

$temporaryRoot = Join-Path $repositoryRoot '.artifacts'
[void][IO.Directory]::CreateDirectory($temporaryRoot)
$downloadDirectory = Join-Path $temporaryRoot ("storage-download-" + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($downloadDirectory)
try {
    $packages = foreach ($package in $packagePins) {
        $file = "$($package.Id).$($package.Version).nupkg"
        $path = Join-Path $downloadDirectory $file
        $url = Get-PackageUrl $package
        Write-Host "Downloading $($package.Id)/$($package.Version) from nuget.org"
        Invoke-WebRequest -Uri $url -OutFile $path
        Assert-PublishedPackage $path $package
        [pscustomobject]@{
            id = $package.Id; version = $package.Version; sourceRevision = $package.Revision
            file = $file; url = $url; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 3
        source = 'nuget.org'
        repositoryUrl = $pinRepositoryUrl
        packages = @($packages)
    }
    $temporaryManifest = Join-Path $downloadDirectory 'manifest.published.json'
    [IO.File]::WriteAllText($temporaryManifest, ($manifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    # Only publish the feed after all five downloads and checks succeed. Move never
    # overwrites an existing file; the manifest is the final completion marker.
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    foreach ($package in $packages) {
        [IO.File]::Move((Join-Path $downloadDirectory $package.file), (Join-Path $OutputDirectory $package.file))
    }
    [IO.File]::Move($temporaryManifest, $manifestPath)
}
finally {
    # Only this invocation's flat staging directory is owned by this script.
    foreach ($temporaryFile in [IO.Directory]::GetFiles($downloadDirectory)) { [IO.File]::Delete($temporaryFile) }
    [IO.Directory]::Delete($downloadDirectory, $false)
}
$manifest = Read-VerifiedManifest
Write-Host "Published storage packages prepared: base=$version, segments=$segmentVersion, journal=$journalVersion in $OutputDirectory"
New-FeedResult $manifest
