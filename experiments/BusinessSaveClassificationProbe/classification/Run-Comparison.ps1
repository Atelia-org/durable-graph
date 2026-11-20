param(
    [ValidateSet('all', 'setup', 'measure')][string] $Stage = 'all',
    [string] $Baseline,
    [string] $Candidate,
    [Parameter(Mandatory)][string] $Output,
    [string] $CpuMask = '10',
    [ValidateSet('A','B')][string] $FirstArm = 'A',
    [string] $CandidateRevision = '97d6189d',
    [string] $ReuseHarnessFrom,
    [ValidateSet('fixed','default')][string] $JitMode = 'fixed'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$platform = 'tests/DurableGraph.Persistence.Tests/bin/Release/net10.0'
$assembly = 'Atelia.DurableGraph.Persistence.Tests.dll'
$products = @('Atelia.DurableGraph.dll', 'Atelia.DurableGraph.Persistence.dll')
$roots = @{A = $Baseline; B = $Candidate}
$target = @{A = 'e1a4e42d'; B = $CandidateRevision}
if ($JitMode -eq 'fixed') {
    $env:DOTNET_TieredCompilation = '0'
} else {
    foreach ($setting in Get-ChildItem Env: | Where-Object Name -Match '^(DOTNET_|COMPlus_)(TC_|Tiered|QuickJit|ReadyToRun|ZapDisable|Jit|PGO|OSR|AltJit)') {
        [Environment]::SetEnvironmentVariable($setting.Name, $null, 'Process')
    }
}
$Output = [IO.Path]::GetFullPath($Output, (Get-Location).Path)
if ($Stage -ne 'measure') {
    if (!$Baseline -or !$Candidate) { throw 'Supply -Baseline and -Candidate with the prebuilt Release checkout paths.' }
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Probe.csproj')) -or !(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Program.cs'))) {
        throw 'Keep Run-Comparison.ps1, Probe.csproj and Program.cs together.'
    }
    $null = New-Item -ItemType Directory -Path $Output -Force
    if ($ReuseHarnessFrom) {
        $buildDir = (Resolve-Path -LiteralPath $ReuseHarnessFrom).Path
        if (!(Test-Path -LiteralPath (Join-Path $buildDir $assembly))) { throw 'Missing previously compiled driver.' }
    } else {
        $driver = New-Item -ItemType Directory -Path (Join-Path $Output 'harness-src') -Force
        foreach ($file in 'Probe.csproj','Program.cs') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $driver.FullName -Force }
        $buildDir = Join-Path $Output 'harness'
        $deps = Join-Path $Baseline 'src/DurableGraph.Persistence/bin/Release/net10.0'
        $generator = Join-Path $Baseline 'src/DurableGraph.Generator/bin/Release/netstandard2.0/Atelia.DurableGraph.Generator.dll'
        dotnet build (Join-Path $driver.FullName 'Probe.csproj') -c Release -m:1 -nr:false "-p:ProductBin=$deps" "-p:GeneratorDll=$generator" -o $buildDir
    }
    $fixture = $null
    foreach ($arm in 'A','B') {
        $dest = Join-Path $Output $arm
        $null = New-Item -ItemType Directory -Path $dest -Force
        Get-ChildItem -Path $buildDir -File | Copy-Item -Destination $dest
        foreach ($name in $products) {
            Copy-Item -LiteralPath (Join-Path $roots[$arm] "$platform/$name") -Destination $dest -Force
            $path = Join-Path $dest $name
            $version = (Get-Item $path).VersionInfo.ProductVersion
            if (!$version.Contains($target[$arm])) { throw "$arm $name wrong provenance $version" }
        }
        $proof = dotnet (Join-Path $dest $assembly) --check | ConvertFrom-Json
        if ($null -ne $fixture -and $proof.fixtureHash -ne $fixture) { throw 'Arms disagree on classifier results or fixture bytes' }
        $fixture = $proof.fixtureHash
        $proof | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Output "$arm.check.json")
    }
    $manifest = foreach ($file in Get-ChildItem -Path (Join-Path $Output 'A/*.dll') -File) {
        $hashA = (Get-FileHash -LiteralPath $file.FullName).Hash
        $hashB = (Get-FileHash -LiteralPath (Join-Path $Output "B/$($file.Name)")).Hash
        if ($file.Name -notin $products -and $hashA -ne $hashB) { throw "Common dependency mismatch for $($file.Name)" }
        [pscustomobject]@{file=$file.Name;baseline=$hashA;candidate=$hashB}
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'assemblies.json')
    if ($Stage -eq 'setup') { return }
}
if (!$IsWindows) { throw 'The recorded paired run fixes processor affinity on Windows.' }
if (!(Test-Path -LiteralPath (Join-Path $Output 'assemblies.json'))) { throw 'Build both arms with setup first.' }
$rows = foreach ($pair in 1..4) {
    $baselineFirst = [bool]($pair % 2) -eq ($FirstArm -eq 'A')
    $order = if ($baselineFirst) { 'A','B' } else { 'B','A' }
    foreach ($arm in $order) {
        $receipt = dotnet (Join-Path $Output "$arm/$assembly") $CpuMask
        $receipt | Set-Content -LiteralPath (Join-Path $Output "pair-$pair-$arm.json")
        $r = $receipt | ConvertFrom-Json
        if (!$r.runtime.version.Contains($target[$arm]) -or !$r.persistence.version.Contains($target[$arm])) { throw 'Wrong loaded binary' }
        foreach ($sample in $r.samples) {
            [pscustomobject]@{ pair=$pair;arm=$arm;rows=$r.rows*$r.loops;nsPerRow=([double]$sample.ticks / $r.frequency * 1000000000 / ($r.rows * $r.loops));bytes=$sample.bytes;sum=$sample.sum }
        }
    }
}
$rows | Export-Csv -LiteralPath (Join-Path $Output 'samples.csv') -NoTypeInformation

function Mid([double[]]$values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Length % 2) { return $sorted[[int][math]::Floor($sorted.Length/2)] }
    return ($sorted[$sorted.Length/2-1]+$sorted[$sorted.Length/2])/2
}
$raw = Get-ChildItem -LiteralPath $Output -Filter 'pair-*.json' -File | Sort-Object Name
if ($raw.Count -ne 8) { throw 'Expected eight process receipts' }
$hashes = @{}; $fixture = $null; $checksum = $null; $alloc = $null
$pairs = foreach ($pair in 1..4) {
    $med = @{}
    foreach ($arm in 'A','B') {
        $r = Get-Content -LiteralPath (Join-Path $Output "pair-$pair-$arm.json") -Raw | ConvertFrom-Json
        if ($r.samples.Count -ne 5) { throw 'Expected five samples per process' }
        if ($null -ne $fixture -and $r.fixtureHash -ne $fixture) { throw 'Input/output evidence differs' }
        if ($hashes.ContainsKey($arm) -and $hashes[$arm] -ne ($r.runtime.sha256+$r.persistence.sha256)) { throw 'Different runtime across processes' }
        $hashes[$arm] = $r.runtime.sha256+$r.persistence.sha256
        $fixture = $r.fixtureHash
        foreach ($b in $r.samples) {
            if ($null -ne $checksum -and $b.sum -ne $checksum) { throw 'Classifications differ' }
            $checksum = $b.sum
            if ($null -ne $alloc -and $b.bytes -ne $alloc) { Write-Warning 'Allocation varies; inspect raw files' }
            $alloc = $b.bytes
        }
        $ns = @($r.samples | ForEach-Object { $_.ticks * 1e9 / $r.frequency / ($r.rows*$r.loops) })
        $med[$arm] = Mid $ns
    }
    $baselineFirst = [bool]($pair % 2) -eq ($FirstArm -eq 'A')
    [pscustomobject]@{pair=$pair;order=if ($baselineFirst) {'AB'} else {'BA'};A=$med.A;B=$med.B;difference=$med.B-$med.A;percentage=100*($med.B/$med.A-1)}
}
$pairs | Export-Csv -LiteralPath (Join-Path $Output 'pairs.csv') -NoTypeInformation
$pairs | Format-Table
[pscustomobject]@{baselineMedian=Mid $pairs.A;candidateMedian=Mid $pairs.B;pairedMedianPct=Mid $pairs.percentage;pairedMedianNs=Mid $pairs.difference;bytesPerRow=$alloc/($r.rows*$r.loops);sum=$checksum;fixtureHash=$fixture} | ConvertTo-Json
