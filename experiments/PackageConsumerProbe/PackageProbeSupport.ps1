# Shared preparation only. Each runner continues to own its domain assertions.

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    if ($Arguments[0] -eq 'restore') {
        # Use the runner's explicit complete feed(s), independently of source
        # mappings inherited from parent NuGet configurations.
        $config = Join-Path $workRoot 'probe.nuget.config'
        if (!(Test-Path -LiteralPath $config)) {
            [void](New-Item -ItemType Directory -Force -Path $workRoot)
            '<configuration><packageSources><clear /></packageSources><packageSourceMapping><clear /></packageSourceMapping></configuration>' |
                Set-Content -LiteralPath $config -Encoding utf8NoBOM
        }
        $Arguments += @('--configfile', $config)
    }
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Prepare-DurableGraphProbeFeed {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $OutputDirectory,
        [Parameter(Mandatory)][string] $Version
    )
    if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.nupkg' -File).Count -ne 0) {
        throw 'Prepare the probe in a fresh feed; existing package versions are never overwritten.'
    }
    $storage = & (Join-Path $RepositoryRoot 'eng/Prepare-Storage.ps1')
    if (@($storage.Packages | Where-Object { $_.version -ceq $Version }).Count -ne 0) {
        throw 'Use a DurableGraph (G) version distinct from every selected storage package version.'
    }
    [void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
    foreach ($package in $storage.Packages) {
        Copy-Item -LiteralPath (Join-Path $storage.PackageSource $package.File) -Destination $OutputDirectory
    }
    foreach ($project in @(
        'src/DurableGraph.Serialization/DurableGraph.Serialization.csproj',
        'src/DurableGraph/DurableGraph.csproj',
        'src/DurableGraph.Storage/DurableGraph.Storage.csproj',
        'src/DurableGraph.Persistence/DurableGraph.Persistence.csproj'
    )) {
        # The five storage projects are never repacked with this downstream G.
        & dotnet pack (Join-Path $RepositoryRoot $project) --configuration Release --output $OutputDirectory "-p:PackageVersion=$Version" `
            "-p:StoragePackageVersion=$($storage.Version)" "-p:StorageRbfSegmentStorePackageVersion=$($storage.RbfSegmentStoreVersion)" `
            "-p:StorageEventJournalPackageVersion=$($storage.EventJournalVersion)" -p:UseStorageSources=false
        if ($LASTEXITCODE -ne 0) { throw "Packing $project failed with exit code $LASTEXITCODE." }
    }
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.nupkg' -File).Count -ne 9) { throw 'Expected five selected storage packages and four DurableGraph packages at G.' }
    Write-Host "Probe feed: storage base=$($storage.Version), segments=$($storage.RbfSegmentStoreVersion), journal=$($storage.EventJournalVersion); DurableGraph G=$Version; $OutputDirectory"
}
