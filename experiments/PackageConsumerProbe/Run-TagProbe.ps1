[CmdletBinding()]
param([string] $PackageSource, [string] $Version)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageSource) -ne [string]::IsNullOrWhiteSpace($Version)) {
    throw 'Supply both -PackageSource and -Version, or neither for a self-contained run.'
}
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$consumerProject = Join-Path $PSScriptRoot 'TagConsumer/TagConsumer.csproj'
$runId = "tags-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/$runId"
$packageCache = Join-Path $workRoot 'packages'
$history = Join-Path $workRoot 'history'
$database = Join-Path $workRoot 'database'
$intermediate = (Join-Path $workRoot 'consumer-obj') + [IO.Path]::DirectorySeparatorChar
$output = (Join-Path $workRoot 'consumer-bin') + [IO.Path]::DirectorySeparatorChar
$consumerText = Get-Content -LiteralPath $consumerProject -Raw
foreach ($forbidden in @('<Import', '<ProjectReference', '<Analyzer', '<AdditionalFiles')) {
    if ($consumerText.Contains($forbidden, [StringComparison]::Ordinal)) { throw "Consumer contains forbidden manual wiring '$forbidden'." }
}
[void](New-Item -ItemType Directory -Path $packageCache)
Push-Location $repositoryRoot
try {
    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        $PackageSource = Join-Path $workRoot 'feed'
        [void](New-Item -ItemType Directory -Path $PackageSource)
        $Version = "0.0.0-tags-e2e.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$PID"
        Prepare-DurableGraphProbeFeed -RepositoryRoot $repositoryRoot -OutputDirectory $PackageSource -Version $Version
    }
    # The helper and package dependency metadata own storage component versions. They
    # may differ; this consumer never forces one version onto all storage packages.
    if (@(Get-ChildItem -LiteralPath $PackageSource -Filter '*.nupkg' -File).Count -ne 9) {
        throw 'Expected the complete nine-package isolated feed.'
    }
    $properties = @(
        "-p:DurableGraphPackageVersion=$Version",
        "-p:DurableGraphSchemaHistoryDirectory=$history",
        "-p:BaseIntermediateOutputPath=$intermediate",
        "-p:BaseOutputPath=$output"
    )
    Invoke-DotNet (@('restore', $consumerProject, '--source', $PackageSource, '--packages', $packageCache) + $properties)
    Invoke-DotNet (@('build', $consumerProject, '--no-restore') + $properties)
    $schemas = @(Get-ChildItem -LiteralPath $history -Filter '*.dgschema' -File)
    if ($schemas.Count -ne 2) { throw 'Expected exactly the State and Event generated schema histories.' }
    $accepted = @{}
    foreach ($schema in $schemas) { $accepted[$schema.Name] = (Get-FileHash -LiteralPath $schema.FullName -Algorithm SHA256).Hash }
    Invoke-DotNet (@('build', $consumerProject, '--no-restore', '-p:DurableGraphSchemaHistoryMode=Verify') + $properties)
    foreach ($schema in $schemas) {
        if ((Get-FileHash -LiteralPath $schema.FullName -Algorithm SHA256).Hash -ne $accepted[$schema.Name]) {
            throw "History Verify modified '$($schema.Name)'."
        }
    }
    $assembly = Join-Path $output 'Debug/net10.0/Atelia.TagConsumer.dll'
    foreach ($lane in @(
        @{ Name = 'seed'; Expected = 'TagsSeed:EventFirst:State:AdvancedBranch' },
        @{ Name = 'metadata'; Expected = 'TagsMetadata:EmptyRegistry:ReadOnly:EventFirst' },
        @{ Name = 'continue'; Expected = 'TagsContinue:RefCheckout:NamedFork:EventFirstState:OldOwnerRejected:MoveStable' },
        @{ Name = 'verify'; Expected = 'TagsVerify:ColdValues:Cycles:ImmutableTags' }
    )) {
        # Separate processes cannot accidentally reuse a repository owner, DTO cache,
        # generated model instance table, or an address from the preceding lane.
        $actual = (& dotnet $assembly $lane.Name $database | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -cne $lane.Expected) {
            throw "Tag consumer lane '$($lane.Name)' failed; output was '$actual'."
        }
        Write-Host $actual
    }
    Write-Host "Tag package consumer probe passed. Artifacts: $workRoot"
}
finally { Pop-Location }
