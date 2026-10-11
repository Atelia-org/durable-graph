[CmdletBinding()]
param([string] $PackageSource, [string] $Version)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw "Supply both -PackageSource and -Version, or neither for a self-contained run."
}
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
function Read-History {
    param([string] $Directory, [int] $ExpectedCount, [string] $OwnedId, [hashtable] $Accepted)
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
    $encodedId = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($OwnedId))
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if (-not $text.StartsWith("// durable-graph-schema-history:9`n", [StringComparison]::Ordinal) -or
            -not $text.Contains("// schema-id-base64:$encodedId`n", [StringComparison]::Ordinal)) {
            throw "History must be canonical v9 and owned by this project: '$($file.Name)'."
        }
        $Accepted[$file.Name] = @{
            Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            Bytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName))
        }
    }
}
function Get-Snapshot {
    param([string] $Directory, [bool] $IncludeTimestamp)
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        $stamp = if ($IncludeTimestamp) { $_.LastWriteTimeUtc.Ticks } else { "" }
        "$($_.FullName)|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)|$stamp"
    }) -join "`n"
}
function Invoke-Consumer {
    param([string] $Mode, [string] $Expected)
    $actual = (& dotnet $hostAssembly $Mode $database | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $Expected) { throw "$Mode failed: '$actual'." }
    Write-Host $actual
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$runStamp = "$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/record-class-$runStamp"
$packageCache = Join-Path $workRoot "packages"
$modelFeed = Join-Path $workRoot "model-feed"
$database = Join-Path $workRoot "database"
New-Item -ItemType Directory -Path $packageCache, $modelFeed | Out-Null
$accepted = @{ BaseLibrary = @{}; FactsLibrary = @{}; Host = @{} }
$ownedIds = @{ BaseLibrary = "RecordFact"; FactsLibrary = "RecordDamage"; Host = "RecordWorld" }
$projects = @{}
foreach ($name in @("BaseLibrary", "FactsLibrary", "Host")) {
    $project = Join-Path $PSScriptRoot "RecordClassConsumer/$name/$name.csproj"
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
        $Version = "0.0.0-record-class-e2e.$runStamp"
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version
        if (@(Get-ChildItem -LiteralPath $PackageSource -Filter *.nupkg -File).Count -ne 10) { throw "Expected ten runtime dependency packages." }
    }
    foreach ($generation in @(1, 2, 3)) {
        $modelVersion = "0.0.0-record-class-v$generation.$runStamp"
        foreach ($name in @("BaseLibrary", "FactsLibrary", "Host")) {
            $history = Join-Path $workRoot "$name-history"
            $intermediate = (Join-Path $workRoot "$name-v$generation-obj") + [IO.Path]::DirectorySeparatorChar
            $output = (Join-Path $workRoot "$name-v$generation-bin") + [IO.Path]::DirectorySeparatorChar
            $properties = @(
                "-p:DurableGraphPackageVersion=$Version", "-p:ModelPackageVersion=$modelVersion", "-p:PackageVersion=$modelVersion",
                "-p:HistoryVersion=$generation", "-p:DurableGraphSchemaHistoryDirectory=$history",
                "-p:BaseIntermediateOutputPath=$intermediate", "-p:BaseOutputPath=$output"
            )
            Invoke-DotNet (@("restore", $projects[$name], "--source", $PackageSource, "--source", $modelFeed, "--packages", $packageCache) + $properties)
            Invoke-DotNet (@("build", $projects[$name], "--no-restore") + $properties)
            $count = if ($generation -eq 3 -and $name -eq "FactsLibrary") { 2 } else { 1 }
            Read-History $history $count $ownedIds[$name] $accepted[$name]
            Invoke-DotNet (@("clean", $projects[$name]) + $properties)
            Invoke-DotNet (@("build", $projects[$name], "--no-restore", "-p:DurableGraphSchemaHistoryMode=Verify") + $properties)
            Read-History $history $count $ownedIds[$name] $accepted[$name]
            if ($name -ne "BaseLibrary") {
                $dependency = if ($name -eq "FactsLibrary") { "BaseLibrary" } else { "FactsLibrary" }
                $assets = Get-Content -LiteralPath (Join-Path $intermediate "project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
                $target = @($assets.targets.Values)[0]
                $entry = $target["Atelia.RecordClass$dependency/$modelVersion"]
                if (-not $entry.compile.ContainsKey("ref/net10.0/Atelia.RecordClass$dependency.dll") -or
                    -not $entry.runtime.ContainsKey("lib/net10.0/Atelia.RecordClass$dependency.dll")) {
                    throw "$name did not consume the reference/implementation assembly pair."
                }
            }
            if ($name -eq "Host") {
                $hostAssembly = Join-Path $output "Debug/net10.0/Atelia.RecordClassHost.dll"
            } else {
                Invoke-DotNet (@("pack", $projects[$name], "--no-build", "--configuration", "Debug", "--output", $modelFeed) + $properties)
            }
        }
        if ($generation -eq 1) {
            Invoke-Consumer "seed" "RecordClass:Seed:S0:E1:Pending:True"
        } elseif ($generation -eq 2) {
            Invoke-Consumer "recover" "RecordClass:Resume:S1:Pending:False"
            $before = Get-Snapshot $database $false
            Invoke-Consumer "check" "RecordClass:Reopen:NoReplay:True"
            if ((Get-Snapshot $database $false) -ne $before) { throw "Completed recovery changed persisted bytes." }
            Invoke-Consumer "with" "RecordClass:With:DistinctEqualIdentity:True"
            Invoke-Consumer "check" "RecordClass:Reopen:NoReplay:True"
        } else {
            Invoke-Consumer "upgrade" "RecordClass:Upgrade:BaseThenDelta:ColdReopen:True"
            Invoke-Consumer "final" "RecordClass:FinalProcess:NoUpgrade:True"
        }
        $before = Get-Snapshot $database $true
        Invoke-Consumer "events" "RecordClass:EventOnly:True"
        if ((Get-Snapshot $database $true) -ne $before) { throw "Read-only event browsing changed persisted files." }
    }
    $accepted | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workRoot "independent-history.json")
    Write-Host "RecordClassPackaging:ThreeBuilds:ClassToRecord:HistoryUnchanged:ReferenceAssemblies:True"
    Write-Host "RecordClass package consumer probe passed. Artifacts: $workRoot"
}
finally { Pop-Location }
