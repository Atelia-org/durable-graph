[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ProbeExe,
    [ValidateSet('all', 'candidate', 'baseline', 'cost')][string] $Suite = 'all',
    [string] $CaseFilter = '*'
)

$ErrorActionPreference = 'Stop'
$ProbeExe = (Resolve-Path -LiteralPath $ProbeExe).Path
$fixtureBase = 'C:\Users\gdtut\AppData\Local\Temp\db085-metadata-20260930'
$taskRoot = Join-Path $fixtureBase ([Guid]::NewGuid().ToString('N'))
if ([IO.Path]::GetFullPath($taskRoot) -notlike 'C:\Users\gdtut\AppData\Local\Temp\db085-metadata-20260930\*' -or
    (Test-Path -LiteralPath $taskRoot)) { throw 'Expected a new dedicated C: task directory.' }
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
[IO.File]::WriteAllText((Join-Path $taskRoot '.db085-metadata-fixture'), 'DB-085 disposable metadata and rotation experiment')
$evidenceRoot = Join-Path $taskRoot 'evidence'
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$results = [Collections.Generic.List[object]]::new()
$previousTemp = [Environment]::GetEnvironmentVariable('TEMP', 'Process')
$previousTmp = [Environment]::GetEnvironmentVariable('TMP', 'Process')

function Invoke-Probe([string[]] $ProbeArgs, [string] $KillPoint = '', [bool] $AllowFailure = $false) {
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
                if ($waits -ge 15) { throw "Timed out waiting for PID $($process.Id): $($ProbeArgs -join ' ')" }
                Write-Host "Waiting for PID $($process.Id): $($ProbeArgs[0])"
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
        if (!$process.WaitForExit(60000)) { throw "PID $($process.Id) did not exit." }
        $stderr = $errors.GetAwaiter().GetResult()
        $log = Join-Path $evidenceRoot ('child-' + [Guid]::NewGuid().ToString('N') + '.jsonl')
        [IO.File]::WriteAllLines($log, $lines)
        if ($stderr) { [IO.File]::WriteAllText($log + '.stderr', $stderr) }
        if ($KillPoint -and !$wasKilled) { throw "Checkpoint not reached: $KillPoint; $($lines -join ' '); $stderr" }
        if (!$KillPoint -and !$AllowFailure -and $process.ExitCode -ne 0) {
            throw "Probe failed with exit $($process.ExitCode): $($lines -join ' '); $stderr"
        }
        return [pscustomobject]@{ pid = $process.Id; killed = $wasKilled; exitCode = $process.ExitCode;
            records = $records.ToArray(); log = $log }
    }
    finally {
        if ($started -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Require-Record($Run, [string] $Type) {
    $found = @($Run.records | Where-Object { $_.type -ceq $Type })
    if ($found.Count -ne 1) { throw "Expected one '$Type' in $($Run.log)." }
    return $found[0]
}

function New-FixturePath([string] $Label) {
    $path = Join-Path $taskRoot ('fixture-' + $Label + '-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $path) { throw 'Fixture path must be new.' }
    return $path
}

function Get-Inventory([string] $Fixture, [string] $Mode = 'metadata-inventory') {
    $run = Invoke-Probe -ProbeArgs @($Mode, $Fixture)
    $record = Require-Record $run $Mode
    foreach ($file in $record.files) {
        $relative = [string]$file.path
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..') -or $relative.Contains(':')) {
            throw "Unsafe inventory path: $relative"
        }
        $path = Join-Path $Fixture $relative
        $bytes = [IO.File]::ReadAllBytes($path)
        $complete = [long]$file.completeLength
        if ($complete -lt 0 -or $complete -gt $bytes.LongLength -or [long]$file.length -ne $bytes.LongLength) {
            throw "Invalid inventory length: $relative"
        }
        $prefix = [byte[]]::new([int]$complete)
        [Array]::Copy($bytes, $prefix, $prefix.Length)
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($prefix))
        if ($hash -cne [string]$file.sha256) { throw "Inventory prefix hash mismatch: $relative" }
    }
    return $record
}

function Assert-Prefix([string] $Fixture, $Before, [string] $Mode = 'metadata-inventory') {
    $after = Get-Inventory $Fixture $Mode
    foreach ($file in $Before.files) {
        # Original-route catalog.snapshot, active.segment and temp publication
        # files follow replace protocols. Only its RBF frames are append-only.
        if ($Mode -eq 'baseline-inventory' -and !$file.path.EndsWith('.rbf', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $observed = @($after.files | Where-Object { $_.path -ceq $file.path })
        if ($observed.Count -ne 1 -or [long]$observed[0].completeLength -lt [long]$file.completeLength) {
            throw "Complete prefix/address removed: $($file.path)"
        }
        $bytes = [IO.File]::ReadAllBytes((Join-Path $Fixture $file.path))
        $prefix = [byte[]]::new([int]$file.completeLength)
        [Array]::Copy($bytes, $prefix, $prefix.Length)
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($prefix))
        if ($hash -cne [string]$file.sha256) { throw "Complete prefix changed: $($file.path)" }
        $oldOffsets = @($file.offsets | ForEach-Object { [long]$_ })
        $newOffsets = @($observed[0].offsets | Where-Object { [long]$_ -lt [long]$file.completeLength } |
            ForEach-Object { [long]$_ })
        if (($oldOffsets -join ',') -cne ($newOffsets -join ',')) {
            throw "Complete record addresses changed: $($file.path)"
        }
    }
    return $after
}

function Get-Branch($View, [string] $Name) {
    $found = @($View.branches | Where-Object { $_.name -ceq $Name })
    if ($found.Count -gt 1) { throw "Duplicate branch $Name" }
    if ($found.Count -eq 0) { return $null }
    return $found[0]
}

function Assert-CandidateView($Run, [long] $Generation, $OldA, [bool] $HasB) {
    $record = Require-Record $Run 'metadata-inspect'
    $view = $record.view
    if ($null -eq $view -or [long]$view.generation -ne $Generation) {
        throw "Wrong selected generation in $($Run.log)"
    }
    $a = Get-Branch $view 'a'
    if ($null -eq $a -or ($a | ConvertTo-Json -Depth 20 -Compress) -cne
        ($OldA | ConvertTo-Json -Depth 20 -Compress)) { throw "A changed in $($Run.log)" }
    $b = Get-Branch $view 'b'
    if (($null -ne $b) -ne $HasB) { throw "Wrong B presence in $($Run.log)" }
    return $view
}

function Assert-BPayload($View, [string] $ByteHex) {
    $b = Get-Branch $View 'b'
    if ($null -eq $b -or [string]$b.payloadHex -cne ($ByteHex * 32) -or
        [long]$b.head.segmentNumber -ne 2) {
        throw "B payload or segment differs from the common EventJournal story."
    }
}

function Assert-FrozenOldRoot([string] $Fixture, $OldView) {
    $run = Invoke-Probe -ProbeArgs @('metadata-inspect', $Fixture, ([string]$OldView.generation))
    $view = (Require-Record $run 'metadata-inspect').view
    if (($view | ConvertTo-Json -Depth 25 -Compress) -cne ($OldView | ConvertTo-Json -Depth 25 -Compress)) {
        throw "Old root consulted newer directory or segment EOF: $($run.log)"
    }
    return $run
}

function Run-CandidateCase([string] $Point, [bool] $Published, [string] $SecondPoint = '', [bool] $Advance = $false) {
    $name = ('candidate-' + $(if ($Advance) { 'advance-' } else { 'create-' }) +
        $Point.Replace(':', '-') + $(if ($SecondPoint) { '-retry-' + $SecondPoint } else { '' }))
    if ($name -notlike $CaseFilter) { return }
    $fixture = New-FixturePath $name
    $seed = Invoke-Probe -ProbeArgs @('metadata-seed', $fixture)
    $oldView = (Require-Record (Invoke-Probe -ProbeArgs @('metadata-inspect', $fixture)) 'metadata-inspect').view
    if ($Advance) {
        $null = Invoke-Probe -ProbeArgs @('metadata-create-b', $fixture)
        $oldView = (Require-Record (Invoke-Probe -ProbeArgs @('metadata-inspect', $fixture)) 'metadata-inspect').view
    }
    $oldA = Get-Branch $oldView 'a'
    if ($null -eq $oldA -or ($null -ne (Get-Branch $oldView 'b')) -ne $Advance) {
        throw 'Bad seed view.'
    }
    $oldInventory = Get-Inventory $fixture
    $mode = if ($Advance) { 'metadata-advance-b' } else { 'metadata-create-b' }
    $crash = Invoke-Probe -ProbeArgs @($mode, $fixture, $Point) -KillPoint $Point
    $beforeRecovery = Assert-Prefix $fixture $oldInventory
    $afterCrash = Invoke-Probe -ProbeArgs @('metadata-inspect', $fixture)
    $expectedGeneration = [long]$oldView.generation + [long][int]$Published
    $selected = Assert-CandidateView $afterCrash $expectedGeneration $oldA ($Advance -or $Published)
    if ($Advance -or $Published) {
        Assert-BPayload $selected $(if ($Published -and $Advance) { '43' } else { '42' })
    }
    $beforeOldRoot = Assert-FrozenOldRoot $fixture $oldView
    $recoveryCrash = $null
    if ($SecondPoint) {
        $recoveryCrash = Invoke-Probe -ProbeArgs @('metadata-recover', $fixture, $SecondPoint) -KillPoint $SecondPoint
        $null = Assert-Prefix $fixture $beforeRecovery
    }
    $recovery = Invoke-Probe -ProbeArgs @('metadata-recover', $fixture)
    $report = Require-Record $recovery 'metadata-recovery'
    if ($report.report.status -cne 'Succeeded') { throw "Recovery did not succeed: $($recovery.log)" }
    $afterRecoveryInventory = Assert-Prefix $fixture $beforeRecovery
    $delivered = Invoke-Probe -ProbeArgs @('metadata-inspect', $fixture)
    $deliveredView = Assert-CandidateView $delivered $expectedGeneration $oldA ($Advance -or $Published)
    if (($selected | ConvertTo-Json -Depth 25 -Compress) -cne
        ($deliveredView | ConvertTo-Json -Depth 25 -Compress)) { throw 'Retry changed selected published view.' }
    $continuationMode = if ($Advance -or $Published) { 'metadata-advance-b' } else { 'metadata-create-b' }
    $continuation = Invoke-Probe -ProbeArgs @($continuationMode, $fixture)
    $continuedView = (Require-Record $continuation $continuationMode).view
    if ([long]$continuedView.generation -ne $expectedGeneration + 1 -or
        ($null -eq (Get-Branch $continuedView 'b')) -or
        (($oldA | ConvertTo-Json -Depth 20 -Compress) -cne
            ((Get-Branch $continuedView 'a') | ConvertTo-Json -Depth 20 -Compress))) {
        throw "Continuation violated A/B/generation: $($continuation.log)"
    }
    Assert-BPayload $continuedView $(if ($continuationMode -eq 'metadata-create-b') { '42' } else { '43' })
    if ($Advance -or $Published) {
        $previousB = Get-Branch $deliveredView 'b'
        $nextB = Get-Branch $continuedView 'b'
        # Complete unselected B events consume sequence numbers; continuation
        # advances beyond their physical high-water mark rather than reusing one.
        if ([long]$nextB.sequence -le [long]$previousB.sequence -or
            [string]$nextB.refId -cne [string]$previousB.refId -or
            ($nextB.head | ConvertTo-Json -Depth 15 -Compress) -ceq
                ($previousB.head | ConvertTo-Json -Depth 15 -Compress)) {
            throw 'Advance did not move B head and sequence.'
        }
    }
    $afterContinuationInventory = Assert-Prefix $fixture $beforeRecovery
    $cold = Invoke-Probe -ProbeArgs @('metadata-inspect', $fixture)
    $coldView = (Require-Record $cold 'metadata-inspect').view
    if (($coldView | ConvertTo-Json -Depth 25 -Compress) -cne
        ($continuedView | ConvertTo-Json -Depth 25 -Compress)) { throw 'Independent cold read differs from continuation.' }
    $afterOldRoot = Assert-FrozenOldRoot $fixture $oldView
    $results.Add([pscustomobject]@{ name = $name; kind = 'candidate'; point = $Point;
        recoveryPoint = $SecondPoint; expectedPublished = $Published; fixture = $fixture;
        seed = $seed; crash = $crash; afterCrash = $afterCrash; oldRootBefore = $beforeOldRoot;
        recoveryCrash = $recoveryCrash; recovery = $recovery; delivered = $delivered;
        continuation = $continuation; cold = $cold; oldRootAfter = $afterOldRoot;
        oldInventory = $oldInventory; beforeRecovery = $beforeRecovery;
        afterRecoveryInventory = $afterRecoveryInventory; afterContinuationInventory = $afterContinuationInventory })
    Write-Host "PASS $name"
}

try {
    [Environment]::SetEnvironmentVariable('TEMP', $taskRoot, 'Process')
    [Environment]::SetEnvironmentVariable('TMP', $taskRoot, 'Process')
    if ($Suite -in @('all', 'candidate')) {
        foreach ($point in @('AfterSegmentCreated', 'AfterSegmentHeaderFlush', 'AfterEventAppend', 'AfterEventFlush',
                'AfterAllocationAppend', 'AfterAllocationFlush', 'AfterDirectoryAppend', 'AfterDirectoryFlush',
                'AfterRootAppend', 'AfterRootFlush', 'BeforeAck',
                'metadata-partial0:Segment2', 'metadata-partial1:Segment2',
                'metadata-partial2:Segment2', 'metadata-partial3:Segment2',
                'written:Segment2', 'flushed:Segment2',
                'partial:Event', 'written:Event', 'flushed:Event',
                'partial:Allocation', 'written:Allocation', 'flushed:Allocation',
                'partial:Directory', 'written:Directory', 'flushed:Directory',
                'partial:Root', 'written:Root', 'flushed:Root')) {
            $published = $point -in @('AfterRootAppend', 'AfterRootFlush', 'BeforeAck', 'written:Root', 'flushed:Root')
            Run-CandidateCase $point $published
        }
        foreach ($pair in @(@('metadata-partial2:Segment2', 'AfterHeaderWriteBeforeFlush'),
                @('partial:Directory', 'AfterSetLengthBeforeFlush'),
                @('BeforeAck', 'BeforeDelivery'))) {
            Run-CandidateCase $pair[0] ($pair[0] -eq 'BeforeAck') $pair[1]
        }
        foreach ($point in @('AfterEventAppend', 'AfterDirectoryAppend', 'AfterRootAppend', 'BeforeAck')) {
            Run-CandidateCase $point ($point -in @('AfterRootAppend', 'BeforeAck')) '' $true
        }
    }
    if ($Suite -in @('all', 'baseline')) {
        # The original route has no corresponding recovery implementation. Preserve
        # its strict rejection and business visibility, without inventing a retry.
        foreach ($point in @('Segment.NextCreate', 'Segment.NextFlush', 'Segment.LocatorCreate',
                'Segment.LocatorFlush', 'Segment.LocatorReplace', 'Segment.LocatorPublished',
                'AfterBEventDurable', 'Journal.RefOpAfterDurableFlush',
                'Journal.RefBeforeInstall', 'AfterBPublication',
                'Journal.CheckpointBeforeTempFlush', 'Journal.CheckpointBeforeReplace',
                'Journal.CheckpointAfterReplace')) {
            $checkpointFixture = $point.StartsWith('Journal.Checkpoint', [StringComparison]::Ordinal)
            $name = 'baseline-' + $point.Replace('.', '-')
            if ($name -notlike $CaseFilter) { continue }
            $fixture = New-FixturePath $name
            $seedArgs = if ($checkpointFixture) { @('baseline-seed', $fixture, '511', '0', '0') } else {
                @('baseline-seed', $fixture)
            }
            $seed = Invoke-Probe -ProbeArgs $seedArgs
            $before = Get-Inventory $fixture 'baseline-inventory'
            $crash = Invoke-Probe -ProbeArgs @('baseline-create-b', $fixture, $point) -KillPoint $point
            $after = Assert-Prefix $fixture $before 'baseline-inventory'
            $inspect = Invoke-Probe -ProbeArgs @('baseline-inspect', $fixture) -AllowFailure $true
            $record = if ($inspect.exitCode -eq 0) { Require-Record $inspect 'baseline-inspect' } else {
                Require-Record $inspect 'error'
            }
            $shouldReject = $point -in @('Segment.NextFlush', 'Segment.LocatorCreate',
                'Segment.LocatorFlush', 'Segment.LocatorReplace')
            if (($inspect.exitCode -ne 0) -ne $shouldReject) {
                throw "Unexpected original-route strict Open outcome at $point : $($inspect.log)"
            }
            $seedView = (Require-Record $seed 'baseline-seed').view
            if ($shouldReject) {
                $failure = $record.error | Out-String
                if ($failure -notmatch 'NextSegmentPresent') {
                    throw "Expected explicit NextSegmentPresent at $point : $($inspect.log)"
                }
            }
            else {
                if ((($record.view.a | ConvertTo-Json -Depth 20 -Compress) -cne
                        ($seedView.a | ConvertTo-Json -Depth 20 -Compress)) -or
                    (($null -ne $record.view.b) -ne
                        ($point -in @('Journal.RefBeforeInstall', 'AfterBPublication')))) {
                    throw "Unexpected original-route A/B business view at $point : $($inspect.log)"
                }
            }
            $results.Add([pscustomobject]@{ name = $name; kind = 'baseline'; point = $point;
                fixture = $fixture; seed = $seed; crash = $crash; before = $before; after = $after;
                inspect = $inspect; strictOpenAccepted = ($inspect.exitCode -eq 0); observed = $record;
                recoveryAvailable = $false })
            Write-Host "RECORDED $name strictOpenAccepted=$($inspect.exitCode -eq 0)"
        }
        foreach ($point in @('AfterAdvanceEventDurable', 'Journal.RefMoveAfterDurableFlush',
                'AfterAdvancePublication')) {
            $name = 'baseline-advance-' + $point.Replace('.', '-')
            if ($name -notlike $CaseFilter) { continue }
            $fixture = New-FixturePath $name
            $seed = Invoke-Probe -ProbeArgs @('baseline-seed', $fixture)
            $created = Invoke-Probe -ProbeArgs @('baseline-create-b', $fixture)
            $oldView = (Require-Record (Invoke-Probe -ProbeArgs @('baseline-inspect', $fixture)) 'baseline-inspect').view
            $before = Get-Inventory $fixture 'baseline-inventory'
            $crash = Invoke-Probe -ProbeArgs @('baseline-advance-b', $fixture, $point) -KillPoint $point
            $after = Assert-Prefix $fixture $before 'baseline-inventory'
            $inspect = Invoke-Probe -ProbeArgs @('baseline-inspect', $fixture)
            $view = (Require-Record $inspect 'baseline-inspect').view
            $published = $point -in @('Journal.RefMoveAfterDurableFlush', 'AfterAdvancePublication')
            $headChanged = ($view.b.headTicket | ConvertTo-Json -Compress) -cne
                ($oldView.b.headTicket | ConvertTo-Json -Compress)
            if (($view.a | ConvertTo-Json -Depth 20 -Compress) -cne
                ($oldView.a | ConvertTo-Json -Depth 20 -Compress) -or
                [string]$view.b.refId -cne [string]$oldView.b.refId -or
                $headChanged -ne $published) {
                throw "Unexpected original-route B head at $point : $($inspect.log)"
            }
            $results.Add([pscustomobject]@{ name = $name; kind = 'baseline-advance'; point = $point;
                expectedPublished = $published; fixture = $fixture; seed = $seed; created = $created;
                crash = $crash; before = $before; after = $after; inspect = $inspect;
                strictOpenAccepted = $true; recoveryAvailable = $false })
            Write-Host "RECORDED $name BAdvancePublished=$published"
        }
    }
    if ($Suite -in @('all', 'cost')) {
        $benchParent = Join-Path $taskRoot ('bench-' + [Guid]::NewGuid().ToString('N'))
        $bench = Invoke-Probe -ProbeArgs @('metadata-measure', $benchParent)
        $report = Require-Record $bench 'metadata-measure'
        if ($null -eq $report.report -or @($report.report.samples).Count -lt 72) {
            throw 'Expected both-route warmup plus three fresh samples for three axes at three scales.'
        }
        $report.report | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'measurements.json')
        $results.Add([pscustomobject]@{ name = 'cost'; kind = 'cost'; log = $bench.log;
            report = (Join-Path $evidenceRoot 'measurements.json') })
    }
    [pscustomobject]@{ status = 'passed'; faultModel = 'F1 external process kill for crash cases; setup failures separately labeled';
        f2Claim = $false; taskRoot = $taskRoot; executable = $ProbeExe; suite = $Suite;
        caseFilter = $CaseFilter; count = $results.Count; cases = $results.ToArray() } |
        ConvertTo-Json -Depth 80 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'evidence.json')
    Write-Host "Evidence: $(Join-Path $evidenceRoot 'evidence.json')"
}
catch {
    [pscustomobject]@{ status = 'failed'; error = $_.ToString(); taskRoot = $taskRoot;
        completed = $results.ToArray() } | ConvertTo-Json -Depth 80 |
        Set-Content -LiteralPath (Join-Path $evidenceRoot 'evidence.json')
    throw
}
finally {
    [Environment]::SetEnvironmentVariable('TEMP', $previousTemp, 'Process')
    [Environment]::SetEnvironmentVariable('TMP', $previousTmp, 'Process')
}
