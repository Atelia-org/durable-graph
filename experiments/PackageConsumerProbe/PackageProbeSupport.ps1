# Shared preparation only. Each runner continues to own its domain assertions.

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    if ($Arguments[0] -eq 'restore') {
        # Atelia candidates come only from the explicit probe/model feeds. Third-party
        # dependencies (including Binary's K4os.Compression.LZ4) come from nuget.org.
        # Turn --source arguments into a complete config so source mapping remains effective.
        $feeds = [Collections.Generic.List[string]]::new()
        $forwarded = [Collections.Generic.List[string]]::new()
        for ($index = 0; $index -lt $Arguments.Count; $index++) {
            if ($Arguments[$index] -eq '--source') {
                $index++
                if ($index -ge $Arguments.Count) { throw 'Missing --source value.' }
                $source = $Arguments[$index]
                if ($source -ne 'https://api.nuget.org/v3/index.json' -and !$feeds.Contains($source)) { $feeds.Add($source) }
            } else { $forwarded.Add($Arguments[$index]) }
        }
        $config = Join-Path $workRoot 'probe.nuget.config'
        [void](New-Item -ItemType Directory -Force -Path $workRoot)
        $sources = [Text.StringBuilder]::new('<packageSources><clear/>')
        $mapping = [Text.StringBuilder]::new('<packageSourceMapping><clear/>')
        for ($index = 0; $index -lt $feeds.Count; $index++) {
            $escaped = [Security.SecurityElement]::Escape($feeds[$index])
            [void]$sources.Append("<add key='probe$index' value='$escaped'/>")
            [void]$mapping.Append("<packageSource key='probe$index'><package pattern='Atelia.*'/></packageSource>")
        }
        [void]$sources.Append("<add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources>")
        [void]$mapping.Append("<packageSource key='nuget.org'><package pattern='*'/></packageSource></packageSourceMapping>")
        "<configuration>$sources$mapping</configuration>" | Set-Content -LiteralPath $config -Encoding utf8NoBOM
        $Arguments = @($forwarded.ToArray()) + @('--configfile', $config, '--disable-parallel')
    }
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Prepare-DurableGraphProbeFeed {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $OutputDirectory,
        [Parameter(Mandatory)][string] $Version,
        [string] $StoragePackageSource = $env:DG_STORAGE_PACKAGE_SOURCE
    )
    if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.nupkg' -File).Count -ne 0) {
        throw 'Prepare the probe in a fresh feed; existing package versions are never overwritten.'
    }
    if ([string]::IsNullOrWhiteSpace($StoragePackageSource)) {
        $storage = & (Join-Path $RepositoryRoot 'eng/Prepare-Storage.ps1')
    } else {
        $storage = & (Join-Path $RepositoryRoot 'eng/Prepare-Storage.ps1') -StoragePackageSource $StoragePackageSource
    }
    if (@($storage.Packages | Where-Object { $_.version -ceq $Version }).Count -ne 0) {
        throw 'Use a DurableGraph (G) version distinct from every selected storage package version.'
    }
    [void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
    foreach ($package in $storage.Packages) {
        Copy-Item -LiteralPath (Join-Path $storage.PackageSource $package.File) -Destination $OutputDirectory
    }
    # Packing remains package-mode. This isolated complete feed supplies the exact upstream
    # candidates; nuget.org supplies unrelated SDK/Roslyn dependencies.
    $packConfig = Join-Path (Split-Path -Parent $OutputDirectory) 'pack.nuget.config'
    $feedXml = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($OutputDirectory))
    "<configuration><packageSources><clear/><add key='probe' value='$feedXml'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources><packageSourceMapping><clear/><packageSource key='probe'><package pattern='Atelia.*'/></packageSource><packageSource key='nuget.org'><package pattern='*'/></packageSource></packageSourceMapping></configuration>" |
        Set-Content -LiteralPath $packConfig -Encoding utf8NoBOM
    foreach ($project in @(
        'src/DurableGraph.Serialization/DurableGraph.Serialization.csproj',
        'src/DurableGraph/DurableGraph.csproj',
        'src/DurableGraph.Storage/DurableGraph.Storage.csproj',
        'src/DurableGraph.Persistence/DurableGraph.Persistence.csproj'
    )) {
        # The six storage projects are never repacked with this downstream G.
        & dotnet pack (Join-Path $RepositoryRoot $project) --configuration Release --output $OutputDirectory "-p:PackageVersion=$Version" `
            "-p:StoragePackageVersion=$($storage.Version)" "-p:RestoreConfigFile=$packConfig" -p:UseStorageSources=false -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw "Packing $project failed with exit code $LASTEXITCODE." }
    }
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.nupkg' -File).Count -ne 10) { throw 'Expected six selected storage packages and four DurableGraph packages at G.' }
    Write-Host "Probe feed: storage=$($storage.Version) at $($storage.Revision); DurableGraph G=$Version; $OutputDirectory"
}
