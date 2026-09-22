[CmdletBinding()]
param([string] $PackageSource, [string] $Version)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw "Supply both -PackageSource and -Version, or neither for a self-contained run."
}
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$probeRoot = Join-Path $PSScriptRoot "ImmutableLeafConsumer"
$generatorProject = Join-Path $probeRoot "SiblingGenerator/SiblingGenerator.csproj"
$familyProject = Join-Path $probeRoot "FamilyConsumer/FamilyConsumer.csproj"
$siblingProject = Join-Path $probeRoot "SiblingConsumer/SiblingConsumer.csproj"

# The Family consumer stays a plain PackageReference consumer (C5). The sibling consumer
# intentionally wires one local analyzer; no other manual wiring is allowed for either.
$familyText = Get-Content -LiteralPath $familyProject -Raw
foreach ($forbidden in @("<Import", "<ProjectReference", "<Analyzer", "<AdditionalFiles")) {
    if ($familyText.Contains($forbidden, [StringComparison]::Ordinal)) { throw "Family consumer contains forbidden manual wiring '$forbidden'." }
}
$siblingText = Get-Content -LiteralPath $siblingProject -Raw
if (-not $siblingText.Contains("<Analyzer", [StringComparison]::Ordinal)) { throw "Sibling consumer must wire the sibling generator through <Analyzer>." }
foreach ($forbidden in @("<Import", "<ProjectReference", "<AdditionalFiles")) {
    if ($siblingText.Contains($forbidden, [StringComparison]::Ordinal)) { throw "Sibling consumer contains forbidden manual wiring '$forbidden'." }
}

$runId = "immutable-leaf-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/$runId"
$packageCache = Join-Path $workRoot "packages"
New-Item -ItemType Directory -Path $packageCache | Out-Null

Push-Location $repositoryRoot
try {
    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        $PackageSource = Join-Path $workRoot "feed"
        New-Item -ItemType Directory -Path $PackageSource | Out-Null
        $Version = "0.0.0-immutable-leaf-e2e.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$PID"
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version
        if (@(Get-ChildItem -LiteralPath $PackageSource -Filter *.nupkg -File).Count -ne 9) { throw "Expected nine dependency packages." }
    }

    # The sibling generator is a real local analyzer DLL; a normal <Analyzer> reference keeps
    # it in the same compiler pass as the packaged durable generator.
    $generatorIntermediate = (Join-Path $workRoot "sibling-generator-obj") + [IO.Path]::DirectorySeparatorChar
    $generatorOutput = (Join-Path $workRoot "sibling-generator-bin") + [IO.Path]::DirectorySeparatorChar
    $generatorProperties = @("-p:BaseIntermediateOutputPath=$generatorIntermediate", "-p:BaseOutputPath=$generatorOutput")
    Invoke-DotNet (@("restore", $generatorProject, "--source", "https://api.nuget.org/v3/index.json", "--packages", $packageCache) + $generatorProperties)
    Invoke-DotNet (@("build", $generatorProject, "--no-restore") + $generatorProperties)
    $siblingAnalyzer = Join-Path $generatorOutput "Debug/netstandard2.0/Atelia.SiblingGenerator.dll"
    if (-not (Test-Path -LiteralPath $siblingAnalyzer -PathType Leaf)) { throw "The sibling analyzer DLL was not produced at '$siblingAnalyzer'." }

    foreach ($consumer in @(
        @{ Name = "Family"; Project = $familyProject; Marker = "ImmutableLeafFamily:Flag:True:Roundtrip:True"; Extra = @(); NeedsDatabase = $true },
        @{ Name = "Sibling"; Project = $siblingProject; Marker = "ImmutableLeafSibling:Flag:False:True"; Extra = @("-p:SiblingAnalyzer=$siblingAnalyzer"); NeedsDatabase = $false }
    )) {
        $history = Join-Path $workRoot "$($consumer.Name)-history"
        $intermediate = (Join-Path $workRoot "$($consumer.Name)-obj") + [IO.Path]::DirectorySeparatorChar
        $output = (Join-Path $workRoot "$($consumer.Name)-bin") + [IO.Path]::DirectorySeparatorChar
        $properties = @(
            "-p:DurableGraphPackageVersion=$Version",
            "-p:DurableGraphSchemaHistoryDirectory=$history",
            "-p:BaseIntermediateOutputPath=$intermediate",
            "-p:BaseOutputPath=$output"
        ) + $consumer.Extra
        Invoke-DotNet (@("restore", $consumer.Project, "--source", $PackageSource, "--packages", $packageCache) + $properties)
        Invoke-DotNet (@("build", $consumer.Project, "--no-restore") + $properties)
        $assembly = Join-Path $output "Debug/net10.0/Atelia.$($consumer.Name)Consumer.dll"
        $runArguments = @($assembly)
        if ($consumer.NeedsDatabase) { $runArguments += (Join-Path $workRoot "$($consumer.Name)-database") }
        $actual = (& dotnet $runArguments | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -ne $consumer.Marker) { throw "$($consumer.Name) consumer failed; output was '$actual'." }
        Write-Host $actual
    }
    Write-Host "ImmutableLeaf package consumer probe passed. Artifacts: $workRoot"
}
finally { Pop-Location }
