[CmdletBinding()]
param([string] $PackageSource, [string] $Version)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw "Supply both -PackageSource and -Version, or neither for a self-contained run."
}
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
function Get-Snapshot {
    param([string] $Directory, [bool] $IncludeTimestamp)
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        $stamp = if ($IncludeTimestamp) { $_.LastWriteTimeUtc.Ticks } else { "" }
        "$($_.FullName)|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)|$stamp"
    }) -join "`n"
}
function Invoke-Consumer {
    param([string] $Mode, [string] $Directory, [string] $Expected)
    $actual = (& dotnet $assembly $Mode $Directory | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $Expected) { throw "$Mode failed: '$actual'." }
    Write-Host $actual
}
function Test-PackageDocumentation {
    $xmlName = "Atelia.DurableGraph.Persistence.xml"
    $restoredXml = Join-Path $packageCache "atelia.durablegraph.persistence/$Version/lib/net10.0/$xmlName"
    $restoredDll = [IO.Path]::ChangeExtension($restoredXml, ".dll")
    if (-not (Test-Path -LiteralPath $restoredXml) -or -not (Test-Path -LiteralPath $restoredDll)) {
        throw "The restored Persistence package must contain adjacent DLL and XML documentation."
    }
    $packagePath = Join-Path $PackageSource "Atelia.DurableGraph.Persistence.$Version.nupkg"
    $zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entry = $zip.GetEntry("lib/net10.0/$xmlName")
        if ($null -eq $entry) { throw "The nupkg has no facade XML documentation." }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $packedXml = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
    if ($packedXml -cne [IO.File]::ReadAllText($restoredXml)) { throw "Restored XML differs from the actual package." }
    [xml] $documentation = $packedXml
    $expected = @{
        'M:Atelia.DurableGraph.Repository.CreateNew(' = 1
        'M:Atelia.DurableGraph.Repository.OpenExisting(' = 1
        'M:Atelia.DurableGraph.Repository.OpenReadOnlyExisting(' = 1
        'M:Atelia.DurableGraph.Repository.CreateBranch(' = 2
        'M:Atelia.DurableGraph.Repository.CreateBranchFromEvent(' = 1
        'M:Atelia.DurableGraph.Repository.Checkout(' = 1
        'M:Atelia.DurableGraph.Repository.ReadFrames(' = 1
        'M:Atelia.DurableGraph.Repository.ReadEvents(' = 1
        'M:Atelia.DurableGraph.Repository.ReadState(' = 1
        'M:Atelia.DurableGraph.Repository.ReadEvent(' = 1
        'M:Atelia.DurableGraph.Repository.ReadPair(' = 1
        'P:Atelia.DurableGraph.Repository.IsFaulted' = 1
        'M:Atelia.DurableGraph.BranchCheckout.CommitEvent(' = 1
        'M:Atelia.DurableGraph.BranchCheckout.CommitState(' = 2
        'P:Atelia.DurableGraph.BranchCheckout.State' = 1
        'P:Atelia.DurableGraph.BranchCheckout.IsFaulted' = 1
        'T:Atelia.DurableGraph.Persistence.GraphCommitException' = 1
        'T:Atelia.DurableGraph.CheckpointAddress' = 1
    }
    foreach ($prefix in $expected.Keys) {
        $members = @($documentation.doc.members.member | Where-Object {
            if ($prefix.EndsWith('(')) { $_.name.StartsWith($prefix, [StringComparison]::Ordinal) }
            else { $_.name -ceq $prefix }
        })
        if ($members.Count -ne $expected[$prefix]) { throw "Missing documentation member(s): $prefix" }
        foreach ($member in $members) {
            if ([string]::IsNullOrWhiteSpace([string]$member.summary) -or [string]::IsNullOrWhiteSpace($member.remarks.InnerText ?? [string]$member.remarks)) {
                throw "Missing summary/remarks for $($member.name)."
            }
            if ($member.name.StartsWith('M:Atelia.DurableGraph.Repository.ReadPair', [StringComparison]::Ordinal)) {
                $remarks = ($member.remarks.InnerText ?? [string]$member.remarks) -replace '\s+', ' '
                foreach ($required in @('Transient mutation', 'outside the graphs', 'without a writer', 'Checkout')) {
                    if (-not $remarks.Contains($required, [StringComparison]::Ordinal)) {
                        throw "Missing ReadPair Transient contract '$required': $($member.name)."
                    }
                }
            }
        }
    }
    Write-Host "FacadeDocumentation:Packaged:Restored:MembersVerified"
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$consumerProject = Join-Path $PSScriptRoot "EventHistoryRecoveryConsumer/EventHistoryRecoveryConsumer.csproj"
$runId = "event-recovery-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/$runId"
$packageCache = Join-Path $workRoot "packages"
$history = Join-Path $workRoot "history"
$intermediate = (Join-Path $workRoot "consumer-obj") + [IO.Path]::DirectorySeparatorChar
$output = (Join-Path $workRoot "consumer-bin") + [IO.Path]::DirectorySeparatorChar
$consumerText = Get-Content -LiteralPath $consumerProject -Raw
foreach ($forbidden in @("<Import", "<ProjectReference", "<Analyzer", "<AdditionalFiles")) {
    if ($consumerText.Contains($forbidden, [StringComparison]::Ordinal)) { throw "Forbidden manual package wiring '$forbidden'." }
}
New-Item -ItemType Directory -Path $packageCache | Out-Null
Push-Location $repositoryRoot
try {
    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        $PackageSource = Join-Path $workRoot "feed"
        New-Item -ItemType Directory -Path $PackageSource | Out-Null
        $Version = "0.0.0-event-recovery-e2e.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$PID"
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version
        if (@(Get-ChildItem -LiteralPath $PackageSource -Filter *.nupkg -File).Count -ne 9) { throw "Expected nine packages." }
    }
    $properties = @(
        "-p:DurableGraphPackageVersion=$Version",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:BaseIntermediateOutputPath=$intermediate",
        "-p:BaseOutputPath=$output"
    )
    Invoke-DotNet (@("restore", $consumerProject, "--source", $PackageSource, "--packages", $packageCache) + $properties)
    Test-PackageDocumentation
    Invoke-DotNet (@("build", $consumerProject, "--no-restore") + $properties)
    $acceptedHistory = Get-Snapshot $history $true
    Invoke-DotNet (@("build", $consumerProject, "--no-restore", "-p:DurableGraphSchemaHistoryMode=Verify") + $properties)
    if ((Get-Snapshot $history $true) -ne $acceptedHistory) { throw "Verify changed accepted schema history." }
    if (@(Get-ChildItem -LiteralPath $history -Filter *.dgschema -File).Count -ne 4) { throw "Expected four history records." }
    $assembly = Join-Path $output "Debug/net10.0/Atelia.EventHistoryRecoveryConsumer.dll"
    foreach ($pathKind in @("hot", "cold")) {
        $database = Join-Path $workRoot "database-$pathKind"
        if ($pathKind -eq "hot") {
            Invoke-Consumer "hot" $database "Completed=True:StateHp=7:Pending=False"
        } else {
            Invoke-Consumer "init-record" $database "Recorded:Pending:StateHp=10:EventHp=10"
            Invoke-Consumer "recover" $database "Completed=True:StateHp=7:Pending=False"
        }
        $beforeRecovery = Get-Snapshot $database $false
        Invoke-Consumer "recover" $database "Completed=False:StateHp=7:Pending=False"
        if ((Get-Snapshot $database $false) -ne $beforeRecovery) { throw "Repeated recovery appended or changed persisted data." }
        $beforeRead = Get-Snapshot $database $true
        Invoke-Consumer "verify" $database "Verified:EventOnly:Hp=10:Observations=ready,armed"
        if ((Get-Snapshot $database $true) -ne $beforeRead) { throw "Readonly event browsing changed files." }
        Invoke-Consumer "next" $database "Completed=True:StateHp=4:Pending=False"
        $beforeRead = Get-Snapshot $database $true
        Invoke-Consumer "verify" $database "Verified:EventOnly:Hp=10:Observations=ready,armed"
        if ((Get-Snapshot $database $true) -ne $beforeRead) { throw "Reading old Event after next E/S changed files." }
    }
    Write-Host "EventHistory recovery package consumer probe passed. Artifacts: $workRoot"
}
finally { Pop-Location }
