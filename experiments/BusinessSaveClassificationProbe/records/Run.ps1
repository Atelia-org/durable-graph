[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineRoot,
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$RunDirectory
)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $RunDirectory){throw 'Use a new run directory'}
$run=[IO.Directory]::CreateDirectory($RunDirectory).FullName
$src=[IO.Directory]::CreateDirectory((Join-Path $run 'harness')).FullName
foreach($file in 'Program.cs','Probe.csproj'){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) $src}
Copy-Item -LiteralPath (Join-Path $BaselineRoot 'global.json') $src
function Invoke-ProbeDotnet([string[]]$Arguments){
    & dotnet @Arguments
    if($LASTEXITCODE){throw "dotnet failed: $Arguments"}
}
function Product([string]$checkout){
    (Resolve-Path (Join-Path $checkout 'tests/DurableGraph.Persistence.Tests/bin/Release/net10.0')).Path
}
$baseline=Product $BaselineRoot
$candidate=Product $CandidateRoot
$generator=(Resolve-Path (Join-Path $BaselineRoot 'src/DurableGraph.Generator/bin/Release/netstandard2.0/Atelia.DurableGraph.Generator.dll')).Path
Push-Location $src
try {
    Invoke-ProbeDotnet @('build','Probe.csproj','-c','Release',"-p:ProductDir=$baseline","-p:GeneratorDll=$generator",'-m:1','-nr:false')
} finally {Pop-Location}
$built=(Join-Path $src 'bin/Release/net10.0')
$name='Atelia.DurableGraph.Persistence.Tests.dll'
$seed=(Join-Path $run 'empty-closed-seed')
Invoke-ProbeDotnet @((Join-Path $built $name),'seed',$seed)
foreach($side in 'baseline','candidate'){
    $dir=[IO.Directory]::CreateDirectory((Join-Path $run $side)).FullName
    $app=Join-Path $dir 'app'
    $store=Join-Path $dir 'repo'
    Copy-Item -LiteralPath $built -Destination $app -Recurse
    Copy-Item -LiteralPath $seed -Destination $store -Recurse
    $product=if ($side -eq 'baseline'){$baseline}else{$candidate}
    foreach($file in (Get-ChildItem -LiteralPath $app -File -Filter '*.dll')){
        if($file.Name -ieq $name){continue}
        $replacement=Join-Path $product $file.Name
        Copy-Item -LiteralPath $replacement $file.FullName -Force
        if((Get-FileHash -LiteralPath $replacement).Hash -cne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "DLL copy failed for $file"}
    }
    Invoke-ProbeDotnet @((Join-Path $app $name),'save-and-read',$store,$dir)
    foreach($entry in (Get-Content -Raw -LiteralPath (Join-Path $dir 'loaded.json') | ConvertFrom-Json)){
        $source=(Get-FileHash -LiteralPath (Join-Path $product ($entry.name+'.dll'))).Hash
        if($source -cne $entry.sha256 -or !(($entry.location).StartsWith($app, [StringComparison]::OrdinalIgnoreCase))){throw "Actual DLL provenance mismatch: $($entry.name)"}
    }
}
$a=(Get-FileHash -LiteralPath (Join-Path $run 'baseline/records.json')).Hash
$b=(Get-FileHash -LiteralPath (Join-Path $run 'candidate/records.json')).Hash
if ($a -cne $b){throw "Receipts differ: $a vs $b. Inspect both."}
Write-Host "Identical State record and cold-open receipts: $a"
