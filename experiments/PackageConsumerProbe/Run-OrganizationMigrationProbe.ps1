[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $LegacyPackageSource,
    [Parameter(Mandatory)][string] $LegacyVersion,
    [Parameter(Mandatory)][string] $PackageSource,
    [Parameter(Mandatory)][string] $Version,
    [ValidateSet('All', 'Seed', 'Complete')][string] $Stage = 'All',
    [string] $WorkRoot
)

# Legacy and current lanes compile identical model bytes with different real runtime/analyzer packages.
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
if ($LegacyVersion -eq $Version) { throw 'The package versions must be distinct.' }
$LegacyPackageSource = (Resolve-Path -LiteralPath $LegacyPackageSource).Path
$PackageSource = [IO.Path]::GetFullPath($PackageSource)
if (!$WorkRoot) {
    if ($Stage -eq 'Complete') { throw 'Complete requires the WorkRoot produced by Seed.' }
    $WorkRoot = Join-Path $PSScriptRoot "obj/organization-migration-$([Guid]::NewGuid().ToString('N'))"
}
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$database = Join-Path $WorkRoot 'database'
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
function Get-History([string] $Root) {
    $result = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Filter *.dgschema) {
        $result[$file.Name] = [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName))
    }
    if ($result.Count -ne 2) { throw "Expected two accepted schema records; found $($result.Count)." }
    return $result
}
function Assert-History([string] $Root, $Expected) {
    $actual = Get-History $Root
    if ($actual.Count -ne $Expected.Count) { throw 'Accepted history file set changed.' }
    foreach ($key in $actual.Keys) { if ($actual[$key] -cne $Expected[$key]) { throw "Accepted history changed: $key" } }
}
function Get-PersistedSnapshot {
    return (@(Get-ChildItem -LiteralPath $database -Recurse -File | Sort-Object FullName | ForEach-Object {
        "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }) -join "`n")
}
function Invoke-Consumer([string] $Lane, [string] $Mode) {
    $assembly = Join-Path $WorkRoot "$Lane/bin/Debug/net10.0/Atelia.OrganizationMigrationConsumer.dll"
    $lines = @(& dotnet $assembly $Mode $database)
    if ($LASTEXITCODE -ne 0 -or $lines[-1] -ne "OrganizationMigration:${Mode}:Passed") { throw "$Lane $Mode failed: $lines" }
    $lines | Set-Content -LiteralPath (Join-Path $WorkRoot "$Lane-$Mode.log")
    $lines | ForEach-Object { Write-Host $_ }
    foreach ($name in @('Atelia.DurableGraph', $(if ($Lane -eq 'legacy') { 'Atelia.DurableGraph.StateStore' } else { 'Atelia.DurableGraph.Persistence' }))) {
        $dll = Join-Path (Split-Path $assembly) "$name.dll"
        $versionForLane = if ($Lane -eq 'legacy') { $LegacyVersion } else { $Version }
        $cached = Join-Path $WorkRoot "$Lane/packages/$($name.ToLowerInvariant())/$versionForLane/lib/net10.0/$name.dll"
        if ((Get-FileHash $dll).Hash -ne (Get-FileHash $cached).Hash) { throw "Loaded DLL differs from restored package: $name" }
    }
}
function Build-Lane([string] $Lane) {
    $legacy = $Lane -eq 'legacy'
    $root = Join-Path $WorkRoot $Lane
    if (Test-Path -LiteralPath $root) { throw "Refusing to overwrite existing lane $root" }
    $history = Join-Path $root 'history'
    New-Item -ItemType Directory -Path $root, $history | Out-Null
    $persistence = if ($legacy) { 'Atelia.DurableGraph.StateStore' } else { 'Atelia.DurableGraph.Persistence' }
    $storage = if ($legacy) { 'Atelia.DurableGraph.StateStore.Storage' } else { 'Atelia.DurableGraph.Storage' }
    $serialization = if ($legacy) { 'Atelia.DurableGraph.StateStore.Serialization' } else { 'Atelia.DurableGraph.Serialization' }
    $feed = if ($legacy) { $LegacyPackageSource } else { $PackageSource }
    $packageVersion = if ($legacy) { $LegacyVersion } else { $Version }
    $project = Join-Path $root 'Consumer.csproj'
    $projectText = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <AssemblyName>Atelia.OrganizationMigrationConsumer</AssemblyName>
    <DurableGraphGenerateDefinitions>true</DurableGraphGenerateDefinitions>
    <WarningsAsErrors>$(WarningsAsErrors);CS0433;CS0436</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Atelia.DurableGraph" Version="__VERSION__" />
    <PackageReference Include="__PERSISTENCE__" Version="__VERSION__" />
  </ItemGroup>
</Project>
'@
    [IO.File]::WriteAllText($project, $projectText.Replace('__VERSION__', $packageVersion).Replace('__PERSISTENCE__', $persistence), $utf8)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OrganizationMigrationConsumer/Model.cs') -Destination $root
    if (!$legacy -and (Get-FileHash -LiteralPath (Join-Path $root 'Model.cs')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $WorkRoot 'legacy/Model.cs')).Hash) {
        throw 'Business model bytes changed between the old and new package lanes.'
    }
    $programName = if ($legacy) { 'Program.Legacy.cs' } else { 'Program.cs' }
    $program = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "OrganizationMigrationConsumer/$programName"))
    [IO.File]::WriteAllText((Join-Path $root 'Program.cs'), $program.Replace('__PERSISTENCE__', $persistence).Replace('__STORAGE__', $storage).Replace('__SERIALIZATION__', $serialization).Replace('__LEGACY__', $legacy.ToString().ToLowerInvariant()), $utf8)
    if (!$legacy) {
        Copy-Item -Path (Join-Path $WorkRoot 'legacy/history/*.dgschema') -Destination $history
        Assert-History $history $script:accepted
    }
    $properties = @("-p:DurableGraphSchemaHistoryDirectory=$history")
    Invoke-DotNet (@('restore', $project, '--source', $feed, '--packages', (Join-Path $root 'packages')) + $properties)
    $historyMode = if ($legacy) { 'Publish' } else { 'Verify' }
    Invoke-DotNet (@('build', $project, '--no-restore', "-p:DurableGraphSchemaHistoryMode=$historyMode", '-v:q') + $properties)
    if ($legacy) { $script:accepted = Get-History $history } else { Assert-History $history $script:accepted }
    Invoke-DotNet (@('clean', $project, '-v:q') + $properties)
    Invoke-DotNet (@('build', $project, '--no-restore', '-p:DurableGraphSchemaHistoryMode=Verify', '-v:q') + $properties)
    Assert-History $history $script:accepted
    $assets = Get-Content -Raw -LiteralPath (Join-Path $root 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
    $provenance = foreach ($entry in $assets.libraries.GetEnumerator() | Where-Object { $_.Value.type -eq 'package' }) {
        $parts = $entry.Key.Split('/')
        $nupkg = Join-Path $feed "$($parts[0]).$($parts[1]).nupkg"
        [pscustomobject]@{ Package = $entry.Key; SHA256 = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash; Source = $nupkg }
    }
    $provenance | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'package-provenance.json')
    $generator = Join-Path $root "packages/atelia.durablegraph/$packageVersion/analyzers/dotnet/cs/Atelia.DurableGraph.Generator.dll"
    Get-FileHash -LiteralPath $generator -Algorithm SHA256 | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'generator-provenance.json')
}

if ($Stage -ne 'Complete') {
    if (Test-Path -LiteralPath $WorkRoot) { throw "Seed requires a fresh WorkRoot: $WorkRoot" }
    New-Item -ItemType Directory -Path $WorkRoot | Out-Null
    Build-Lane 'legacy'
    Invoke-Consumer 'legacy' 'seed'
    $accepted | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $WorkRoot 'accepted-history.json')
    @{ LegacyPackageSource = $LegacyPackageSource; LegacyVersion = $LegacyVersion; Version = $Version } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $WorkRoot 'seed.json')
    Write-Host "OrganizationMigration:LegacySeed:Passed; Artifacts: $WorkRoot"
}
if ($Stage -ne 'Seed') {
    $seed = Get-Content -Raw -LiteralPath (Join-Path $WorkRoot 'seed.json') | ConvertFrom-Json
    if ($seed.LegacyVersion -ne $LegacyVersion -or $seed.Version -ne $Version -or $seed.LegacyPackageSource -ne $LegacyPackageSource) { throw 'Seed provenance does not match requested lanes.' }
    $script:accepted = Get-Content -Raw -LiteralPath (Join-Path $WorkRoot 'accepted-history.json') | ConvertFrom-Json -AsHashtable
    Build-Lane 'current'
    $before = Get-PersistedSnapshot
    Invoke-Consumer 'current' 'read'
    if ($before -cne (Get-PersistedSnapshot)) { throw 'Cold read altered persisted files.' }
    Invoke-Consumer 'current' 'continue'
    $before = Get-PersistedSnapshot
    Invoke-Consumer 'current' 'check'
    if ($before -cne (Get-PersistedSnapshot)) { throw 'Final cold read altered persisted files.' }
    $frames = @(Get-Content -Raw -LiteralPath (Join-Path $WorkRoot 'legacy-frames.json') | ConvertFrom-Json)
    foreach ($frame in $frames) {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $database $frame.Path))
        $oldFrame = [byte[]]::new($frame.Length)
        [Array]::Copy($bytes, [long]$frame.Offset, $oldFrame, 0L, [long]$frame.Length)
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($oldFrame))
        if ($hash -cne $frame.SHA256) { throw "Old complete frame changed at $($frame.Path):$($frame.Offset)." }
    }
    Assert-History (Join-Path $WorkRoot 'legacy/history') $accepted
    Assert-History (Join-Path $WorkRoot 'current/history') $accepted
    "OrganizationMigration:TwoRealPackages:HistoryUnchanged:OldFramesUnchanged:$($frames.Count):ReadOnlyUnchanged:Delta:NoChange:Passed" | Tee-Object -FilePath (Join-Path $WorkRoot 'result.txt')
    Write-Host "Organization migration package probe passed. Artifacts: $WorkRoot"
}
