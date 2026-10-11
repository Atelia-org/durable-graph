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

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$runStamp = "$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/cross-assembly-$runStamp"
$packageCache = Join-Path $workRoot "packages"
$modelFeed = Join-Path $workRoot "model-feed"
$database = Join-Path $workRoot "database"
$domainHistory = Join-Path $workRoot "domain-history"
$appHistory = Join-Path $workRoot "app-history"
$hostHistory = Join-Path $workRoot "host-history"
New-Item -ItemType Directory -Path $packageCache, $modelFeed | Out-Null
$projects = @{}
foreach ($name in @("DomainLibrary", "AppModel", "Host")) {
    $project = Join-Path $PSScriptRoot "CrossAssemblyConsumer/$name/$name.csproj"
    $source = Get-Content -LiteralPath $project -Raw
    foreach ($forbidden in @("<Import", "<ProjectReference", "<Analyzer", "<AdditionalFiles", "<NoWarn")) {
        if ($source.Contains($forbidden, [StringComparison]::Ordinal)) { throw "$name contains forbidden manual wiring '$forbidden'." }
    }
    $projects[$name] = $project
}
$modelVersion1 = "0.0.0-cross-assembly-v1.$runStamp"
$modelVersion2 = "0.0.0-cross-assembly-v2.$runStamp"
$appVersion = "0.0.0-cross-assembly-app.$runStamp"
$domainOutput = (Join-Path $workRoot "domain-bin") + [IO.Path]::DirectorySeparatorChar
$appOutput = (Join-Path $workRoot "app-bin") + [IO.Path]::DirectorySeparatorChar
$hostOutput = (Join-Path $workRoot "host-bin") + [IO.Path]::DirectorySeparatorChar
Push-Location $repositoryRoot
try {
    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        $PackageSource = Join-Path $workRoot "feed"
        New-Item -ItemType Directory -Path $PackageSource | Out-Null
        $Version = "0.0.0-cross-assembly-e2e.$runStamp"
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version
        if (@(Get-ChildItem -LiteralPath $PackageSource -Filter *.nupkg -File).Count -ne 10) { throw "Expected ten runtime dependency packages." }
    }
    $common = @("-p:DurableGraphPackageVersion=$Version")
    $domainProperties = $common + @(
        "-p:DurableGraphSchemaHistoryDirectory=$domainHistory",
        "-p:BaseIntermediateOutputPath=$((Join-Path $workRoot 'domain-obj') + [IO.Path]::DirectorySeparatorChar)",
        "-p:BaseOutputPath=$domainOutput"
    )
    $domainV1 = $domainProperties + @("-p:HistoryVersion=1", "-p:PackageVersion=$modelVersion1")
    Invoke-DotNet (@("restore", $projects.DomainLibrary, "--source", $PackageSource, "--packages", $packageCache) + $domainV1)
    Invoke-DotNet (@("build", $projects.DomainLibrary, "--no-restore") + $domainV1)
    $domainAccepted = @{}
    Read-History $domainHistory 2 $domainAccepted
    Invoke-DotNet (@("build", $projects.DomainLibrary, "--no-restore", "-p:DurableGraphSchemaHistoryMode=Verify") + $domainV1)
    Read-History $domainHistory 2 $domainAccepted
    Invoke-DotNet (@("pack", $projects.DomainLibrary, "--no-build", "--configuration", "Debug", "--output", $modelFeed) + $domainV1)

    $appProperties = $common + @(
        "-p:DomainLibraryPackageVersion=$modelVersion1", "-p:PackageVersion=$appVersion",
        "-p:DurableGraphSchemaHistoryDirectory=$appHistory",
        "-p:BaseIntermediateOutputPath=$((Join-Path $workRoot 'app-obj') + [IO.Path]::DirectorySeparatorChar)",
        "-p:BaseOutputPath=$appOutput"
    )
    Invoke-DotNet (@("restore", $projects.AppModel, "--source", $PackageSource, "--source", $modelFeed, "--packages", $packageCache) + $appProperties)
    Invoke-DotNet (@("build", $projects.AppModel, "--no-restore") + $appProperties)
    $appAccepted = @{}
    Read-History $appHistory 1 $appAccepted
    Invoke-DotNet (@("build", $projects.AppModel, "--no-restore", "-p:DurableGraphSchemaHistoryMode=Verify") + $appProperties)
    Read-History $appHistory 1 $appAccepted
    Invoke-DotNet (@("pack", $projects.AppModel, "--no-build", "--configuration", "Debug", "--output", $modelFeed) + $appProperties)

    $hostProperties = $common + @(
        "-p:AppModelPackageVersion=$appVersion", "-p:DurableGraphSchemaHistoryDirectory=$hostHistory",
        "-p:BaseIntermediateOutputPath=$((Join-Path $workRoot 'host-obj') + [IO.Path]::DirectorySeparatorChar)",
        "-p:BaseOutputPath=$hostOutput"
    )
    Invoke-DotNet (@("restore", $projects.Host, "--source", $PackageSource, "--source", $modelFeed, "--packages", $packageCache) + $hostProperties)
    Invoke-DotNet (@("build", $projects.Host, "--no-restore") + $hostProperties)
    if ((Test-Path -LiteralPath $hostHistory) -and @(Get-ChildItem -LiteralPath $hostHistory -Filter *.dgschema -File).Count -ne 0) {
        throw "Pure Host must not own model history."
    }
    $hostDirectory = Join-Path $hostOutput "Debug/net10.0"
    $hostAssembly = Join-Path $hostDirectory "Atelia.Host.dll"
    $appAssembly = Join-Path $hostDirectory "Atelia.AppModel.dll"
    $domainAssembly = Join-Path $hostDirectory "Atelia.DomainLibrary.dll"
    $hostHash = (Get-FileHash -LiteralPath $hostAssembly -Algorithm SHA256).Hash
    $appHash = (Get-FileHash -LiteralPath $appAssembly -Algorithm SHA256).Hash
    $domainHash = (Get-FileHash -LiteralPath $domainAssembly -Algorithm SHA256).Hash
    $identityV1 = [Reflection.AssemblyName]::GetAssemblyName($domainAssembly).FullName
    $seed = (& dotnet $hostAssembly seed $database | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $seed -ne "CrossAssemblySeed:True:IndependentCatalogs:True:SharedCycle:True:ChildOnlyDelta:True:ColdNoChange:True") {
        throw "V1 Host failed: '$seed'."
    }
    Write-Host $seed

    # Only DomainLibrary is rebuilt. AppModel and Host continue to use their original DLLs.
    Read-History $domainHistory 2 $domainAccepted
    Read-History $appHistory 1 $appAccepted
    $domainV2 = $domainProperties + @("-p:HistoryVersion=2", "-p:PackageVersion=$modelVersion2")
    Invoke-DotNet (@("clean", $projects.DomainLibrary) + $domainV2)
    Invoke-DotNet (@("build", $projects.DomainLibrary, "--no-restore") + $domainV2)
    Read-History $domainHistory 4 $domainAccepted
    Invoke-DotNet (@("build", $projects.DomainLibrary, "--no-restore", "-p:DurableGraphSchemaHistoryMode=Verify") + $domainV2)
    Read-History $domainHistory 4 $domainAccepted
    Invoke-DotNet (@("pack", $projects.DomainLibrary, "--no-build", "--configuration", "Debug", "--output", $modelFeed) + $domainV2)
    $v2Package = Join-Path $modelFeed "Atelia.DomainLibrary.$modelVersion2.nupkg"
    $extracted = Join-Path $workRoot "domain-v2-package"
    [IO.Compression.ZipFile]::ExtractToDirectory($v2Package, $extracted)
    $replacement = Join-Path $extracted "lib/net10.0/Atelia.DomainLibrary.dll"
    if ([Reflection.AssemblyName]::GetAssemblyName($replacement).FullName -ne $identityV1 -or
        (Get-FileHash -LiteralPath $replacement -Algorithm SHA256).Hash -eq $domainHash) {
        throw "V2 must change library DLL bytes while preserving its assembly identity."
    }
    $runtimeDirectory = Join-Path $workRoot "runtime-v2"
    New-Item -ItemType Directory -Path $runtimeDirectory | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $hostDirectory) {
        Copy-Item -LiteralPath $file.FullName -Destination $runtimeDirectory -Recurse
    }
    Copy-Item -LiteralPath $replacement -Destination (Join-Path $runtimeDirectory "Atelia.DomainLibrary.dll")
    foreach ($directory in @($hostDirectory, $runtimeDirectory)) {
        if ((Get-FileHash -LiteralPath (Join-Path $directory "Atelia.Host.dll") -Algorithm SHA256).Hash -ne $hostHash -or
            (Get-FileHash -LiteralPath (Join-Path $directory "Atelia.AppModel.dll") -Algorithm SHA256).Hash -ne $appHash) {
            throw "The fixed AppModel/Host DLLs changed."
        }
    }
    $upgrade = (& dotnet (Join-Path $runtimeDirectory "Atelia.Host.dll") upgrade $database | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $upgrade -ne "CrossAssemblyUpgrade:True:DeletedInlineClr:True:HistoricalExact:True:TargetOnlyBase:True:NoChangeThenDelta:True:ColdReopen:True") {
        throw "V2 replacement failed: '$upgrade'."
    }
    Write-Host $upgrade
    Read-History $domainHistory 4 $domainAccepted
    Read-History $appHistory 1 $appAccepted
    @{
        AssemblyIdentity = $identityV1
        DomainV1Sha256 = $domainHash
        DomainV2Sha256 = (Get-FileHash -LiteralPath $replacement -Algorithm SHA256).Hash
        AppModelSha256 = $appHash
        HostSha256 = $hostHash
        DomainHistory = $domainAccepted
        AppHistory = $appAccepted
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $workRoot "binary-compatibility.json")
    Write-Host "CrossAssemblyBinaryCompatibility:True:LibraryChanged:True:AssemblyIdentityStable:True:AppAndHostUnchanged:True:IndependentHistoryUnchanged:True"
    Write-Host "CrossAssembly package consumer probe passed. Artifacts: $workRoot"
}
finally { Pop-Location }
