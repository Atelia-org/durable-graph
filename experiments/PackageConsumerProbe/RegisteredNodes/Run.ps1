[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageSource,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$dgFeed = (Resolve-Path -LiteralPath $PackageSource).Path
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh directory for the apps, caches, store and receipts.' }
[void][IO.Directory]::CreateDirectory($output)
$probe = Join-Path $output 'consumer'
$receipts = Join-Path $output 'receipts'
[void][IO.Directory]::CreateDirectory($probe)
[void][IO.Directory]::CreateDirectory($receipts)
foreach ($dir in 'Writer','Reader','PagesV1','PagesV2') {
    $copy = Join-Path $probe $dir
    [void][IO.Directory]::CreateDirectory($copy)
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $dir) -File | Where-Object Extension -in '.cs','.csproj' | Copy-Item -Destination $copy
}
foreach ($item in 'Directory.Build.props','Book.cs','Page.cs') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $item) $probe }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
Copy-Item -LiteralPath (Join-Path $repo 'global.json') $probe
# The staged hosts are independent of the repo's build configuration, analyzer paths and source references.
'<Project />' | Set-Content -LiteralPath (Join-Path $probe 'Directory.Build.targets')
'<Project />' | Set-Content -LiteralPath (Join-Path $probe 'Directory.Packages.props')

function Run([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE) { throw "dotnet $($Arguments -join ' ') failed ($LASTEXITCODE)." }
}
function Get-LibraryHash([string] $Package, [string] $Entry) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $file = $zip.GetEntry($Entry)
        if ($null -eq $file) { throw "Missing $Entry in $Package" }
        $stream = $file.Open()
        try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
}

$hasK4os = @(Get-ChildItem -LiteralPath $dgFeed -File -Filter 'K4os.Compression.LZ4.*.nupkg').Count -ne 0
$summary = [ordered]@{ version=$Version; sdk=$null; targets=@(); stages=@() }
$hosts = @{}
Push-Location $probe
try {
    $summary.sdk = (& dotnet --version).Trim()
    foreach ($stage in 'Writer','Reader') {
        $library = if ($stage -eq 'Writer') { 'PagesV1' } else { 'PagesV2' }
        $domain = "RegisteredNodes.$library"
        $local = Join-Path $output "$stage-feed"
        [void][IO.Directory]::CreateDirectory($local)
        $cache = Join-Path $output "$stage-cache"
        $config = Join-Path $output "$stage.NuGet.Config"
        $extraSource = if (!$hasK4os) { '<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>' }
        $extraMapping = if (!$hasK4os) { '<packageSource key="nuget.org"><package pattern="K4os.Compression.LZ4"/></packageSource>' }
        $mappedK4os = if ($hasK4os) { '<package pattern="K4os.Compression.LZ4"/>' }
        @"
<configuration><packageSources><clear/>
<add key="dg" value="$([Security.SecurityElement]::Escape($dgFeed))"/>
<add key="domain" value="$([Security.SecurityElement]::Escape($local))"/>$extraSource
</packageSources><packageSourceMapping><clear/>
<packageSource key="dg"><package pattern="Atelia.*"/>$mappedK4os</packageSource>
<packageSource key="domain"><package pattern="$domain"/></packageSource>$extraMapping
</packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>
"@ | Set-Content -LiteralPath $config
        $properties = @("-p:DurableGraphPackageVersion=$Version", "-p:RestorePackagesPath=$cache", '-m:1', '-nr:false')
        $modelProject = Join-Path $library "$domain.csproj"
        $project = Join-Path $stage "RegisteredNodes.$stage.csproj"
        Run (@('restore',$modelProject,'--configfile',$config) + $properties)
        Run (@('pack',$modelProject,'-c','Release','--no-restore','-o',$local,"-p:PackageVersion=$Version") + $properties)
        Run (@('restore',$project,'--configfile',$config) + $properties)
        Run (@('build',$project,'-c','Release','--no-restore','-t:Rebuild') + $properties)
        Run (@('build',$project,'-c','Release','--no-restore','-t:Rebuild','-p:DurableGraphSchemaHistoryMode=Verify') + $properties)
        $schemas = @(Get-ChildItem -LiteralPath (Join-Path $stage 'DurableGraphSchemaHistory') -Filter '*.dgschema' -File)
        if ($schemas.Count -ne 1) { throw "Expected one generated Book history for $stage." }
        $assets = Get-Content -Raw (Join-Path $stage 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
        if ($assets.packageFolders.Count -ne 1 -or [IO.Path]::TrimEndingDirectorySeparator(@($assets.packageFolders.Keys)[0]) -ine $cache) {
            throw "$stage restored outside its dedicated cache."
        }
        $ids = @($assets.libraries.Keys | ForEach-Object { $_.Split('/')[0] })
        $expected = @($domain,'Atelia.DurableGraph','Atelia.DurableGraph.Persistence','Atelia.DurableGraph.Serialization','Atelia.DurableGraph.Storage',
            'Atelia.Binary','Atelia.Rbf','Atelia.FrameStore','Atelia.HeadStore','Atelia.Data','Atelia.Primitives','K4os.Compression.LZ4')
        if (Compare-Object $ids $expected) { throw "Unexpected dependency set for $stage : $ids" }
        $bin = Join-Path (Resolve-Path $stage).Path 'bin/Release/net10.0'
        $archives = @{}
        foreach ($name in $assets.libraries.Keys) {
            $entry = $assets.libraries[$name]
            if ($entry.type -cne 'package') { throw "Found non-package input $name in $stage." }
            $id, $selected = $name -split '/', 2
            if (($id -like 'Atelia.DurableGraph*' -or $id -ceq $domain) -and $selected -cne $Version) { throw "Wrong candidate G for $id." }
            $dir = Join-Path $cache $entry.path
            $archive = (Get-ChildItem -LiteralPath $dir -Filter '*.nupkg' -File).FullName
            $origin = if ($id -ceq $domain) { $local } else { $dgFeed }
            $archiveHash = (Get-FileHash -LiteralPath $archive).Hash
            if ($id -ne 'K4os.Compression.LZ4' -or $hasK4os) {
                $selectedArchive = @(Get-ChildItem -LiteralPath $origin -File -Filter '*.nupkg' | Where-Object Name -ieq "$id.$selected.nupkg")
                if ($selectedArchive.Count -ne 1 -or (Get-FileHash -LiteralPath $selectedArchive[0].FullName).Hash -ne $archiveHash) {
                    throw "$stage cached archive differs from feed for $id."
                }
            }
            $dllHash = (Get-FileHash -LiteralPath (Join-Path $bin "$id.dll")).Hash
            $runtimeEntry = @($assets.targets.Values)[0][$name].runtime.Keys | Where-Object { $_.EndsWith("/$id.dll") }
            if (!$runtimeEntry -or (Get-LibraryHash $archive $runtimeEntry) -ne $dllHash) { throw "$stage output differs from the selected $id runtime asset." }
            $archives[$id] = [ordered]@{ key=$name; nupkg=$archiveHash; dll=$dllHash }
        }
        $summary.targets += [ordered]@{ host=$stage; history=$schemas[0].Name; packages=$archives }
        $hosts[$stage] = [pscustomobject]@{ dll=(Join-Path $bin "RegisteredNodes.$stage.dll"); libraries=$archives }
    }

    $store = Join-Path $output 'repository'
    foreach ($stage in 'write','upgrade','verify','verify-old') {
        $hostInfo = if ($stage -in 'write','verify-old') { $hosts.Writer } else { $hosts.Reader }
        $receipt = Join-Path $receipts "$stage.json"
        $argsForHost = @($hostInfo.dll)
        if ($stage -ne 'write') { $argsForHost += $stage }
        Run ($argsForHost + @($store, $receipt))
        $actual = Get-Content -Raw -LiteralPath $receipt | ConvertFrom-Json
        foreach ($lib in $actual.assemblies) {
            if (!$hostInfo.libraries.ContainsKey($lib.name) -or $hostInfo.libraries[$lib.name].dll -ne $lib.sha256) {
                throw "Unexpected assembly loaded in $stage : $($lib.name)."
            }
        }
        $expectedDomain = if ($stage -in 'write','verify-old') { 'RegisteredNodes.PagesV1' } else { 'RegisteredNodes.PagesV2' }
        foreach ($required in 'Atelia.DurableGraph','Atelia.DurableGraph.Persistence',$expectedDomain) {
            if ($required -cnotin $actual.assemblies.name) { throw "$stage did not load $required." }
        }
        $summary.stages += $actual
    }
    $summary | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding utf8NoBOM
    Write-Host "Registered node package acceptance passed: $output"
} finally { Pop-Location }
