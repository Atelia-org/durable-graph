param(
    [Parameter(Mandatory)][string]$MasterRoot,
    [Parameter(Mandatory)][string]$StorageSourceRoot,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_TieredCompilation = '0'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Select a fresh output directory.' }
$dest = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$research = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$master = (Resolve-Path -LiteralPath $MasterRoot).Path
$storage = (Resolve-Path -LiteralPath $StorageSourceRoot).Path
$lanes = 'master', 'lab-builtin', 'lab-leaf'
foreach ($lane in $lanes) {
    $stage = New-Item -ItemType Directory -Path (Join-Path $dest $lane)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Fanout.csproj'),(Join-Path $PSScriptRoot 'Program.cs') -Destination $stage
    $graph = if ($lane -eq 'master') { $master } else { $research }
    $registered = $lane -eq 'lab-leaf'
    dotnet build (Join-Path $stage 'Fanout.csproj') -c Release -m:1 -nr:false -v:q "-p:GraphRoot=$graph" "-p:RegisteredLeaf=$registered" -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
    if ($LASTEXITCODE) { throw "Failed to build $lane" }
}
$results = @()
for ($round = 0; $round -lt 3; $round++) {
    for ($offset = 0; $offset -lt 3; $offset++) {
        $lane = $lanes[($round + $offset) % $lanes.Length]
        $exe = Join-Path $dest "$lane/bin/Release/net10.0/RegisteredLeaf.Fanout.dll"
        $json = dotnet $exe (Join-Path $dest "store-$round-$lane")
        if ($LASTEXITCODE) { throw "Failed to run $lane" }
        $result = $json | ConvertFrom-Json
        $result | Add-Member -NotePropertyName lane -NotePropertyValue $lane
        $results += $result
        $result | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $dest 'fanout.jsonl')
    }
}
foreach ($lane in $lanes) {
    $group = $results | Where-Object lane -eq $lane
    foreach ($phase in 'hot','cold') {
        $times = @($group | ForEach-Object { $_.$phase.Microseconds } | Sort-Object)
        $bytes = @($group | ForEach-Object { $_.$phase.Bytes } | Sort-Object)
        [pscustomobject]@{ Lane=$lane; Phase=$phase; MedianUs=$times[1]; MedianBytes=$bytes[1]; MinUs=$times[0]; MaxUs=$times[2] }
    }
}
