param(
    [Parameter(Mandatory)][string]$StoragePackageSource,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new directory outside the repository.' }
$root = [IO.Path]::GetFullPath($OutputDirectory)
if ($root.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The producer and package consumers need an external work directory.'
}
$source = Join-Path $root 'source'
$feed = Join-Path $root 'feed'
$bitmap = Join-Path $root 'bitmap'
$app = Join-Path $root 'consumer'
$cache = Join-Path $root 'consumer-cache'
$version = '0.0.0-leafpkg.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.g' + [guid]::NewGuid().ToString('N').Substring(0,8)
foreach ($dir in $source,$bitmap,$app) { [void][IO.Directory]::CreateDirectory($dir) }

function Run([string[]]$DotNetArgs) {
    & dotnet @DotNetArgs
    if ($LASTEXITCODE) { throw "dotnet $($DotNetArgs -join ' ') failed" }
}
function Write-Project([string]$Path, [string[]]$PackageIds, [string]$Additional) {
    $refs = foreach ($id in $PackageIds) { "<PackageReference Include='$id' Version='[$version]'/>" }
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>$Additional</PropertyGroup>
  <ItemGroup>$($refs -join [Environment]::NewLine)</ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

# Pack one committed tree. Git metadata supplies the same provenance to each NuGet package.
$revision = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -or $revision -notmatch '^[0-9a-f]{40}$') { throw 'Could not identify the source commit.' }
$remote = git -C $repo remote get-url origin
if ($LASTEXITCODE) { throw 'Could not identify the source repository.' }
git clone --quiet --local --no-hardlinks --no-checkout -- $repo $source
if ($LASTEXITCODE) { throw 'Could not clone the package producer.' }
git -C $source -c advice.detachedHead=false checkout --quiet --detach $revision
if ($LASTEXITCODE) { throw 'Could not select the source commit.' }
git -C $source remote set-url origin $remote
if ($LASTEXITCODE) { throw 'Could not set the source repository URL for packing.' }
. (Join-Path $source 'experiments/PackageConsumerProbe/PackageProbeSupport.ps1')
Push-Location $source
try {
    Prepare-DurableGraphProbeFeed -RepositoryRoot $source -OutputDirectory $feed -Version $version -StoragePackageSource $StoragePackageSource -IncludeText
} finally { Pop-Location }

# Complete the offline consumer feed with the third-party packages selected by Text's restore.
$textAssets = Get-Content -Raw (Join-Path $source 'src/Text/obj/project.assets.json') | ConvertFrom-Json -AsHashtable
foreach ($p in $textAssets.libraries.Values) {
    if ($p.type -ne 'package' -or $p.path.StartsWith('atelia.')) { continue }
    $package = foreach ($location in $textAssets.packageFolders.Keys) {
        Get-ChildItem -LiteralPath (Join-Path $location $p.path) -Filter '*.nupkg' -ErrorAction SilentlyContinue
    }
    if (!$package) { throw "Could not find the resolved third-party archive $($p.path)" }
    Copy-Item -LiteralPath @($package)[0].FullName -Destination $feed
}
$config = Join-Path $root 'NuGet.Config'
"<configuration><packageSources><clear/><add key='candidate' value='$([Security.SecurityElement]::Escape($feed))'/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>" |
    Set-Content -LiteralPath $config -Encoding utf8NoBOM
Copy-Item (Join-Path $source 'global.json') $root
Copy-Item (Join-Path $source 'experiments/RegisteredLeafProbe/Bitmap/*.cs') $bitmap
Copy-Item (Join-Path $source 'experiments/RegisteredLeafProbe/Consumer/*.cs') $app
$bitmapProject = Join-Path $bitmap 'Atelia.VariableBitmap.csproj'
Write-Project $bitmapProject @('Atelia.DurableGraph.Serialization') ''
$appProject = Join-Path $app 'Probe.Consumer.csproj'
Write-Project $appProject @('Atelia.DurableGraph','Atelia.DurableGraph.Persistence','Atelia.Text','Atelia.VariableBitmap') '<OutputType>Exe</OutputType><DurableGraphGenerateDefinitions>true</DurableGraphGenerateDefinitions>'

Push-Location $root
try {
    Run @('pack',$bitmapProject,'-c','Release','-o',$feed,"-p:PackageVersion=$version","-p:RestoreConfigFile=$config","-p:RestorePackagesPath=$(Join-Path $root 'bitmap-cache')",'-m:1','-nr:false')
    Run @('restore',$appProject,'--packages',$cache,'--configfile',$config)
    Run @('build',$appProject,'-c','Release','--no-restore','-t:Rebuild','-m:1','-nr:false')
    Run @('build',$appProject,'-c','Release','--no-restore','-t:Rebuild','-p:DurableGraphSchemaHistoryMode=Verify','-m:1','-nr:false')
    $output = Join-Path $app 'bin/Release/net10.0'
    foreach ($stage in 'seed','resume','verify','missing') {
        Run @((Join-Path $output 'Probe.Consumer.dll'),$stage,(Join-Path $root 'store'),(Join-Path $root "$stage.json"))
    }
} finally { Pop-Location }

$assets = Get-Content -Raw (Join-Path $app 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
if ($assets.packageFolders.Count -ne 1 -or [IO.Path]::TrimEndingDirectorySeparator(@($assets.packageFolders.Keys)[0]) -ne $cache) {
    throw 'Consumer restore did not use just the fresh cache.'
}
if ($assets.project.restore.sources.Count -ne 1 -or !$assets.project.restore.sources.Contains($feed)) {
    throw 'Consumer restore did not use just the local feed.'
}
$generator = 'Atelia.DurableGraph.Generator.dll'
$restoredGenerator = Join-Path $cache "atelia.durablegraph/$version/analyzers/dotnet/cs/$generator"
$generatorHash = (Get-FileHash $restoredGenerator).Hash
if ($generatorHash -ne (Get-FileHash (Join-Path $source "src/DurableGraph.Generator/bin/Release/netstandard2.0/$generator")).Hash) {
    throw 'The analyzer in the Runtime nupkg differs from the isolated source build.'
}
$packages = foreach ($key in $assets.libraries.Keys) {
    $entry = $assets.libraries[$key]
    if ($entry.type -ne 'package') { throw "Unexpected non-package input $key" }
    $dir = Join-Path $cache $entry.path
    $archive = Get-ChildItem -LiteralPath $dir -Filter '*.nupkg' -File
    $original = (Get-FileHash (Join-Path $feed $archive.Name)).Hash
    if ((Get-FileHash $archive.FullName).Hash -ne $original) { throw "Restored package differs from feed: $key" }
    [xml]$nuspec = Get-Content -Raw (Get-ChildItem -LiteralPath $dir -Filter '*.nuspec' -File).FullName
    $id = $nuspec.package.metadata.id
    $deps = @($nuspec.SelectNodes("//*[local-name()='dependency']") | ForEach-Object { $_.id } | Sort-Object -Unique)
    if (($id -like 'Atelia.DurableGraph*' -or $id -eq 'Atelia.Text') -and
        ($nuspec.package.metadata.repository.commit -ne $revision -or $nuspec.package.metadata.version -ne $version)) {
        throw "Incorrect source revision or version in $id"
    }
    $dll = Join-Path $output "$id.dll"
    if (!(Test-Path $dll)) { throw "No application output for $id" }
    $hash = (Get-FileHash $dll).Hash
    $candidate = @(Get-ChildItem -LiteralPath (Join-Path $dir 'lib') -Recurse -Filter "$id.dll" | Where-Object { (Get-FileHash $_.FullName).Hash -eq $hash })
    if (!$candidate.Count) { throw "Output DLL differs from package $id" }
    $refs = @(([Reflection.Assembly]::LoadFile($dll)).GetReferencedAssemblies().Name | Where-Object { $_ -like 'Atelia.*' } | Sort-Object)
    if ($id -in 'Atelia.VariableBitmap','Atelia.Text') {
        if ($deps.Count -ne 1 -or $deps[0] -ne 'Atelia.DurableGraph.Serialization' -or $refs -contains 'Atelia.DurableGraph' -or $refs -contains 'Atelia.DurableGraph.Persistence') {
            throw "Unexpected leaf dependencies for $id"
        }
    } elseif ($id -like 'Atelia.DurableGraph*' -and ('Atelia.Text' -in $deps -or 'Atelia.Text' -in $refs -or 'Atelia.VariableBitmap' -in $refs -or 'Atelia.VariableBitmap' -in $deps)) {
        throw "$id references a leaf implementation"
    }
    [pscustomobject]@{ id=$id; version=$nuspec.package.metadata.version; dependencies=$deps; assemblyReferences=$refs; nupkgSha256=$original; dllSha256=$hash }
}
foreach ($stage in 'seed','resume','verify','missing') {
    $receipt = Get-Content -Raw (Join-Path $root "$stage.json") | ConvertFrom-Json
    foreach ($dll in $receipt.assemblies) {
        if ($dll.hash -ne (Get-FileHash (Join-Path $output "$($dll.name).dll")).Hash) { throw "Loaded assembly differs: $stage $($dll.name)" }
    }
}
$history = @(Get-ChildItem -LiteralPath (Join-Path $app 'DurableGraphSchemaHistory') -Filter '*.dgschema' -File)
if (!$history.Count) { throw 'No generated schema-history files.' }
[pscustomobject]@{ sourceCommit=$revision; version=$version; sdk=(dotnet --version); packages=$packages; analyzerSha256=$generatorHash; schemaFiles=$history.Name; stages=@('seed','resume','verify','missing') } |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $root 'package-evidence.json')
Write-Host "Package verification passed. $root"
