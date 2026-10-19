[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageSource,
    [Parameter(Mandatory)][string] $Version,
    [string] $WorkDirectory
)

$ErrorActionPreference = 'Stop'
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (!$WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dg-repository-api-' + [Guid]::NewGuid().ToString('N')) }
$workRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory).TrimEnd('\', '/')
foreach ($boundary in @($repositoryRoot, $PackageSource)) {
    $boundary = [IO.Path]::GetFullPath($boundary).TrimEnd('\', '/')
    if ($workRoot.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $workRoot.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use a fresh work directory outside the repository and input feed.'
    }
}
if (Test-Path -LiteralPath $workRoot) { throw 'Use a new work directory; previous cache and evidence are retained.' }
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$cache = Join-Path $workRoot 'packages'
$project = Join-Path $workRoot 'RepositoryApi.csproj'
$receipt = [ordered]@{ Passed = $false; Version = $Version; PackageSource = $PackageSource; WorkDirectory = $workRoot
    Commands = @(); NegativeCases = @(); Packages = @(); Failure = $null }
function Save-Receipt { $receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $workRoot 'result.json') -Encoding utf8NoBOM }
function Execute([string] $Name, [string[]] $Arguments, [bool] $AllowFailure = $false) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $workRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['NUGET_PACKAGES'] = $cache
    $start.Environment['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(180000)) { throw "dotnet $Name timed out after 180 seconds." }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        $log = Join-Path $workRoot ($Name + '.log')
        [IO.File]::WriteAllText($log, $output, [Text.UTF8Encoding]::new($false))
        $receipt.Commands += [ordered]@{ Name = $Name; Arguments = $Arguments; ExitCode = $process.ExitCode; Log = $log
            Sha256 = (Get-FileHash -LiteralPath $log).Hash; ProcessId = $process.Id }
        Save-Receipt
        if (!$AllowFailure -and $process.ExitCode -ne 0) { throw "dotnet $Name failed; see $log" }
        [pscustomobject]@{ Output = $output; ExitCode = $process.ExitCode }
    } finally {
        if (!$process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

Save-Receipt
try {
    foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        '<Project />' | Set-Content -LiteralPath (Join-Path $workRoot $file) -Encoding utf8NoBOM
    }
    $feedXml = [Security.SecurityElement]::Escape($PackageSource)
    $versionXml = [Security.SecurityElement]::Escape($Version)
    "<configuration><packageSources><clear/><add key='probe' value='$feedXml'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources><packageSourceMapping><clear/><packageSource key='probe'><package pattern='Atelia.*'/></packageSource><packageSource key='nuget.org'><package pattern='*'/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>" |
        Set-Content -LiteralPath (Join-Path $workRoot 'NuGet.Config') -Encoding utf8NoBOM
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <DurableGraphGenerateDefinitions>true</DurableGraphGenerateDefinitions><UseSharedCompilation>false</UseSharedCompilation>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Atelia.DurableGraph" Version="$versionXml" />
    <PackageReference Include="Atelia.DurableGraph.Persistence" Version="$versionXml" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8NoBOM
    @'
using Atelia.DurableGraph;
namespace RepositoryApi;
[DurableType("probe.repository.api.state", 1)]
public sealed partial class State : IDurableObject {
    [DurableField(1)] public int Value;
}
'@ | Set-Content -LiteralPath (Join-Path $workRoot 'Models.cs') -Encoding utf8NoBOM
    @'
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using RepositoryApi;

static StateModelRegistry Models() {
    StateModelRegistry models = new();
    Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    return models;
}
string path = Path.GetFullPath(args[0]);
BranchId mainId, otherId, eventsId;
using (Repository repository = Repository.CreateNew(path, Models())) {
    mainId = repository.CreateRootBranchFromState("main", new State { Value = 1 }).Id;
    using WorkingCopy main = repository.OpenWorkingCopy(mainId);
    ((State)main.State!).Value = 2;
    main.CommitState();
    otherId = repository.ForkBranch(main.BaseCommitId, "other").Id;
    using WorkingCopy draft = repository.OpenWorkingCopy(otherId);
    ((State)draft.State!).Value = 3;
    draft.CommitState();
    BranchId forkId = repository.ForkBranch(main.BaseCommitId, "fork").Id;
    using WorkingCopy fork = repository.OpenWorkingCopy(forkId);
    fork.CommitState();
    eventsId = repository.CreateRootBranchFromEvent("events", new State { Value = 4 }).Id;
    using WorkingCopy events = repository.OpenWorkingCopy(eventsId);
    if (events.State is not null) { throw new InvalidDataException("Event-only branch acquired a State."); }
    State firstState = new() { Value = 5 };
    CommitInfo saved = events.CommitState(firstState);
    if (!ReferenceEquals(firstState, events.State) || saved.Id != events.BaseCommitId) {
        throw new InvalidDataException("Successful State publication did not install the supplied instance and actual CommitId.");
    }
}
using (Repository repository = Repository.OpenExisting(path, Models())) {
    using WorkingCopy main = repository.OpenWorkingCopy(mainId);
    if (((State)main.State!).Value != 2) { throw new InvalidDataException("Shared cold value changed."); }
    main.CommitState();
    using WorkingCopy draft = repository.OpenWorkingCopy(otherId);
    if (((State)draft.State!).Value != 3) { throw new InvalidDataException("Forked cold value changed."); }
    draft.CommitState();
}
using (Repository reader = Repository.OpenReadOnlyExisting(path, Models())) {
    if (((State)reader.LoadState(reader.ReadCurrentCommit(eventsId).Commit.Id)).Value != 5) {
        throw new InvalidDataException("Cold first State changed.");
    }
}
Console.WriteLine("RepositoryApi:BranchCommitIds:SharedColdSave:Passed");
'@ | Set-Content -LiteralPath (Join-Path $workRoot 'Program.cs') -Encoding utf8NoBOM
    $null = Execute 'restore' @('restore', $project, '--configfile', (Join-Path $workRoot 'NuGet.Config'), '--packages', $cache, '--disable-parallel')
    $assetsFile = Join-Path $workRoot 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count) { throw 'ProjectReference appeared in consumer assets.' }
    $folders = @($assets.packageFolders.Keys)
    if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ine $cache.TrimEnd('\', '/')) { throw 'Assets must use only the fresh cache.' }
    foreach ($id in @('Atelia.DurableGraph', 'Atelia.DurableGraph.Persistence')) {
        if (!$assets.libraries.ContainsKey($id + '/' + $Version)) { throw "Wrong restored package version: $id" }
    }
    foreach ($key in @($assets.libraries.Keys | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::Ordinal) })) {
        $id, $packageVersion = $key.Split('/')
        $name = $id.ToLowerInvariant() + '.' + $packageVersion.ToLowerInvariant() + '.nupkg'
        $feedPackage = Join-Path $PackageSource ($id + '.' + $packageVersion + '.nupkg')
        $cachePackage = Join-Path $cache ($id.ToLowerInvariant() + '/' + $packageVersion.ToLowerInvariant() + '/' + $name)
        $sha = (Get-FileHash -LiteralPath $feedPackage).Hash
        if ((Get-FileHash -LiteralPath $cachePackage).Hash -cne $sha) { throw "Restored package differs from feed: $key" }
        $receipt.Packages += [ordered]@{ Identity = $key; File = $feedPackage; Sha256 = $sha }
    }
    $receipt['Assets'] = $assetsFile
    $receipt['AssetsSha256'] = (Get-FileHash -LiteralPath $assetsFile).Hash
    $build = @('build', $project, '-c', 'Release', '--no-restore', '-t:Rebuild', '-m:1', '-nr:false', '-p:DurableGraphSchemaHistoryMode=Verify', '-p:UseStorageSources=false')
    $null = Execute 'positive-publish' ($build | ForEach-Object { $_.Replace('DurableGraphSchemaHistoryMode=Verify', 'DurableGraphSchemaHistoryMode=Publish') })
    $cases = @(
        @('old-state-address', '_ = repository.OpenWorkingCopy(default).StateRevisionAddress;', 'CS1061', 'StateRevisionAddress'),
        @('checkout', 'repository.Checkout("main");', 'CS1061', 'Checkout'),
        @('fork', 'repository.Fork("fork", checkpoint);', 'CS1061', 'Fork'),
        @('event-create', 'repository.CreateBranchFromEvent("events", state);', 'CS1061', 'CreateBranchFromEvent'),
        @('mode-property', '_ = repository.SchemaWriteMode;', 'CS1061', 'SchemaWriteMode'),
        @('mode-parameter', '_ = Repository.OpenExisting("path", models, schemaWriteMode: default);', 'CS1739', 'schemaWriteMode'),
        @('mode-type', '_ = typeof(global::Atelia.DurableGraph.SchemaWriteMode);', 'CS0234', 'SchemaWriteMode'),
        @('removed-create', 'repository.CreateBranch("main", checkpoint);', 'CS1061', 'CreateBranch'),
        @('old-state-create', 'repository.CreateSharedBranch("main", state);', 'CS1061', 'CreateSharedBranch'),
        @('old-checkout', 'repository.CheckoutShared("main");', 'CS1061', 'CheckoutShared'),
        @('old-fork', 'repository.ForkShared("fork", checkpoint);', 'CS1061', 'ForkShared'),
        @('address-type', '_ = typeof(global::Atelia.DurableGraph.CheckpointAddress);', 'CS0234', 'CheckpointAddress'),
        @('checkout-type', '_ = typeof(global::Atelia.DurableGraph.BranchCheckout);', 'CS0234', 'BranchCheckout'),
        @('rewind', 'repository.MoveBranch("main", checkpoint, checkpoint);', 'CS1061', 'MoveBranch'),
        @('object-fork', 'repository.ForkBranch(state, "fork");', 'CS1503', 'state'),
        @('private-checkout', 'repository.CheckoutPrivate("main");', 'CS1061', 'CheckoutPrivate'),
        @('promote-state', 'repository.OpenWorkingCopy(default).CommitStateToShared();', 'CS1061', 'CommitStateToShared'),
        @('promote-explicit-state', 'repository.OpenWorkingCopy(default).CommitStateToShared(state);', 'CS1061', 'CommitStateToShared'),
        @('promote-event', 'repository.OpenWorkingCopy(default).CommitEventToShared(state);', 'CS1061', 'CommitEventToShared')
    )
    $negative = Join-Path $workRoot 'Negative.cs'
    foreach ($case in $cases) {
        $line = '    public static void Check(Repository repository, StateModelRegistry models, IDurableObject state, CommitId checkpoint) { ' + $case[1] + ' }'
        @('using Atelia.DurableGraph;', 'using Atelia.DurableGraph.Persistence;', 'internal static class Negative {', $line, '}') |
            Set-Content -LiteralPath $negative -Encoding utf8NoBOM
        $result = Execute ('negative-' + $case[0]) $build $true
        $diagnostics = @([regex]::Matches($result.Output, '(?m)^.*Negative\.cs\((\d+),(\d+)\): error (CS\d+): (.*)$') | ForEach-Object { $_.Value.Trim() } | Select-Object -Unique)
        $position = $line.IndexOf($case[3], [StringComparison]::Ordinal)
        if ($case[0] -eq 'object-fork') { $position = $line.LastIndexOf('state', [StringComparison]::Ordinal) }
        $expected = 'Negative.cs(4,' + ($position + 1) + '): error ' + $case[2] + ':'
        if ($result.ExitCode -eq 0 -or $diagnostics.Count -ne 1 -or !$diagnostics[0].Contains($expected, [StringComparison]::Ordinal)) {
            throw "Expected one $expected diagnostic; see negative-$($case[0]).log"
        }
        $otherErrors = @([regex]::Matches($result.Output, '(?m)^.*: error (?:CS|DG)\d+: .*$') | ForEach-Object { $_.Value.Trim() } | Select-Object -Unique)
        if ($otherErrors.Count -ne 1) { throw 'A compilation negative also failed for unrelated compiler/generator reasons.' }
        $receipt.NegativeCases += [ordered]@{ Name = $case[0]; Code = $case[2]; Line = 4; Column = $position + 1; Symbol = $case[3]; Diagnostic = $diagnostics[0] }
    }
    # Restore legal source and rebuild before execution; no successful stale DLL may qualify this gate.
    [IO.File]::WriteAllText($negative, '// Removed obsolete-call negative; restored legal source.' + [Environment]::NewLine)
    $null = Execute 'restored-positive-verify' $build
    $run = Execute 'run' @((Join-Path $workRoot 'bin/Release/net10.0/RepositoryApi.dll'), (Join-Path $workRoot 'database'))
    if (!$run.Output.Contains('RepositoryApi:BranchCommitIds:SharedColdSave:Passed', [StringComparison]::Ordinal)) { throw 'The restored consumer did not complete its runtime story.' }
    $receipt.Passed = $true
    Write-Host "Repository API package probe passed. Artifacts: $workRoot"
} catch {
    $receipt.Failure = $_.Exception.ToString()
    throw
} finally { Save-Receipt }
