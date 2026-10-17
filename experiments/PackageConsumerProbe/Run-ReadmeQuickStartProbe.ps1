[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CandidateDirectory,
    [string] $WorkDirectory
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [version]'7.3') { throw 'PowerShell 7.3 or later is required to stop on every native command failure.' }
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$CandidateDirectory = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$utf8 = [Text.UTF8Encoding]::new($false)
$readme = Get-Content -LiteralPath (Join-Path $repositoryRoot 'README.md') -Raw
function Get-Example([string] $Language, [string] $Marker, [string] $Document = $readme) {
    $pattern = '(?ms)^```' + [regex]::Escape($Language) + '\r?\n(.*?)^```'
    $matches = @([regex]::Matches($Document, $pattern) | Where-Object { $_.Groups[1].Value.Contains($Marker) })
    if ($matches.Count -ne 1) { throw "Expected one $Language block containing '$Marker'." }
    $matches[0].Groups[1].Value
}
function Text-Image([string] $Text) { $Text.Replace("`r`n", "`n").Trim() }
function Images([string] $Directory, [string] $Filter = '*') {
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter $Filter | Sort-Object FullName | ForEach-Object {
        [ordered]@{ File = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
}
function Require-Images([string] $Directory, $Expected, [string] $Filter = '*') {
    if ((@(Images $Directory $Filter) | ConvertTo-Json -Compress) -cne (@($Expected) | ConvertTo-Json -Compress)) { throw "File set or bytes changed: $Directory" }
}
function Zip-Hash([string] $Package, [string] $Entry) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $item = $zip.GetEntry($Entry)
        if (!$item) { throw "Missing package entry $Entry" }
        $stream = $item.Open()
        try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
}

if (!$WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dg readme candidate ' + [Guid]::NewGuid().ToString('N')) }
$workRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory).TrimEnd('\', '/')
foreach ($boundary in @($repositoryRoot, (Join-Path $repositoryRoot '../atelia-storage'), $CandidateDirectory)) {
    $boundary = [IO.Path]::GetFullPath($boundary).TrimEnd('\', '/')
    if ($workRoot.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $workRoot.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use a fresh work directory outside DG, storage and the input candidate.'
    }
}
if (Test-Path -LiteralPath $workRoot) { throw 'Use a new work directory; keep previous databases, caches and receipts.' }
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$moved = Join-Path $workRoot 'moved candidate with spaces'
$cache = Join-Path $workRoot 'packages'
$receipt = [ordered]@{
    Passed = $false; CandidateDirectory = $CandidateDirectory; WorkDirectory = $workRoot; MovedDirectory = $moved
    TestedSdk = '10.0.201'; Packages = @(); Commands = @(); OriginalCommands = $null; Regressions = @(); Failure = $null
}
function Save-Receipt { $receipt | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workRoot 'result.json') -Encoding utf8NoBOM }
function Execute([string] $Executable, [string[]] $Arguments, [string] $Directory, [string] $Log, [string] $CacheDirectory = $cache, [bool] $AllowFailure = $false) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.Environment['NUGET_PACKAGES'] = $CacheDirectory
    $start.Environment['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($Log, $output, $utf8)
        $receipt.Commands += [ordered]@{ Executable = $Executable; Arguments = $Arguments; WorkingDirectory = $Directory
            Cache = $CacheDirectory; ProcessId = $process.Id; ExitCode = $process.ExitCode; Log = $Log; Sha256 = (Get-FileHash -LiteralPath $Log).Hash }
        Save-Receipt
        if ($process.ExitCode -ne 0 -and !$AllowFailure) { throw "Command failed ($($process.ExitCode)); see $Log" }
        [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
    } finally { $process.Dispose() }
}
function Run-DotNet([string] $Name, [string[]] $Arguments) { Execute 'dotnet' $Arguments $moved (Join-Path $workRoot "$Name.log") }

Save-Receipt
try {
    # This qualifies the attached example on one SDK, not an arbitrary offline deployment.
    '{"sdk":{"version":"10.0.201","rollForward":"disable"}}' | Set-Content -LiteralPath (Join-Path $workRoot 'global.json') -Encoding utf8NoBOM
    foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        '<Project />' | Set-Content -LiteralPath (Join-Path $workRoot $name) -Encoding utf8NoBOM
    }
    $missing = Join-Path $workRoot 'deliberately missing feed'
    $escapedMissing = [Security.SecurityElement]::Escape($missing)
    "<configuration><packageSources><clear/><add key='inherited' value='$escapedMissing'/></packageSources><packageSourceMapping><clear/><packageSource key='inherited'><package pattern='*'/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>" |
        Set-Content -LiteralPath (Join-Path $workRoot 'NuGet.Config') -Encoding utf8NoBOM
    $sdk = Execute 'dotnet' @('--version') $workRoot (Join-Path $workRoot 'sdk.log')
    if ($sdk.Output.Trim() -cne '10.0.201') { throw 'This probe qualifies SDK 10.0.201 only.' }

    $inputs = @(Images $CandidateDirectory)
    $authored = @('README.md', 'NuGet.Config', 'packages.sha256', 'QuickStart/QuickStart.csproj', 'QuickStart/Models.cs', 'QuickStart/Program.cs')
    foreach ($required in $authored) { if ($required -cnotin $inputs.File) { throw "Missing authored candidate input: $required" } }
    if (@($inputs | Where-Object { $_.File -notin $authored -and $_.File -notmatch '^feed/[^/]+\.nupkg$' }).Count -ne 0) {
        throw 'Use the final, unused candidate directory without bin/obj/history or other hidden inputs.'
    }
    Copy-Item -LiteralPath $CandidateDirectory -Destination $moved -Recurse
    Require-Images $moved $inputs
    $receipt['Inputs'] = $inputs
    $projectRoot = Join-Path $moved 'QuickStart'
    $project = Join-Path $projectRoot 'QuickStart.csproj'
    $database = Join-Path $moved 'quickstart-world'
    $history = Join-Path $projectRoot 'DurableGraphSchemaHistory'
    $feed = Join-Path $moved 'feed'
    $candidateReadme = Get-Content -LiteralPath (Join-Path $moved 'README.md') -Raw
    $shellMarker = 'dotnet restore QuickStart/QuickStart.csproj --configfile ./NuGet.Config'
    $shell = Get-Example 'powershell' $shellMarker
    if ((Text-Image $shell) -cne (Text-Image (Get-Example 'powershell' $shellMarker $candidateReadme))) { throw 'Root and candidate README commands differ.' }
    $expectedLines = @($shellMarker, 'dotnet run --project QuickStart/QuickStart.csproj --no-restore -- ./quickstart-world', 'dotnet run --project QuickStart/QuickStart.csproj --no-restore -- ./quickstart-world')
    if ((Text-Image $shell) -cne ($expectedLines -join "`n")) { throw 'Review the probe when the documented three-command entry changes.' }
    if ((Text-Image (Get-Example 'xml' '<add key="candidate" value="feed"')) -cne
        (Text-Image (Get-Content -LiteralPath (Join-Path $moved 'NuGet.Config') -Raw))) { throw 'Candidate config differs from root README.' }
    foreach ($example in @(@('Models.cs', 'public partial class Character'), @('Program.cs', 'string path = Path.GetFullPath(args[0]);'))) {
        if ((Text-Image (Get-Example 'csharp' $example[1])) -cne (Text-Image (Get-Content -LiteralPath (Join-Path $projectRoot $example[0]) -Raw))) { throw "Candidate $($example[0]) differs from root README." }
    }
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $versionNodes = @($projectXml.SelectNodes('/Project/PropertyGroup/DurableGraphPackageVersion'))
    if ($versionNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($versionNodes[0].InnerText)) { throw 'Candidate must contain G once in DurableGraphPackageVersion.' }
    $version = $versionNodes[0].InnerText
    [void]$versionNodes[0].ParentNode.RemoveChild($versionNodes[0])
    [xml]$expectedXml = Get-Example 'xml' '<Project Sdk='
    if ($projectXml.OuterXml -cne $expectedXml.OuterXml) { throw 'Candidate project differs from root README beyond its one generated G property.' }
    [xml]$pins = Get-Content -LiteralPath (Join-Path $repositoryRoot 'eng/StorageDependency.props') -Raw
    $storageVersion = $pins.SelectSingleNode('/Project/PropertyGroup/StoragePackageVersion').InnerText
    $storageRevision = $pins.SelectSingleNode('/Project/PropertyGroup/StorageSourceRevision').InnerText
    if ($version -ceq $storageVersion) { throw 'G and S have independent versions.' }
    $receipt['DgVersion'] = $version
    $receipt['StorageVersion'] = $storageVersion
    $receipt['StorageRevision'] = $storageRevision

    $ids = @('Atelia.Binary', 'Atelia.Data', 'Atelia.DurableGraph', 'Atelia.DurableGraph.Persistence', 'Atelia.DurableGraph.Serialization',
        'Atelia.DurableGraph.Storage', 'Atelia.FrameStore', 'Atelia.Primitives', 'Atelia.Rbf', 'Atelia.HeadStore', 'K4os.Compression.LZ4')
    $packages = @(Get-ChildItem -LiteralPath $feed -File -Filter '*.nupkg')
    $hashLines = @(Get-Content -LiteralPath (Join-Path $moved 'packages.sha256') | Where-Object { $_.Trim() })
    if ($packages.Count -ne 11 -or $hashLines.Count -ne 11) { throw 'Require the current example closure: four G, six S and one K4os package.' }
    $dgCommit = $null
    foreach ($id in $ids) {
        $packageVersion = if ($id -ceq 'K4os.Compression.LZ4') { '1.3.8' } elseif ($id.StartsWith('Atelia.DurableGraph')) { $version } else { $storageVersion }
        $package = @($packages | Where-Object Name -ieq "$id.$packageVersion.nupkg")
        if ($package.Count -ne 1) { throw "Missing exact selected package: $id/$packageVersion" }
        $sha = (Get-FileHash -LiteralPath $package[0].FullName).Hash
        if ("$($sha.ToLowerInvariant())  feed/$($package[0].Name)" -cnotin $hashLines) { throw "Candidate checksum differs: $id" }
        $zip = [IO.Compression.ZipFile]::OpenRead($package[0].FullName)
        try {
            $specEntries = @($zip.Entries | Where-Object FullName -like '*.nuspec')
            if ($specEntries.Count -ne 1) { throw "Expected one nuspec: $id" }
            $reader = [IO.StreamReader]::new($specEntries[0].Open())
            try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
            if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $id -or
                $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $packageVersion) { throw "Wrong package identity: $id" }
            $commit = if ($id.StartsWith('Atelia.')) { $metadata.SelectSingleNode('*[local-name()="repository"]').GetAttribute('commit') } else { $null }
            if ($id.StartsWith('Atelia.DurableGraph')) {
                if (!$dgCommit) { $dgCommit = $commit }
                if ($commit -cnotmatch '^[0-9a-f]{40}$' -or $commit -cne $dgCommit) { throw 'DG packages disagree on source commit.' }
            } elseif ($id.StartsWith('Atelia.') -and $commit -ine $storageRevision) { throw "Wrong selected storage source: $id" }
        } finally { $zip.Dispose() }
        $receipt.Packages += [ordered]@{ Id = $id; Version = $packageVersion; File = $package[0].Name; Sha256 = $sha; SourceCommit = $commit }
    }
    & git -C $repositoryRoot cat-file -e "$dgCommit^{commit}"
    if ($LASTEXITCODE -ne 0) { throw 'DG package source commit is not present in this repository.' }
    $receipt['DgSourceCommit'] = $dgCommit

    # Exact old --source shape, with no candidate config in its parent chain.
    $legacy = Join-Path $workRoot 'legacy QuickStart'
    [IO.Directory]::CreateDirectory($legacy) | Out-Null
    Copy-Item -LiteralPath $project -Destination (Join-Path $legacy 'QuickStart.csproj')
    $legacyCache = Join-Path $workRoot 'legacy packages'
    $negative = Execute 'dotnet' @('restore', (Join-Path $legacy 'QuickStart.csproj'), '--source', $feed, '--source', 'https://api.nuget.org/v3/index.json') $legacy (Join-Path $workRoot 'old-source-negative.log') $legacyCache $true
    $nugetErrors = @([regex]::Matches($negative.Output, 'error ([A-Z]+\d+):') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($negative.ExitCode -eq 0 -or $nugetErrors.Count -ne 1 -or $nugetErrors[0] -cne 'NU1100' -or
        $negative.Output -notmatch 'NU1100: Unable to resolve ''Atelia\.DurableGraph \(' -or
        $negative.Output -notmatch 'NU1100: Unable to resolve ''Atelia\.DurableGraph\.Persistence \(' -or
        $negative.Output -notmatch 'PackageSourceMapping is enabled') { throw 'Expected precise old-command NU1100 mapping failures for both direct packages.' }
    $receipt['LegacyNegative'] = 'NU1100 for both direct references under strict inherited mapping'

    # Execute the literal block, without Invoke-DotNet or hidden restore arguments.
    # Native errors terminate this child immediately, including an early restore failure.
    $literalScript = Join-Path $workRoot 'documented commands.ps1'
    [IO.File]::WriteAllText($literalScript, '$ErrorActionPreference = ''Stop''' + "`n" + '$PSNativeCommandUseErrorActionPreference = $true' + "`n" + $shell, $utf8)
    $receipt.OriginalCommands = [ordered]@{ Text = $shell; Script = $literalScript; Sha256 = (Get-FileHash -LiteralPath $literalScript).Hash }
    $shellExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
    $entry = Execute $shellExecutable @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $literalScript) $moved (Join-Path $workRoot 'documented-commands.log')
    $hp = @([regex]::Matches($entry.Output, 'Alice: Hp=(\d+); Revision=') | ForEach-Object { $_.Groups[1].Value })
    if (($hp -join ',') -cne '99,98') { throw "Documented commands must cold-run twice, producing HP 99,98; got $($hp -join ',')." }
    $accepted = @(Images $history '*.dgschema')
    if ($accepted.Count -ne 2) { throw 'Expected only Character and World V1 histories; no Event model in QuickStart.' }
    $receipt['V1History'] = $accepted
    $assets = Get-Content -LiteralPath (Join-Path $projectRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($assets.libraries.Count -ne 11 -or @($assets.libraries.Values | Where-Object type -cne 'package').Count -ne 0 -or
        @($assets.packageFolders.Keys).Count -ne 1 -or [IO.Path]::GetFullPath(@($assets.packageFolders.Keys)[0]).TrimEnd('\', '/') -ine $cache -or
        @($assets.project.restore.sources.Keys).Count -ne 1 -or [IO.Path]::GetFullPath(@($assets.project.restore.sources.Keys)[0]).TrimEnd('\', '/') -ine $feed) { throw 'Assets must contain only eleven packages, one moved local source and one fresh cache.' }
    foreach ($package in $receipt.Packages) {
        if (!$assets.libraries.ContainsKey($package.Id + '/' + $package.Version)) { throw "Wrong resolved version: $($package.Id)" }
        $cached = Join-Path $cache "$($package.Id.ToLowerInvariant())/$($package.Version.ToLowerInvariant())/$($package.Id.ToLowerInvariant()).$($package.Version.ToLowerInvariant()).nupkg"
        if ((Get-FileHash -LiteralPath $cached).Hash -cne $package.Sha256) { throw 'Cache bytes differ from moved candidate.' }
        if ($package.Id.StartsWith('Atelia.')) {
            $assembly = Join-Path $projectRoot "bin/Debug/net10.0/$($package.Id).dll"
            if ((Get-FileHash -LiteralPath $assembly).Hash -cne (Zip-Hash (Join-Path $feed $package.File) "lib/net10.0/$($package.Id).dll")) { throw 'Runtime output differs from selected candidate package.' }
        }
    }
    $receipt['ResolvedPackages'] = @($assets.libraries.Keys | Sort-Object)
    $receipt.Regressions += 'Original commands: restore; independent HP99 and HP98 processes; only two State-model histories'

    $prefix = "using Atelia.DurableGraph;`nusing Atelia.DurableGraph.Persistence;`nusing QuickStart;`n" +
        "string path = args[0];`nvar models = new StateModelRegistry();`n" +
        "Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);`n"
    $browse = $prefix + (Get-Example 'csharp' 'foreach (var frame in history.ReadFrames("main"))') + @'
using (var check = Repository.OpenReadOnlyExisting(path, models)) {
    var frames = check.ReadFrames("main");
    if (frames.Count != 3 || frames.Any(frame => frame.Kind != CheckpointKind.State) ||
        check.EnumerateEvents(check.GetHead("main")).Any() ||
        !frames.Select(frame => ((World)check.ReadState(frame)).Hero.Hp).SequenceEqual(new[] { 100, 99, 98 })) {
        throw new InvalidOperationException("Expected S0/S1/S2 only, with HP100/99/98 and no Event.");
    }
}
'@
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), $browse, $utf8)
    $browseBefore = @(Images $database)
    $browseResult = Run-DotNet 'state-browse' @('run', '--project', $project, '--no-restore', '--', $database)
    $historyHp = @([regex]::Matches($browseResult.Output, 'History: Hp=(\d+)') | ForEach-Object { $_.Groups[1].Value })
    if (($historyHp -join ',') -cne '100,99,98') { throw 'README State history must print HP100,99,98.' }
    Require-Images $database $browseBefore
    $receipt.Regressions += 'State-only browse: exactly S0/S1/S2, HP100/99/98, no Event, read-only database unchanged'

    $fork = $prefix + (Get-Example 'csharp' 'using (var branches = Repository.OpenExisting(')
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), $fork, $utf8)
    $forkResult = Run-DotNet 'fork' @('run', '--project', $project, '--no-restore', '--', $database)
    if ($forkResult.Output -notmatch 'Branches: main=98, alternate=88') { throw 'README historical-State fork and cold continuation failed.' }
    $verifyFork = $prefix + @'
using var check = Repository.OpenReadOnlyExisting(path, models);
var main = check.ReadFrames("main");
var alternate = check.ReadFrames("alternate");
if (main.Count != 3 || alternate.Count != 4 || !main.Take(2).SequenceEqual(alternate.Take(2)) ||
    main.Concat(alternate).Any(frame => frame.Kind != CheckpointKind.State) ||
    check.EnumerateEvents(check.GetHead("main")).Any() || check.EnumerateEvents(check.GetHead("alternate")).Any() ||
    !main.Select(frame => ((World)check.ReadState(frame)).Hero.Hp).SequenceEqual(new[] { 100, 99, 98 }) ||
    !alternate.Select(frame => ((World)check.ReadState(frame)).Hero.Hp).SequenceEqual(new[] { 100, 99, 89, 88 })) {
    throw new InvalidOperationException("Fork/cold save must preserve the shared ancestors, old State and main branch without Events.");
}
Console.WriteLine("StateOnly:main=100,99,98;alternate=100,99,89,88;events=0");
'@
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), $verifyFork, $utf8)
    $verifyBefore = @(Images $database)
    $null = Run-DotNet 'fork-cold-verify' @('run', '--project', $project, '--no-restore', '--', $database)
    Require-Images $database $verifyBefore
    $receipt['StateOnly'] = [ordered]@{ MainHp = @(100, 99, 98); AlternateHp = @(100, 99, 89, 88)
        Events = 0; SharedAncestorCount = 2; IndependentColdVerification = $true }
    $receipt.Regressions += 'Historical-State Fork: S1 HP99 -> HP89 -> cold HP88; independent process verifies old histories and main HP98; no Events'

    $configured = $prefix +
        "using var repository = Repository.OpenExisting(path, models);`n" +
        "using var session = repository.CheckoutShared(""main"");`nWorld world = (World)session.State!;`n" +
        (Get-Example 'csharp' 'var savePolicy = new ReadAmplificationBaseBudgetParameters(') +
        "`nif (world.Hero.Hp != 97 || !ReferenceEquals(world, session.State) || repository.EnumerateEvents(session.Head).Any()) { throw new InvalidOperationException(); }`n" +
        "Console.WriteLine(""ReadmePolicyOverride:Hp=97:Threshold=11:Budget=5"");`n"
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), $configured, $utf8)
    $null = Run-DotNet 'policy' @('run', '--project', $project, '--no-restore', '--', $database)
    $receipt.Regressions += 'State-only policy: HP97, threshold11, budget5, checkout instance retained, still no Event'

    # The optional path introduces the Event model only after the complete State-only story.
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Events.cs'), (Get-Example 'csharp' 'public partial class DamageEvent'), $utf8)
    $events = $prefix + (Get-Example 'csharp' 'using (var events = Repository.OpenExisting(') +
        "`nif (history.EnumerateEvents(end).Count() != 1 || occurrence.Amount != 1 || before.Hero.Hp != 97 ||`n" +
        "    !ReferenceEquals(before, checkpoint.PreviousState) || ((World)history.ReadState(checkpoint.PreviousStateAddress!)).Hero.Hp != 97 ||`n" +
        "    ((World)history.ReadState(end)).Hero.Hp != 96) { throw new InvalidOperationException(); }`n" +
        "before.Hero.Hp = 100;`n" +
        "if (!ReferenceEquals(before, checkpoint.PreviousState) || checkpoint.PreviousState is not World { Hero.Hp: 100 } ||`n" +
        "    ((World)history.ReadState(checkpoint.PreviousStateAddress!)).Hero.Hp != 97) { throw new InvalidOperationException(); }`n"
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), $events, $utf8)
    $null = Run-DotNet 'optional-events' @('run', '--project', $project, '--no-restore', '--', $database)
    $beforeUpgrade = @(Images $history '*.dgschema')
    if ($beforeUpgrade.Count -ne 3) { throw 'Optional Event must add only its one history.' }
    foreach ($entry in $accepted) {
        if ((Get-FileHash -LiteralPath (Join-Path $history $entry.File)).Hash -cne $entry.Sha256) { throw 'Adding Event changed accepted State-model history.' }
    }
    $receipt['EventHistory'] = $beforeUpgrade
    $receipt.Regressions += 'Optional Event: one E/S pair, damage1, previous State HP97 and current HP96; local read mutation preserves history and stable getters'

    $model = Get-Example 'csharp' 'public partial class Character'
    $program = Get-Example 'csharp' 'string path = Path.GetFullPath(args[0]);'
    foreach ($replacement in @(@($model, '[DurableType("World", 1)]'), @($model, 'public partial class World : IDurableObject {'), @($program, 'world.RebuildTransient();'))) {
        if ([regex]::Matches($replacement[0], [regex]::Escape($replacement[1])).Count -ne 1) { throw 'Review the Upgrade fixture when the README model or initialization point changes.' }
    }
    $model = $model.Replace('[DurableType("World", 1)]', '[DurableType("World", 2)]')
    $model = $model.Replace('public partial class World : IDurableObject {', "public partial class World : IDurableObject {`n    [DurableField(3)] public int Day;")
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Models.cs'), $model, $utf8)
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Upgrades.cs'), (Get-Example 'csharp' 'public static class WorldUpgrades'), $utf8)
    $checkedProgram = $program.Replace('world.RebuildTransient();', 'if (world.Day != 1) { throw new InvalidOperationException("Upgrade did not initialize Day."); }' + "`nworld.RebuildTransient();")
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), $checkedProgram, $utf8)
    $upgrade = Run-DotNet 'upgrade' @('run', '--project', $project, '--no-restore', '--', $database)
    if ($upgrade.Output -notmatch 'Alice: Hp=95; Revision=') { throw 'README V2 must produce HP95 after the optional Event story.' }
    if (@(Images $history '*.dgschema').Count -ne 4) { throw 'Expected only one additional World V2 history.' }
    foreach ($entry in $beforeUpgrade) {
        if ((Get-FileHash -LiteralPath (Join-Path $history $entry.File)).Hash -cne $entry.Sha256) { throw 'Upgrade changed accepted V1 history.' }
    }
    $acceptedV2 = @(Images $history '*.dgschema')
    $receipt['V2History'] = $acceptedV2
    $receipt.Regressions += 'Upgrade: HP95, Day1, original Character/World/Event histories unchanged'
    $null = Run-DotNet 'clean' @('clean', $project, '-v:q')
    $null = Run-DotNet 'verify' @('build', $project, '--no-restore', '-p:DurableGraphSchemaHistoryMode=Verify', '-v:q')
    Require-Images $history $acceptedV2 '*.dgschema'
    $receipt.Regressions += 'Clean/Verify: all four accepted histories unchanged'
    Require-Images $feed @($inputs | Where-Object File -like 'feed/*' | ForEach-Object { [ordered]@{ File = $_.File.Substring(5); Sha256 = $_.Sha256 } })
    Require-Images $CandidateDirectory $inputs
    $receipt.Passed = $true
    Write-Host "README QuickStart candidate probe passed. Artifacts: $workRoot"
} catch {
    $receipt.Failure = $_.Exception.ToString()
    throw
} finally { Save-Receipt }
