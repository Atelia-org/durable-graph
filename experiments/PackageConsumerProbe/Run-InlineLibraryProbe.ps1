[CmdletBinding()]
param(
    [Alias("FeedPath")][string] $PackageSource,
    [Alias("PackageVersion")][string] $Version
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw "Supply both -PackageSource and -Version, or neither for a self-contained run."
}

. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
function Read-History {
    param([string] $Directory, [int] $ExpectedCount, [hashtable] $Accepted)
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter *.dgschema -File)
    if ($files.Count -ne $ExpectedCount) { throw "Expected $ExpectedCount history files in '$Directory', found $($files.Count)." }
    foreach ($name in $Accepted.Keys) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Accepted[$name].Hash -or
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ne $Accepted[$name].Bytes) {
            throw "Previously accepted history '$name' changed or disappeared."
        }
    }
    foreach ($file in $files) {
        if (-not (Get-Content -LiteralPath $file.FullName -Raw).StartsWith("// durable-graph-schema-history:9`n", [StringComparison]::Ordinal)) {
            throw "Expected canonical history v9."
        }
        $Accepted[$file.Name] = @{
            Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            Bytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName))
        }
    }
}

function Assert-OwnedHistory {
    param([string] $Directory, [string[]] $OwnedIds)
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter *.dgschema -File) {
        $content = Get-Content -LiteralPath $file.FullName -Raw
        # Ownership is checked from the canonical declaration, not mentions in dependency expressions.
        $matched = $false
        foreach ($id in $OwnedIds) {
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($id))
            if ($content.Contains("// schema-id-base64:$encoded`n", [StringComparison]::Ordinal)) { $matched = $true }
        }
        if (-not $matched) { throw "History '$($file.Name)' does not belong to this project; imported history was published." }
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$runStamp = "$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/inline-library-$runStamp"
$packageCache = Join-Path $workRoot "packages"
$modelFeed = Join-Path $workRoot "model-feed"
$database = Join-Path $workRoot "database"
New-Item -ItemType Directory -Path $packageCache, $modelFeed | Out-Null
$projects = @{}
$accepted = @{ LibraryB = @{}; LibraryA = @{}; AppModel = @{} }
$ownedIds = @{ LibraryB = @("IBLeaf", "IBPoint"); LibraryA = @("IACoordinate", "IAPair"); AppModel = @("IWorld") }
foreach ($name in @("LibraryB", "LibraryA", "AppModel", "Host")) {
    $project = Join-Path $PSScriptRoot "InlineLibraryConsumer/$name/$name.csproj"
    $source = Get-Content -LiteralPath $project -Raw
    foreach ($forbidden in @("<Import", "<ProjectReference", "<Analyzer", "<AdditionalFiles", "<NoWarn")) {
        if ($source.Contains($forbidden, [StringComparison]::Ordinal)) { throw "$name contains forbidden manual wiring '$forbidden'." }
    }
    $projects[$name] = $project
}
Push-Location $repositoryRoot
try {
    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        $PackageSource = Join-Path $workRoot "feed"
        New-Item -ItemType Directory -Path $PackageSource | Out-Null
        $Version = "0.0.0-inline-library-e2e.$runStamp"
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version
        if (@(Get-ChildItem -LiteralPath $PackageSource -Filter *.nupkg -File).Count -ne 10) { throw "Expected ten runtime dependency packages." }
    }
    foreach ($generation in @(1, 2)) {
        $modelVersion = "0.0.0-inline-library-v$generation.$runStamp"
        foreach ($name in @("LibraryB", "LibraryA", "AppModel", "Host")) {
            $history = Join-Path $workRoot "$name-history"
            # Each generation has empty intermediates: no stale generated manifest may supply dependencies.
            $intermediate = (Join-Path $workRoot "$name-v$generation-obj") + [IO.Path]::DirectorySeparatorChar
            $output = (Join-Path $workRoot "$name-v$generation-bin") + [IO.Path]::DirectorySeparatorChar
            $properties = @(
                "-p:DurableGraphPackageVersion=$Version", "-p:ModelPackageVersion=$modelVersion", "-p:PackageVersion=$modelVersion",
                "-p:HistoryVersion=$generation", "-p:DurableGraphSchemaHistoryDirectory=$history",
                "-p:BaseIntermediateOutputPath=$intermediate", "-p:BaseOutputPath=$output"
            )
            Invoke-DotNet (@("restore", $projects[$name], "--source", $PackageSource, "--source", $modelFeed, "--packages", $packageCache) + $properties)
            Invoke-DotNet (@("build", $projects[$name], "--no-restore") + $properties)
            if ($name -eq "Host") {
                if ((Test-Path -LiteralPath $history) -and @(Get-ChildItem -LiteralPath $history -Filter *.dgschema -File).Count -ne 0) {
                    throw "Pure Host must not own model history."
                }
                $hostAssembly = Join-Path $output "Debug/net10.0/Atelia.InlineHost.dll"
                continue
            }
            $counts = if ($generation -eq 1) { @{ LibraryB = 2; LibraryA = 2; AppModel = 1 } } else { @{ LibraryB = 4; LibraryA = 3; AppModel = 2 } }
            Read-History $history $counts[$name] $accepted[$name]
            Assert-OwnedHistory $history $ownedIds[$name]
            Invoke-DotNet (@("clean", $projects[$name]) + $properties)
            Invoke-DotNet (@("build", $projects[$name], "--no-restore", "-p:DurableGraphSchemaHistoryMode=Verify") + $properties)
            Read-History $history $counts[$name] $accepted[$name]
            Invoke-DotNet (@("pack", $projects[$name], "--no-build", "--configuration", "Debug", "--output", $modelFeed) + $properties)
            $package = Join-Path $modelFeed "Atelia.Inline$name.$modelVersion.nupkg"
            $zip = [IO.Compression.ZipFile]::OpenRead($package)
            try {
                foreach ($kind in @("ref", "lib")) {
                    if ($null -eq $zip.GetEntry("$kind/net10.0/Atelia.Inline$name.dll")) { throw "$name package lacks $kind assembly." }
                }
            }
            finally { $zip.Dispose() }
            # SDK resolve assets must use references for compilation, implementations for execution.
            if ($name -ne "LibraryB") {
                $dependency = if ($name -eq "LibraryA") { "LibraryB" } else { "LibraryA" }
                $assets = Get-Content -LiteralPath (Join-Path $intermediate "project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
                $target = @($assets.targets.Values)[0]
                $entry = $target["Atelia.Inline$dependency/$modelVersion"]
                if (-not $entry.compile.ContainsKey("ref/net10.0/Atelia.Inline$dependency.dll") -or
                    -not $entry.runtime.ContainsKey("lib/net10.0/Atelia.Inline$dependency.dll")) {
                    throw "$name did not consume the reference/implementation assembly pair."
                }
            }
        }
        $mode = if ($generation -eq 1) { "seed" } else { "upgrade" }
        $expected = if ($generation -eq 1) {
            "InlineLibrarySeed:True:TransitivePrivateInline:True:NullableAndGeneric:True:OwnerDelta:True:ColdNoChange:True"
        } else {
            "InlineLibraryUpgrade:True:DeletedInlineClr:True:HistoricalExact:True:OwnerBase:True:NoChangeThenDelta:True:ColdReopen:True"
        }
        $result = (& dotnet $hostAssembly $mode $database | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $result -ne $expected) { throw "Generation $generation failed: '$result'." }
        Write-Host $result
    }
    foreach ($name in @("LibraryB", "LibraryA", "AppModel")) {
        Read-History (Join-Path $workRoot "$name-history") $accepted[$name].Count $accepted[$name]
        Assert-OwnedHistory (Join-Path $workRoot "$name-history") $ownedIds[$name]
    }
    $accepted | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workRoot "independent-history.json")
    Write-Host "InlineLibraryPackaging:True:ReferenceAssemblies:True:IndependentHistoryUnchanged:True:NoImportedHistoryPublication:True"
    Write-Host "InlineLibrary package consumer probe passed. Artifacts: $workRoot"
}
finally { Pop-Location }
