[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageSource,
    [Parameter(Mandatory)][string] $Version
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'PackageProbeSupport.ps1')
function Get-Example {
    param([string] $Language, [string] $Marker)
    $pattern = '(?ms)^```' + [regex]::Escape($Language) + '\r?\n(.*?)^```'
    $matches = @([regex]::Matches($readme, $pattern) | Where-Object { $_.Groups[1].Value.Contains($Marker) })
    if ($matches.Count -ne 1) { throw "Expected one $Language block containing '$Marker'." }
    return $matches[0].Groups[1].Value
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
$readme = Get-Content -LiteralPath (Join-Path $repositoryRoot "README.md") -Raw
$runId = "readme-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$workRoot = Join-Path $PSScriptRoot "obj/$runId"
$projectRoot = Join-Path $workRoot "QuickStart"
$project = Join-Path $projectRoot "QuickStart.csproj"
$database = Join-Path $workRoot "database"
$history = Join-Path $projectRoot "DurableGraphSchemaHistory"
$packageCache = Join-Path $workRoot "packages"
$utf8 = [Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Path $projectRoot | Out-Null
$model = Get-Example "csharp" 'public partial class Character'
$program = Get-Example "csharp" 'string path = Path.GetFullPath(args[0]);'
[IO.File]::WriteAllText($project, (Get-Example "xml" '<Project Sdk='), $utf8)
[IO.File]::WriteAllText((Join-Path $projectRoot "Models.cs"), $model, $utf8)
[IO.File]::WriteAllText((Join-Path $projectRoot "Program.cs"), $program, $utf8)
$properties = @("-p:DurableGraphPackageVersion=$Version")
Push-Location $repositoryRoot
try {
    Invoke-DotNet (@("restore", $project, "--source", $PackageSource, "--packages", $packageCache) + $properties)
    foreach ($hp in @(99, 98)) {
        $actual = (& dotnet run --project $project --no-restore @properties -- $database | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -notmatch "Alice: Hp=$hp; Revision=") { throw "README run expected HP=$hp; got '$actual'." }
        Write-Host "ReadmeQuickStart:Hp=$hp"
    }
    $accepted = @{}
    foreach ($file in Get-ChildItem -LiteralPath $history -Filter *.dgschema -File) {
        $accepted[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    if ($accepted.Count -ne 3) { throw "Expected the three README V1 schemas." }

    # Apply only the two model edits described in README, then compile its exact Upgrade block.
    $model = $model.Replace('[DurableType("World", 1)]', '[DurableType("World", 2)]')
    $model = $model.Replace('public partial class World : IDurableObject {', "public partial class World : IDurableObject {`n    [DurableField(3)] public int Day;")
    [IO.File]::WriteAllText((Join-Path $projectRoot "Models.cs"), $model, $utf8)
    [IO.File]::WriteAllText((Join-Path $projectRoot "Upgrades.cs"), (Get-Example "csharp" 'public static class WorldUpgrades'), $utf8)
    $checkedProgram = $program.Replace('world.RebuildTransient();', 'if (world.Day != 1) { throw new InvalidOperationException("Upgrade did not initialize Day."); }' + "`nworld.RebuildTransient();")
    [IO.File]::WriteAllText((Join-Path $projectRoot "Program.cs"), $checkedProgram, $utf8)
    $actual = (& dotnet run --project $project --no-restore @properties -- $database | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -notmatch 'Alice: Hp=97; Revision=') { throw "README V2 run failed: '$actual'." }
    if (@(Get-ChildItem -LiteralPath $history -Filter *.dgschema -File).Count -ne 4) { throw "Expected only the new World V2 schema." }
    foreach ($name in $accepted.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $history $name) -Algorithm SHA256).Hash -ne $accepted[$name]) {
            throw "README Upgrade changed accepted history '$name'."
        }
    }
    Write-Host "ReadmeQuickStart:Hp=97:UpgradeDay=1:HistoryPreserved"

    # Compile the browsing snippet unchanged, with only its surrounding program context supplied.
    $browse = "using Atelia.DurableGraph;`nusing Atelia.DurableGraph.Persistence;`nusing QuickStart;`n" +
        "string path = args[0];`nvar models = new StateModelRegistry();`n" +
        "Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);`n" +
        (Get-Example "csharp" 'using var history = Repository.OpenReadOnlyExisting(path, models);') +
        "`nif (events.Count != 3 || pair.First is not World || pair.Second is not DamageEvent) { throw new InvalidOperationException(); }`n"
    [IO.File]::WriteAllText((Join-Path $projectRoot "Program.cs"), $browse, $utf8)
    Invoke-DotNet (@("run", "--project", $project, "--no-restore") + $properties + @("--", $database))

    # Execute the exact policy-override snippet against the same freshly packaged API.
    $configured = "using Atelia.DurableGraph;`nusing Atelia.DurableGraph.Persistence;`nusing QuickStart;`n" +
        "string path = args[0];`nvar models = new StateModelRegistry();`n" +
        "Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);`n" +
        "using var repository = Repository.OpenExisting(path, models);`n" +
        "using var session = repository.Checkout(""main"");`nWorld world = (World)session.State!;`n" +
        (Get-Example "csharp" 'var savePolicy = new ReadAmplificationBaseBudgetParameters(') +
        "`nif (world.Hero.Hp != 96 || !ReferenceEquals(world, session.State)) { throw new InvalidOperationException(); }`n" +
        "Console.WriteLine(""ReadmePolicyOverride:Hp=96:Threshold=11:Budget=5"");`n"
    [IO.File]::WriteAllText((Join-Path $projectRoot "Program.cs"), $configured, $utf8)
    Invoke-DotNet (@("run", "--project", $project, "--no-restore") + $properties + @("--", $database))
    Invoke-DotNet (@("clean", $project, "-v:q") + $properties)
    Invoke-DotNet (@("build", $project, "--no-restore", "-p:DurableGraphSchemaHistoryMode=Verify", "-v:q") + $properties)
    Write-Host "README QuickStart package probe passed. Artifacts: $workRoot"
}
finally { Pop-Location }
