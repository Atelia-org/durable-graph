[CmdletBinding()]
param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot 'bin/Debug/net10.0/Atelia.DurableGraph.Persistence.Tests.dll'),
    [string]$RunRoot,
    [ValidateRange(5, 300)][int]$ReadyTimeoutSeconds = 60,
    [ValidateRange(5, 600)][int]$HostTimeoutSeconds = 120,
    [ValidateRange(5, 60)][int]$StopTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$assembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
$dotnetExecutable = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$assetsPath = Join-Path $PSScriptRoot 'obj/project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw 'Build the probe in explicit source mode before running this controller; project.assets.json is missing.'
}
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$sourceProjects = @()
foreach ($name in @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Binary', 'Atelia.Rbf', 'Atelia.FrameStore', 'Atelia.VersionStore')) {
    $matches = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name.Split('/')[0] -eq $name })
    if ($matches.Count -ne 1 -or $matches[0].Value.type -ne 'project') {
        throw "This source-only witness requires an actual ProjectReference for $name."
    }
    $projectPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $matches[0].Value.path))
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "Missing source project: $projectPath" }
    $sourceProjects += [ordered]@{ Name = $name; Path = $projectPath }
}
[xml]$storageProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'eng/StorageDependency.props') -Raw
$storageRevision = [string]$storageProperties.Project.PropertyGroup.StorageSourceRevision
$frameStoreProject = ($sourceProjects | Where-Object { $_.Name -eq 'Atelia.FrameStore' }).Path
$storageRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $frameStoreProject) '../..'))
$actualStorageRevision = (& git -C $storageRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $actualStorageRevision -ne $storageRevision) {
    throw 'The actual storage source checkout does not match eng/StorageDependency.props.'
}
$storageStatus = (& git -C $storageRoot status --porcelain | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $storageStatus.Length -ne 0) { throw 'The pinned storage source checkout must be clean.' }
$dgRevision = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot record the DurableGraph source revision.' }
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $runName = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N')
    $RunRoot = Join-Path $repositoryRoot ('.artifacts/MetaBootstrapCrashProbe/' + $runName)
}
$runDirectory = [IO.Path]::GetFullPath($RunRoot)
if (Test-Path -LiteralPath $runDirectory) { throw 'RunRoot must be fresh; this controller never overwrites an earlier fixture.' }
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$receiptPath = Join-Path $runDirectory 'receipt.json'
$inputHashes = @()
foreach ($file in @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in '.cs', '.csproj', '.ps1', '.md' })) {
    $inputHashes += [ordered]@{ Path = $file.FullName; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$assemblyHashes = @()
foreach ($file in @(Get-ChildItem -LiteralPath (Split-Path -Parent $assembly) -Filter 'Atelia.*.dll' -File)) {
    $assemblyHashes += [ordered]@{ Path = $file.FullName; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$receipt = [ordered]@{
    ProtocolVersion = 1
    Scope = 'ProcessCrashOnly'
    StorageMode = 'Source'
    BuildProperties = [ordered]@{ UseStorageSources = $true; BuildsPerformedByController = 0 }
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    CompletedUtc = $null
    ControllerProcessId = $PID
    DurableGraphRevision = $dgRevision
    StorageRevision = $actualStorageRevision
    StorageSourceProjects = $sourceProjects
    AssemblyPath = $assembly
    AssetsPath = $assetsPath
    AssetsSha256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash
    InputHashes = $inputHashes
    AssemblyHashes = $assemblyHashes
    RunRoot = $runDirectory
    Windows = @()
    Passed = $false
    Failure = $null
}

function Save-Receipt {
    $temporary = $receiptPath + '.tmp'
    [IO.File]::WriteAllText($temporary, ($receipt | ConvertTo-Json -Depth 24), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $receiptPath, $true)
}

function Start-ProbeProcess {
    param([string[]]$HostArguments, [string]$LogPrefix, [switch]$ReadyProtocol)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnetExecutable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $repositoryRoot
    $start.ArgumentList.Add($assembly)
    foreach ($argument in $HostArguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $errorTask = $process.StandardError.ReadToEndAsync()
    $outputTask = if ($ReadyProtocol) { $process.StandardOutput.ReadLineAsync() } else { $process.StandardOutput.ReadToEndAsync() }
    return [pscustomobject]@{
        Process = $process
        OutputTask = $outputTask
        ErrorTask = $errorTask
        ReadyProtocol = [bool]$ReadyProtocol
        ProcessId = $process.Id
        StartedUtc = [DateTime]::UtcNow.ToString('O')
        Stdout = $LogPrefix + '.stdout.log'
        Stderr = $LogPrefix + '.stderr.log'
    }
}

function Wait-ProbeExit {
    param($Execution, [int]$TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $Execution.Process.WaitForExit(100)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "Process $($Execution.ProcessId) did not exit within $TimeoutSeconds seconds." }
    }
}

function Kill-ProbeProcess {
    param($Execution)
    if (-not $Execution.Process.HasExited) { $Execution.Process.Kill($true) }
    Wait-ProbeExit $Execution $StopTimeoutSeconds
}

function Finish-ProbeLogs {
    param($Execution)
    $output = $Execution.OutputTask.GetAwaiter().GetResult()
    if ($Execution.ReadyProtocol) {
        $output = [string]$output + [Environment]::NewLine + $Execution.Process.StandardOutput.ReadToEnd()
    }
    [IO.File]::WriteAllText($Execution.Stdout, [string]$output, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($Execution.Stderr, [string]($Execution.ErrorTask.GetAwaiter().GetResult()), [Text.UTF8Encoding]::new($false))
    return [ordered]@{
        ProcessId = $Execution.ProcessId
        StartedUtc = $Execution.StartedUtc
        ExitedUtc = [DateTime]::UtcNow.ToString('O')
        ExitCode = $Execution.Process.ExitCode
        Stdout = $Execution.Stdout
        Stderr = $Execution.Stderr
    }
}

function Invoke-ProbeHost {
    param([string[]]$HostArguments, [string]$LogPrefix, [System.Collections.IDictionary]$Lane, [string]$Role)
    $execution = Start-ProbeProcess -HostArguments $HostArguments -LogPrefix $LogPrefix
    $Lane[$Role] = [ordered]@{ ProcessId = $execution.ProcessId; StartedUtc = $execution.StartedUtc; Stdout = $execution.Stdout; Stderr = $execution.Stderr }
    Save-Receipt
    try {
        Wait-ProbeExit $execution $HostTimeoutSeconds
        $result = Finish-ProbeLogs $execution
        $Lane[$Role] = $result
        if ($result.ExitCode -ne 0) { throw "Host process $($result.ProcessId) failed with exit $($result.ExitCode); see $($result.Stderr)." }
        return $result
    } finally {
        if (-not $execution.Process.HasExited) { Kill-ProbeProcess $execution; $Lane[$Role] = Finish-ProbeLogs $execution }
        $execution.Process.Dispose()
        Save-Receipt
    }
}

function Read-Result {
    param([string]$Path, [int]$ExpectedProcessId)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "The successful host did not produce $Path." }
    $result = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($result.Passed -isnot [bool] -or -not $result.Passed -or [int]$result.ProcessId -ne $ExpectedProcessId) {
        throw "The host result is not a successful receipt for process $ExpectedProcessId."
    }
    return $result
}

$windows = @(
    @{ Name = 'AfterMetaAppend'; Expected = 'Old' },
    @{ Name = 'AfterBusinessAppend'; Expected = 'Old' },
    @{ Name = 'AfterFirstCuEnd'; Expected = 'Old' },
    @{ Name = 'AfterAllCuEnd'; Expected = 'New' },
    @{ Name = 'AfterPublicationConfirmed'; Expected = 'New' },
    @{ Name = 'BeforeInstall'; Expected = 'New' }
)
Save-Receipt
try {
    foreach ($window in $windows) {
        $directory = Join-Path $runDirectory $window.Name
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $fixture = Join-Path $directory 'repo'
        $seedPath = Join-Path $directory 'seed.json'
        $readyPath = Join-Path $directory 'ready.json'
        $verifyPath = Join-Path $directory 'verify.json'
        $lane = [ordered]@{ Window = $window.Name; Expected = $window.Expected; Fixture = $fixture; Seed = $null; Writer = $null; Verifier = $null; Passed = $false }
        $receipt.Windows += $lane
        $lane.Seed = Invoke-ProbeHost -HostArguments @('seed', '--repo', $fixture, '--result', $seedPath) -LogPrefix (Join-Path $directory 'seed') -Lane $lane -Role 'Seed'
        $seed = Read-Result -Path $seedPath -ExpectedProcessId $lane.Seed.ProcessId
        $lane.Seed['Result'] = $seedPath
        Save-Receipt

        $writer = Start-ProbeProcess -HostArguments @('writer', '--repo', $fixture, '--window', $window.Name, '--ready', $readyPath) -LogPrefix (Join-Path $directory 'writer') -ReadyProtocol
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
            while (-not $writer.OutputTask.IsCompleted -or -not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
                if ($writer.Process.HasExited) { throw "Writer exited before READY at $($window.Name)." }
                if ([DateTime]::UtcNow -ge $deadline) { throw "Writer did not reach READY at $($window.Name) within $ReadyTimeoutSeconds seconds." }
                Start-Sleep -Milliseconds 50
            }
            if ($writer.OutputTask.GetAwaiter().GetResult() -ne 'READY' -or $writer.Process.HasExited) {
                throw "Writer did not park at READY for $($window.Name)."
            }
            $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
            if ($ready.Protocol -ne 'DG.MetaBootstrapCrashProbe.v1' -or $ready.Protocol -ne $seed.Protocol -or
                $ready.Window -ne $window.Name -or [int]$ready.ProcessId -ne $writer.ProcessId -or
                [string]::IsNullOrWhiteSpace([string]$ready.OldState) -or [string]::IsNullOrWhiteSpace([string]$ready.OldSchema) -or
                [string]::IsNullOrWhiteSpace([string]$ready.PublicationStage) -or @($ready.CapturedFrames).Count -eq 0) {
                throw "READY evidence does not match the controlled writer/window $($window.Name)."
            }
            if ($ready.OldState -ne $seed.State -or $ready.OldSchema -ne $seed.Schema -or
                $ready.RootId -ne $seed.RootId -or $ready.ListId -ne $seed.ListId) {
                throw "Writer did not start from the completed seed roots and object IDs at $($window.Name)."
            }
            $readyObservedUtc = [DateTime]::UtcNow.ToString('O')
            Kill-ProbeProcess $writer # External process termination: the writer never executes normal Dispose.
            $lane.Writer = Finish-ProbeLogs $writer
            $lane.Writer['Ready'] = $readyPath
            $lane.Writer['ReadySha256'] = (Get-FileHash -LiteralPath $readyPath -Algorithm SHA256).Hash
            $lane.Writer['ReadyObservedUtc'] = $readyObservedUtc
            $lane.Writer['Termination'] = 'ExternalProcessKillEntireTree'
            if ($lane.Writer.ExitCode -eq 0) { throw 'A parked writer returned exit0 instead of being externally terminated.' }
        } finally {
            if (-not $writer.Process.HasExited) { Kill-ProbeProcess $writer; $lane.Writer = Finish-ProbeLogs $writer }
            elseif ($null -eq $lane.Writer) { $lane.Writer = Finish-ProbeLogs $writer }
            $writer.Process.Dispose()
            Save-Receipt
        }

        $lane.Verifier = Invoke-ProbeHost -HostArguments @('verify', '--repo', $fixture, '--window', $window.Name, '--ready', $readyPath, '--result', $verifyPath) -LogPrefix (Join-Path $directory 'verify') -Lane $lane -Role 'Verifier'
        $verified = Read-Result -Path $verifyPath -ExpectedProcessId $lane.Verifier.ProcessId
        if ($lane.Verifier.ProcessId -eq $lane.Writer.ProcessId -or [int]$verified.WriterProcessId -ne $lane.Writer.ProcessId -or
            $verified.Window -ne $window.Name -or $verified.ObservedOldOrNew -ne $window.Expected) {
            throw "Fresh verifier did not confirm the expected old/new pair at $($window.Name)."
        }
        foreach ($check in @('SingleDelta', 'ExactPrior', 'IdsPreserved', 'HVerified', 'CompletedFramesPreserved', 'ReadOnlyFilesUnchanged', 'NoNonNoneRecovery')) {
            if ($verified.$check -isnot [bool] -or -not $verified.$check) { throw "Verifier failed $check at $($window.Name)." }
        }
        $lane.Verifier['Result'] = $verifyPath
        $lane.Verifier['ObservedOldOrNew'] = $verified.ObservedOldOrNew
        $lane.Verifier['SelectedState'] = $verified.SelectedState
        $lane.Verifier['SelectedSchema'] = $verified.SelectedSchema
        $lane.Verifier['ContinuationState'] = $verified.ContinuationState
        $lane.Passed = $true
        Save-Receipt
        Write-Output "ProcessCrashOnly=$($window.Name); Expected=$($window.Expected); Passed=True"
    }
    $receipt.Passed = $true
} catch {
    $receipt.Failure = $_.Exception.ToString()
    throw
} finally {
    $receipt.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    Save-Receipt
    Write-Output "Receipt=$receiptPath"
}
