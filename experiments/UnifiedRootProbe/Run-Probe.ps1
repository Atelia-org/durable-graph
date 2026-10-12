[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ProbeExe,
    [ValidateSet('all', 'commit', 'recovery', 'cost')][string] $Suite = 'all',
    [string] $CaseFilter = '*'
)

$ErrorActionPreference = 'Stop'
$ProbeExe = (Resolve-Path -LiteralPath $ProbeExe).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$evidenceBase = Join-Path $repositoryRoot '.artifacts/db085-root-20260930'
$fixtureBase = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp/db085-root-20260930'))
if ([IO.Path]::GetPathRoot($fixtureBase) -ne 'C:\') { throw 'Fixtures must use the dedicated C: task directory.' }
if ($fixtureBase -notlike 'C:\*db085-root-20260930') { throw 'Unexpected fixture base.' }
[IO.Directory]::CreateDirectory($fixtureBase) | Out-Null
[IO.Directory]::CreateDirectory($evidenceBase) | Out-Null
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$taskRoot = Join-Path $fixtureBase ('run-' + $runId)
$resultRoot = Join-Path $evidenceBase $runId
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
[IO.Directory]::CreateDirectory($resultRoot) | Out-Null
[IO.File]::WriteAllText((Join-Path $taskRoot '.db085-unified-root-fixture'), 'DB-085 unified-root disposable experiment; unique new fixtures only.')
$results = [Collections.Generic.List[object]]::new()
$benchmark = $null
$previousTemp = [Environment]::GetEnvironmentVariable('TEMP', 'Process')
$previousTmp = [Environment]::GetEnvironmentVariable('TMP', 'Process')

function Invoke-Probe([string[]] $ProbeArgs, [string] $KillPoint = '') {
    $executable = if ([IO.Path]::GetExtension($ProbeExe) -eq '.dll') { 'dotnet' } else { $ProbeExe }
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($executable -eq 'dotnet') { $start.ArgumentList.Add($ProbeExe) }
    foreach ($argument in $ProbeArgs) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $lines = [Collections.Generic.List[string]]::new()
    $records = [Collections.Generic.List[object]]::new()
    $wasKilled = $false
    $started = $false
    try {
        if (!$process.Start()) { throw "Could not start probe: $($ProbeArgs -join ' ')" }
        $started = $true
        $errors = $process.StandardError.ReadToEndAsync()
        while ($true) {
            $read = $process.StandardOutput.ReadLineAsync()
            $waits = 0
            while (!$read.Wait(60000)) {
                $waits++
                if ($waits -ge 30) { throw "Timed out waiting for probe: $($ProbeArgs -join ' ')" }
                Write-Host "Waiting for probe PID $($process.Id): $($ProbeArgs[0])"
            }
            $line = $read.Result
            if ($null -eq $line) { break }
            $lines.Add($line)
            $record = $line | ConvertFrom-Json
            $records.Add($record)
            if ($record.type -eq 'READY') {
                if (!$KillPoint -or $record.point -cne $KillPoint -or [int]$record.pid -ne $process.Id) {
                    throw "READY PID/checkpoint mismatch: $line"
                }
                $process.Kill($true)
                $wasKilled = $true
                break
            }
        }
        if (!$process.WaitForExit(60000)) { throw 'Probe did not exit after command or kill.' }
        $stderr = $errors.GetAwaiter().GetResult()
        $log = Join-Path $resultRoot ('child-' + [Guid]::NewGuid().ToString('N') + '.jsonl')
        [IO.File]::WriteAllLines($log, $lines)
        if ($stderr) { [IO.File]::WriteAllText($log + '.stderr', $stderr) }
        if ($KillPoint -and !$wasKilled) { throw "Checkpoint not reached: $KillPoint; $($lines -join ' '); $stderr" }
        if (!$KillPoint -and $process.ExitCode -ne 0) { throw "Probe failed with exit $($process.ExitCode): $($lines -join ' '); $stderr" }
        return [pscustomobject]@{ pid = $process.Id; killed = $wasKilled; exitCode = $process.ExitCode; records = $records.ToArray(); log = $log }
    }
    finally {
        if ($started -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Require-Record($Run, [string] $Type) {
    $found = @($Run.records | Where-Object { $_.type -eq $Type })
    if ($found.Count -ne 1) { throw "Expected exactly one '$Type' record in $($Run.log)." }
    return $found[0]
}

function Get-Inventory([string] $Fixture) {
    $run = Invoke-Probe -ProbeArgs @('inventory', $Fixture)
    $inventory = Require-Record $run 'inventory'
    $paths = @($inventory.files | ForEach-Object { $_.path } | Sort-Object)
    if (($paths -join ',') -ne 'a.rbf,b.rbf,roots.rbf') { throw "Unexpected fixture files: $($paths -join ',')" }
    foreach ($file in $inventory.files) {
        $actual = Join-Path $Fixture $file.path
        $bytes = [IO.File]::ReadAllBytes($actual)
        $complete = [long]$file.completeLength
        if ($complete -lt 0 -or $complete -gt $bytes.LongLength -or [long]$file.length -ne $bytes.LongLength) {
            throw "Invalid inventory length for $actual"
        }
        $prefix = [byte[]]::new([int]$complete)
        [Array]::Copy($bytes, $prefix, $prefix.Length)
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($prefix))
        if ($hash -cne $file.sha256) { throw "Inventory hash mismatch for $actual" }
    }
    return $inventory
}

function Assert-Prefix([string] $Fixture, $Before) {
    $after = Get-Inventory $Fixture
    foreach ($file in $Before.files) {
        $observed = @($after.files | Where-Object { $_.path -eq $file.path })[0]
        if ($null -eq $observed -or [long]$observed.completeLength -lt [long]$file.completeLength) {
            throw "Complete prefix/address removed from $($file.path)"
        }
        $bytes = [IO.File]::ReadAllBytes((Join-Path $Fixture $file.path))
        $prefix = [byte[]]::new([int]$file.completeLength)
        [Array]::Copy($bytes, $prefix, $prefix.Length)
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($prefix))
        if ($hash -cne $file.sha256) { throw "Complete prefix changed in $($file.path)" }
        $originalOffsets = @($file.offsets | ForEach-Object { [long]$_ })
        $newOffsets = @($observed.offsets | Where-Object { [long]$_ -lt [long]$file.completeLength } | ForEach-Object { [long]$_ })
        if (($originalOffsets -join ',') -cne ($newOffsets -join ',')) {
            throw "Complete record addresses changed in $($file.path)"
        }
    }
    return $after
}

function Get-FullSnapshot([string] $Fixture) {
    return @(foreach ($name in @('a.rbf', 'b.rbf', 'roots.rbf', 'repository.lock')) {
        $path = Join-Path $Fixture $name
        [pscustomobject]@{ path = $name; length = (Get-Item -LiteralPath $path).Length;
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    })
}

function Assert-View($Run, [long] $A, [long] $B, [long] $Generation, [string] $Type) {
    $record = Require-Record $Run $Type
    $view = $record.view
    if ($null -eq $view -or [long]$view.aValue -ne $A -or [long]$view.bValue -ne $B -or
        [long]$view.generation -ne $Generation) {
        throw "Wrong or mixed view in $($Run.log): expected ($Generation, $A, $B)."
    }
    return $view
}

function New-Fixture([string] $Name) {
    $fixture = Join-Path $taskRoot ('fixture-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $fixture) { throw 'Fixture path must be new.' }
    $seed = Invoke-Probe -ProbeArgs @('seed', $fixture, '10', '100')
    $old = Assert-View $seed 10 100 0 'seed'
    $inventory = Get-Inventory $fixture
    return [pscustomobject]@{ path = $fixture; seed = $seed; old = $old; seedInventory = $inventory; name = $Name }
}

function Run-CommitCase([string] $Point, [bool] $Published) {
    $name = 'commit-' + $Point.Replace(':', '-')
    if ($name -notlike $CaseFilter) { return }
    $fixture = New-Fixture $name
    $crash = Invoke-Probe -ProbeArgs @('commit', $fixture.path, '20', '200', $Point) -KillPoint $Point
    $afterCrash = Get-Inventory $fixture.path
    $null = Assert-Prefix $fixture.path $fixture.seedInventory
    if ($Point.StartsWith('partial:')) {
        $role = $Point.Substring(8)
        $path = if ($role -eq 'Root') { 'roots.rbf' } else { $role.ToLowerInvariant() + '.rbf' }
        $short = @($afterCrash.files | Where-Object { $_.path -eq $path })[0]
        if ([long]$short.length -le [long]$short.completeLength) { throw "No real short append at $Point" }
    }
    $beforeInspect = Get-FullSnapshot $fixture.path
    $firstInspect = Invoke-Probe -ProbeArgs @('inspect', $fixture.path)
    $selectedGeneration = if ($Published) { 1L } else { 0L }
    $selectedA = if ($Published) { 20L } else { 10L }
    $selectedB = if ($Published) { 200L } else { 100L }
    $firstView = Assert-View $firstInspect $selectedA $selectedB $selectedGeneration 'inspect'
    $afterInspect = Get-FullSnapshot $fixture.path
    if (($beforeInspect | ConvertTo-Json -Depth 5 -Compress) -cne ($afterInspect | ConvertTo-Json -Depth 5 -Compress)) {
        throw 'Normal inspect changed the crash image.'
    }
    $recovery = Invoke-Probe -ProbeArgs @('recover', $fixture.path)
    $null = Require-Record $recovery 'recovery'
    $afterRecovery = Assert-Prefix $fixture.path $afterCrash
    $delivered = Invoke-Probe -ProbeArgs @('inspect', $fixture.path)
    $deliveredView = Assert-View $delivered $selectedA $selectedB $selectedGeneration 'inspect'
    $continued = Invoke-Probe -ProbeArgs @('commit', $fixture.path, '30', '300')
    $continueView = Assert-View $continued 30 300 ($selectedGeneration + 1) 'commit'
    $afterContinue = Assert-Prefix $fixture.path $afterCrash
    $cold = Invoke-Probe -ProbeArgs @('inspect', $fixture.path)
    $coldView = Assert-View $cold 30 300 ($selectedGeneration + 1) 'inspect'
    $null = Assert-Prefix $fixture.path $afterCrash
    $results.Add([pscustomobject]@{ name = $name; kind = 'commit'; fixture = $fixture.path; point = $Point;
        expectedPublished = $Published; seed = $fixture.seed; crash = $crash; firstInspect = $firstInspect;
        recovery = $recovery; deliveredInspect = $delivered; continuation = $continued; coldInspect = $cold;
        seedInventory = $fixture.seedInventory; completeFactsBeforeRecovery = $afterCrash;
        afterRecovery = $afterRecovery; afterContinuation = $afterContinue;
        firstView = $firstView; deliveredView = $deliveredView; continuedView = $continueView; coldView = $coldView })
    Write-Host "PASS $name"
}

function Run-RecoveryCase([string] $FirstPoint, [string] $SecondPoint, [bool] $Published) {
    $name = 'recovery-' + $FirstPoint.Replace(':', '-') + '-' + $SecondPoint
    if ($name -notlike $CaseFilter) { return }
    $fixture = New-Fixture $name
    $firstCrash = Invoke-Probe -ProbeArgs @('commit', $fixture.path, '20', '200', $FirstPoint) -KillPoint $FirstPoint
    $beforeRecovery = Get-Inventory $fixture.path
    $null = Assert-Prefix $fixture.path $fixture.seedInventory
    $secondCrash = Invoke-Probe -ProbeArgs @('recover', $fixture.path, $SecondPoint) -KillPoint $SecondPoint
    $afterSecondCrash = Assert-Prefix $fixture.path $beforeRecovery
    $retry = Invoke-Probe -ProbeArgs @('recover', $fixture.path)
    $null = Require-Record $retry 'recovery'
    $afterRetry = Assert-Prefix $fixture.path $beforeRecovery
    $selectedGeneration = if ($Published) { 1L } else { 0L }
    $selectedA = if ($Published) { 20L } else { 10L }
    $selectedB = if ($Published) { 200L } else { 100L }
    # Inspect immediately after the first successful retry, before any further
    # maintenance or publication can hide an incorrect delivered view.
    $delivered = Invoke-Probe -ProbeArgs @('inspect', $fixture.path)
    $deliveredView = Assert-View $delivered $selectedA $selectedB $selectedGeneration 'inspect'
    $continued = Invoke-Probe -ProbeArgs @('commit', $fixture.path, '30', '300')
    $continuedView = Assert-View $continued 30 300 ($selectedGeneration + 1) 'commit'
    $afterContinue = Assert-Prefix $fixture.path $beforeRecovery
    $cold = Invoke-Probe -ProbeArgs @('inspect', $fixture.path)
    $coldView = Assert-View $cold 30 300 ($selectedGeneration + 1) 'inspect'
    $null = Assert-Prefix $fixture.path $beforeRecovery
    $results.Add([pscustomobject]@{ name = $name; kind = 'recovery'; fixture = $fixture.path;
        firstPoint = $FirstPoint; secondPoint = $SecondPoint; firstCrash = $firstCrash; secondCrash = $secondCrash;
        retry = $retry; deliveredInspect = $delivered; continuation = $continued; coldInspect = $cold;
        seedInventory = $fixture.seedInventory; completeFactsBeforeRecovery = $beforeRecovery;
        afterSecondCrash = $afterSecondCrash; afterRetry = $afterRetry; afterContinuation = $afterContinue;
        deliveredView = $deliveredView; continuedView = $continuedView; coldView = $coldView })
    Write-Host "PASS $name"
}

try {
    [Environment]::SetEnvironmentVariable('TEMP', $taskRoot, 'Process')
    [Environment]::SetEnvironmentVariable('TMP', $taskRoot, 'Process')
    if ($Suite -in @('all', 'commit')) {
        foreach ($point in @('partial:A', 'partial:B', 'partial:Root', 'written:A', 'written:B', 'written:Root',
                'flushed:A', 'flushed:B', 'flushed:Root', 'AfterAppendA', 'AfterFlushA', 'AfterAppendB',
                'AfterFlushB', 'AfterAppendRoot', 'AfterFlushRoot', 'BeforeAck')) {
            $published = $point -in @('written:Root', 'flushed:Root', 'AfterAppendRoot', 'AfterFlushRoot', 'BeforeAck')
            Run-CommitCase $point $published
        }
    }
    if ($Suite -in @('all', 'recovery')) {
        $actionPoints = @('CandidateResourcesDisposed', 'BeforeAction', 'BeforeSetLength',
            'AfterSetLengthBeforeFlush', 'AfterAction', 'BeforeFreshOpen', 'AfterConfirmA',
            'AfterConfirmB', 'AfterConfirmRoot', 'BeforeDelivery')
        foreach ($role in @('A', 'B', 'Root')) {
            foreach ($point in $actionPoints) { Run-RecoveryCase "partial:$role" $point $false }
        }
        foreach ($point in @('CandidateResourcesDisposed', 'BeforeAction', 'BeforeFreshOpen',
                'AfterConfirmA', 'AfterConfirmB', 'AfterConfirmRoot', 'BeforeDelivery')) {
            Run-RecoveryCase 'BeforeAck' $point $true
        }
    }
    if ($Suite -in @('all', 'cost')) {
        $benchParent = Join-Path $taskRoot ('bench-' + [Guid]::NewGuid().ToString('N'))
        $benchmark = Invoke-Probe -ProbeArgs @('measure', $benchParent)
        $measurement = Require-Record $benchmark 'measurement'
        if ($null -eq $measurement.report -or @($measurement.report.samples).Count -ne 24 -or
            @($measurement.report.actionSamples).Count -ne 8) {
            throw 'Measurement matrix must contain 24 no-action and 8 short-tail raw cells including warmups.'
        }
        $measurement.report | ConvertTo-Json -Depth 50 |
            Set-Content -LiteralPath (Join-Path $resultRoot 'measurements.json')
        Write-Host "Measurements: $(Join-Path $resultRoot 'measurements.json')"
    }
    [pscustomobject]@{ status = 'passed'; faultModel = $(if ($Suite -eq 'cost') { 'none; controlled 2B write failure in action samples' } else { 'F1 external process kill' }); f2Claim = $false;
        runId = $runId; fixtureRoot = $taskRoot; probeExecutable = $ProbeExe; suite = $Suite;
        caseFilter = $CaseFilter; count = $results.Count; cases = $results.ToArray(); benchmarkLog = $benchmark.log } |
        ConvertTo-Json -Depth 60 | Set-Content -LiteralPath (Join-Path $resultRoot 'evidence.json')
    Write-Host "Evidence: $(Join-Path $resultRoot 'evidence.json')"
}
catch {
    [pscustomobject]@{ status = 'failed'; error = $_.ToString(); runId = $runId;
        fixtureRoot = $taskRoot; completed = $results.ToArray() } |
        ConvertTo-Json -Depth 60 | Set-Content -LiteralPath (Join-Path $resultRoot 'evidence.json')
    throw
}
finally {
    [Environment]::SetEnvironmentVariable('TEMP', $previousTemp, 'Process')
    [Environment]::SetEnvironmentVariable('TMP', $previousTmp, 'Process')
}
