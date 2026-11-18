[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageSource,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $ExpectedSourceCommit,
    [Parameter(Mandatory)][string] $WorkDirectory,
    [string] $TextDatabase
)
$ErrorActionPreference = 'Stop'
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
foreach ($boundary in @((Resolve-Path (Join-Path $PSScriptRoot '../..')).Path, $PackageSource)) {
    $boundary = [IO.Path]::GetFullPath($boundary).TrimEnd('\','/')
    if ($work.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $work.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use an evidence directory outside the source repository and feed.'
    }
}
if (Test-Path -LiteralPath $work) { throw 'Choose a new evidence directory.' }
if ($ExpectedSourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'Full expected package source SHA required.' }
[IO.Directory]::CreateDirectory($work) | Out-Null
$cache = Join-Path $work 'packages'
$receipt = [ordered]@{ Passed = $false; Version = $Version; SourceCommit = $ExpectedSourceCommit; Commands = @(); Packages = @(); Lanes = @(); Failure = $null }
function Save-Receipt { $receipt | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $work 'result.json') -Encoding utf8NoBOM }
function Run([string] $Name, [string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $work; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['NUGET_PACKAGES'] = $cache
    foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(300000)) { throw "$Name timeout." }
        $log = Join-Path $work ($Name + '.log')
        [IO.File]::WriteAllText($log, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        $receipt.Commands += @{ Name = $Name; Arguments = $Arguments; ExitCode = $process.ExitCode; ProcessId = $process.Id; Log = $log }
        Save-Receipt
        if ($process.ExitCode -ne 0) { throw "$Name failed: $log" }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}
try {
    foreach ($name in 'Directory.Build.props','Directory.Build.targets','Directory.Packages.props') { '<Project />' | Set-Content (Join-Path $work $name) }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DurableTextReaderAdmission/Program.cs') -Destination $work
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DurableTextReaderAdmission/Models.cs') -Destination $work
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DurableTextReaderAdmission/DurableTextReaderAdmission.csproj') -Destination $work
    $source = [Security.SecurityElement]::Escape($PackageSource)
    "<configuration><packageSources><clear/><add key='feed' value='$source'/></packageSources><packageSourceMapping><clear/><packageSource key='feed'><package pattern='*'/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>" | Set-Content (Join-Path $work 'NuGet.Config')
    $project = Join-Path $work 'DurableTextReaderAdmission.csproj'
    $properties = @("-p:DurableGraphPackageVersion=$Version", '-p:UseStorageSources=false', '-p:UseSharedCompilation=false')
    Run 'restore' (@('restore',$project,'--configfile',(Join-Path $work 'NuGet.Config'),'--packages',$cache,'--disable-parallel') + $properties)
    Run 'build' (@('build',$project,'-c','Release','--no-restore','-t:Rebuild','-m:1','-nr:false') + $properties)
    $assets = Get-Content (Join-Path $work 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Values | Where-Object type -eq 'project').Count) { throw 'Unexpected ProjectReference.' }
    if (@($assets.packageFolders.Keys).Count -ne 1 -or [IO.Path]::GetFullPath(@($assets.packageFolders.Keys)[0]).TrimEnd('\','/') -ine $cache.TrimEnd('\','/')) { throw 'Unexpected cache.' }
    $binary = Join-Path $work 'bin/Release/net10.0'
    $hashes = @{}
    $dg = @('Atelia.DurableGraph','Atelia.DurableGraph.Persistence','Atelia.DurableGraph.Serialization','Atelia.DurableGraph.Storage')
    $keys = @($assets.libraries.Keys | Where-Object { $_.StartsWith('Atelia.') })
    if ($keys.Count -ne 10) { throw 'Expected four DG and six storage packages.' }
    foreach ($key in $keys) {
        $id,$v = $key.Split('/')
        $package = Join-Path $PackageSource "$id.$v.nupkg"
        $hash = (Get-FileHash $package -Algorithm SHA256).Hash
        $cached = Join-Path $cache ($id.ToLowerInvariant() + '/' + $v.ToLowerInvariant() + '/' + $id.ToLowerInvariant() + '.' + $v.ToLowerInvariant() + '.nupkg')
        if ((Get-FileHash $cached -Algorithm SHA256).Hash -cne $hash) { throw "Cached package mismatch: $key" }
        $zip = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            $entry = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })[0]
            $reader = [IO.StreamReader]::new($entry.Open())
            try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $commit = [string]$spec.package.metadata.repository.commit
            if ($dg -contains $id -and ($v -cne $Version -or $commit -ine $ExpectedSourceCommit)) { throw "Wrong DG identity: $key" }
            $dll = $zip.GetEntry("lib/net10.0/$id.dll").Open()
            try { $hashes[$id] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($dll)) } finally { $dll.Dispose() }
        } finally { $zip.Dispose() }
        if ((Get-FileHash (Join-Path $binary "$id.dll") -Algorithm SHA256).Hash -cne $hashes[$id]) { throw "Output DLL mismatch: $id" }
        $receipt.Packages += @{ Identity=$key; Sha256=$hash; SourceCommit=$commit; RuntimeSha256=$hashes[$id] }
    }
    $assembly = Join-Path $binary 'DurableTextReaderAdmission.dll'
    $control = Join-Path $work 'old-control'
    $lanes = @(@{ Name='seed'; Mode='seed'; Database=$control }, @{ Name='control-read'; Mode='read'; Database=$control })
    if ($TextDatabase) { $lanes += @{ Name='text-read'; Mode='read'; Database=(Resolve-Path -LiteralPath $TextDatabase).Path } }
    foreach ($lane in $lanes) {
        $evidence = Join-Path $work ($lane.Name + '.json')
        Run $lane.Name @($assembly,$lane.Mode,$lane.Database,$evidence)
        $actual = Get-Content $evidence -Raw | ConvertFrom-Json
        foreach ($loaded in $actual.LoadedAssemblies) {
            if (!$hashes.ContainsKey($loaded.Name) -or $hashes[$loaded.Name] -cne $loaded.Sha256 -or
                [IO.Path]::GetFullPath($loaded.Path) -ine (Join-Path $binary ($loaded.Name+'.dll'))) { throw "Wrong loaded bytes: $($loaded.Name)" }
        }
        if ($lane.Name -eq 'text-read') {
            $messages = $actual.Exceptions.Message -join ' '
            if (!$actual.Rejected -or $messages -notmatch '(?i)(unknown|unsupported)' -or $messages -notmatch '(?i)(type|schema|ABI|format)') {
                throw "Expected explicit unsupported-format/type rejection, observed $($actual.Stage): $messages"
            }
        } elseif ($actual.Rejected -or $actual.Stage -cne 'Completed' -or ($lane.Name -eq 'control-read' -and $actual.Observed -cne 'plain-before-text')) { throw "Old positive control failed: $($lane.Name)" }
        $receipt.Lanes += @{ Name=$lane.Name; Evidence=$evidence; ProcessId=$actual.ProcessId; Stage=$actual.Stage; Rejected=$actual.Rejected; Exceptions=$actual.Exceptions }
    }
    if (@($receipt.Lanes.ProcessId | Select-Object -Unique).Count -ne $lanes.Count) { throw 'Expected distinct processes.' }
    $receipt['TextAdmissionExecuted'] = [bool]$TextDatabase
    $receipt['Assembly'] = $assembly
    $receipt.Passed = $true
} catch { $receipt.Failure=$_.Exception.ToString(); throw } finally { Save-Receipt }
