[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $LegacyPackageSource,
    [Parameter(Mandatory)][string] $LegacyVersion,
    [Parameter(Mandatory)][string] $PackageSource,
    [Parameter(Mandatory)][string] $Version
)

# The legacy source below is an intentional, isolated input witness. No product compatibility shell is used.
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
function Assert-History {
    param([string] $Directory, [hashtable] $Accepted)
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter *.dgschema -File)
    if ($files.Count -ne 1) { throw "Expected exactly one unchanged World v1 history record." }
    foreach ($file in $files) {
        $bytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName))
        if ($Accepted.Count -ne 0 -and $Accepted[$file.Name] -ne $bytes) {
            throw "Marker migration changed accepted history '$($file.Name)'."
        }
        $Accepted[$file.Name] = $bytes
    }
}
function Invoke-Consumer {
    param([string] $Assembly, [string] $Mode, [string] $Expected)
    $actual = (& dotnet $Assembly $Mode $database | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $Expected) { throw "$Mode failed: '$actual'." }
    Write-Host $actual
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$LegacyPackageSource = (Resolve-Path -LiteralPath $LegacyPackageSource).Path
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
if ($LegacyVersion -eq $Version) { throw "Use distinct immutable package versions for the two implementations." }
$stamp = "$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/durable-base-migration-$stamp"
$database = Join-Path $workRoot "database"
$accepted = @{}
$utf8 = [Text.UTF8Encoding]::new($false)
$projectText = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>Atelia.MarkerMigrationConsumer</AssemblyName>
    <RootNamespace>MarkerMigrationConsumer</RootNamespace>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DurableGraphGenerateDefinitions>true</DurableGraphGenerateDefinitions>
    <WarningsAsErrors>$(WarningsAsErrors);CS0433;CS0436</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Model.cs" />
    <Compile Include="Program.cs" />
    <PackageReference Include="Atelia.DurableGraph" Version="$(DurableGraphPackageVersion)" />
    <PackageReference Include="Atelia.DurableGraph.StateStore" Version="$(DurableGraphPackageVersion)" />
  </ItemGroup>
</Project>
'@
$modelText = @'
using Atelia.DurableGraph;
namespace MarkerMigrationConsumer;
[DurableType("MarkerMigrationWorld", 1)]
public partial class World : __MARKER__ {
    [DurableField(1)] public long Value;
    [DurableField(2)] public long A = 11;
    [DurableField(3)] public long B = 22;
    [DurableField(4)] public long C = 33;
    [DurableField(5)] public long D = 44;
    [DurableField(6)] public long E = 55;
    [DurableField(7)] public long F = 66;
    [DurableField(8)] public long G = 77;
}
'@
$programText = @'
using Atelia.DurableGraph;
using Atelia.DurableGraph.StateStore;
using Atelia.DurableGraph.StateStore.Storage;
using Atelia.RbfSegmentStore;
using MarkerMigrationConsumer;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

if (args.Length != 2) throw new ArgumentException("Expected seed|continue|check and a directory.");
string mode = args[0], directory = Path.GetFullPath(args[1]);
var runtime = typeof(DurableTypeAttribute).Assembly;
bool legacy = __LEGACY__;
Require((runtime.GetType("Atelia.DurableGraph.DurableBase") is not null) == legacy,
    "Expected old exported base only in the actual legacy runtime package.");
Require((runtime.GetType("Atelia.DurableGraph.IDurableObject") is not null) != legacy,
    "Expected marker only in the new runtime package.");
var options = new RbfSegmentStoreOptions { NewStoreLayout = RbfSegmentStoreLayout.Flat };
var models = new StateModelRegistry();
Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
if (mode == "seed") {
    Require(legacy, "Seed must run against the real old package.");
    using var repository = EventHistoryRepository.CreateNew(directory, options);
    using var session = repository.CreateBranch("main", new World { Value = 100 }, models);
    session.CommitDomainEvent(new World { Value = 3 });
    Require(session.State.Value == 100 && session.PendingEvent is World { Value: 3 }, "Old E1 capture failed.");
    Console.WriteLine("MarkerMigration:Legacy:S0:E1:Pending:True");
} else if (mode == "continue") {
    Require(!legacy, "Continuation must use the new marker runtime.");
    FrameAddress revision;
    using (var repository = EventHistoryRepository.OpenExisting(directory, options)) {
        using var session = repository.Resume<World>("main", models);
        Require(session.State.Value == 100 && session.PendingEvent is World { Value: 3 }, "Cannot resume old S0/E1.");
        CheckFields(session.State);
        session.State.Value += ((World)session.PendingEvent!).Value;
        revision = session.CommitDomainState(new ReadAmplificationBaseBudgetParameters(int.MaxValue, 1)).RevisionAddress;
        Require(session.PendingEvent is null && session.State.Value == 103, "New S1 did not install.");
    }
    using (var segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), options)) {
        __STATE_DECLARATION__ states = new StateRevisionStore(segments);
        var rows = states.Read(revision).LocalObjects;
        Require(rows.Count == 1 && rows[0].Kind == ObjectVersionKind.Delta,
            "Unchanged-schema, same-instance continuation must produce a real object Delta.");
    }
    Console.WriteLine("MarkerMigration:New:ResumeOld:SameSchema:Delta:True");
} else if (mode == "check") {
    Require(!legacy, "Final cold read must use the new marker runtime.");
    using var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, options);
    var frames = repository.ReadFrames("main");
    Require(frames.Count == 3, "Expected S0/E1/S1.");
    var state = repository.ReadState<World>(frames.Last(), models);
    var old = repository.ReadState<World>(frames.First(), models);
    var fact = repository.ReadEvent<World>(repository.ReadEvents("main").Single(), models);
    Require(state.Value == 103 && old.Value == 100 && fact.Value == 3, "Cold old/new versions were not preserved.");
    CheckFields(state);
    CheckFields(old);
    Console.WriteLine("MarkerMigration:ColdRead:OldBase:NewDelta:Event:True");
} else throw new ArgumentException("Unknown mode.");
static void CheckFields(World world) => Require(world.A == 11 && world.B == 22 && world.C == 33 &&
    world.D == 44 && world.E == 55 && world.F == 66 && world.G == 77, "Untouched fields changed.");
static void Require(bool condition, string message) {
    if (!condition) throw new InvalidOperationException(message);
}
'@

Push-Location $repositoryRoot
try {
    foreach ($generation in @("legacy", "current")) {
        $isLegacy = $generation -eq "legacy"
        $root = Join-Path $workRoot $generation
        $history = Join-Path $root "history"
        New-Item -ItemType Directory -Path $root, $history | Out-Null
        $project = Join-Path $root "Consumer.csproj"
        # Keep the legacy package and namespace templates bound to the real pre-marker implementation.
        $generationProject = $projectText
        $generationProgram = $programText
        if (-not $isLegacy) {
            $generationProject = $generationProject.Replace("Atelia.DurableGraph.StateStore", "Atelia.DurableGraph.Persistence")
            $generationProgram = $generationProgram.Replace("Atelia.DurableGraph.StateStore.Storage", "Atelia.DurableGraph.Storage").Replace("Atelia.DurableGraph.StateStore", "Atelia.DurableGraph.Persistence")
            # DB-077 moves the registry to Open only in the current package lane.
            $generationProgram = $generationProgram.Replace(', models)', ')')
            foreach ($method in @('CreateNew', 'OpenExisting', 'OpenReadOnlyExisting')) {
                $generationProgram = $generationProgram.Replace("EventHistoryRepository.$method(directory, options)", "EventHistoryRepository.$method(directory, models, options)")
            }
        }
        [IO.File]::WriteAllText($project, $generationProject, $utf8)
        $marker = if ($isLegacy) { "DurableBase" } else { "IDurableObject" }
        $legacyLiteral = if ($isLegacy) { "true" } else { "false" }
        # DB-067 introduced Store-owned caches and IDisposable; the actual older package has neither.
        $stateDeclaration = if ($isLegacy) { "var" } else { "using var" }
        [IO.File]::WriteAllText((Join-Path $root "Model.cs"), $modelText.Replace("__MARKER__", $marker), $utf8)
        [IO.File]::WriteAllText((Join-Path $root "Program.cs"), $generationProgram.Replace("__LEGACY__", $legacyLiteral).Replace("__STATE_DECLARATION__", $stateDeclaration), $utf8)
        if (-not $isLegacy) {
            foreach ($file in Get-ChildItem -LiteralPath (Join-Path $workRoot "legacy/history") -Filter *.dgschema -File) {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $history $file.Name)
            }
            Assert-History $history $accepted
        }
        $feed = if ($isLegacy) { $LegacyPackageSource } else { $PackageSource }
        $packageVersion = if ($isLegacy) { $LegacyVersion } else { $Version }
        $properties = @("-p:DurableGraphPackageVersion=$packageVersion", "-p:DurableGraphSchemaHistoryDirectory=$history")
        Invoke-DotNet (@("restore", $project, "--source", $feed, "--packages", (Join-Path $root "packages")) + $properties)
        Invoke-DotNet (@("build", $project, "--no-restore", "-p:DurableGraphSchemaHistoryMode=Publish") + $properties)
        Assert-History $history $accepted
        Invoke-DotNet (@("clean", $project, "-v:q") + $properties)
        Invoke-DotNet (@("build", $project, "--no-restore", "-p:DurableGraphSchemaHistoryMode=Verify", "-v:q") + $properties)
        Assert-History $history $accepted
        $assembly = Join-Path $root "bin/Debug/net10.0/Atelia.MarkerMigrationConsumer.dll"
        if ($isLegacy) {
            Invoke-Consumer $assembly "seed" "MarkerMigration:Legacy:S0:E1:Pending:True"
        } else {
            Invoke-Consumer $assembly "continue" "MarkerMigration:New:ResumeOld:SameSchema:Delta:True"
            $before = @(Get-ChildItem -LiteralPath $database -Recurse -File | Sort-Object FullName | ForEach-Object {
                "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
            }) -join "`n"
            Invoke-Consumer $assembly "check" "MarkerMigration:ColdRead:OldBase:NewDelta:Event:True"
            $after = @(Get-ChildItem -LiteralPath $database -Recurse -File | Sort-Object FullName | ForEach-Object {
                "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
            }) -join "`n"
            if ($before -ne $after) { throw "Read-only cold verification changed persisted files." }
        }
    }
    Assert-History (Join-Path $workRoot "legacy/history") $accepted
    $accepted | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workRoot "unchanged-history.json")
    Write-Host "MarkerMigration:TwoRealPackages:PublishCleanVerify:HistoryBytesUnchanged:True"
    Write-Host "DurableBase migration package probe passed. Artifacts: $workRoot"
}
finally { Pop-Location }
