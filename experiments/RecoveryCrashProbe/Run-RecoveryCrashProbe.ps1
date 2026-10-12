param(
    [Parameter(Mandatory)][string] $HostDll,
    [ValidateSet('all', 'commit', 'recovery')][string] $Suite = 'all',
    [string] $CaseFilter = '*'
)
$ErrorActionPreference = 'Stop'
$HostDll = (Resolve-Path -LiteralPath $HostDll).Path
$taskRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('durable-s0c-kill-' + [Guid]::NewGuid().ToString('N'))
if ([System.IO.Path]::GetPathRoot($taskRoot) -ne 'C:\') { throw 'Set TEMP and TMP to task-specific C: directories before invoking.' }
[System.IO.Directory]::CreateDirectory($taskRoot) | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

function Child([string] $Mode, [string] $Root, [string] $Point = '', [string] $Kind = '', [bool] $Kill = $false) {
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($HostDll, $Mode, $Root, $Point, $Kind)) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    $lines = [System.Collections.Generic.List[string]]::new()
    $records = [System.Collections.Generic.List[object]]::new()
    $killed = $false
    try {
        if (!$process.Start()) { throw 'Child process did not start.' }
        $errors = $process.StandardError.ReadToEndAsync()
        while ($true) {
            $read = $process.StandardOutput.ReadLineAsync()
            if (!$read.Wait(60000)) { throw "Child timeout: $Mode $Point" }
            $line = $read.Result
            if ($null -eq $line) { break }
            $lines.Add($line)
            $record = $line | ConvertFrom-Json
            $records.Add($record)
            if ($record.type -eq 'READY') {
                if (!$Kill -or $record.pid -ne $process.Id -or $record.point -ne $Point) { throw 'READY handshake mismatch.' }
                if ($Point.StartsWith('partial:') -and ($record.detail.WrittenBytes -ne 2 -or $record.detail.RequestedBytes -le 2)) { throw 'Partial write not demonstrated.' }
                $process.Kill($true)
                $killed = $true
                break
            }
        }
        if (!$process.WaitForExit(60000)) { throw 'Child exit timeout.' }
        $stderr = $errors.GetAwaiter().GetResult()
        $log = Join-Path $taskRoot ('child-' + [Guid]::NewGuid().ToString('N') + '.jsonl')
        [System.IO.File]::WriteAllLines($log, $lines)
        if ($stderr) { [System.IO.File]::WriteAllText($log + '.stderr', $stderr) }
        if ($Kill -and !$killed) { throw "Checkpoint not reached: $Mode $Point; $($lines -join ' ')" }
        if (!$Kill -and $process.ExitCode -ne 0) { throw "Child failed: $Mode; $($lines -join ' ') $stderr" }
        return [pscustomobject]@{ records = $records.ToArray(); pid = $process.Id; killed = $killed; log = $log }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Inventory([string] $Root) {
    $output = Child 'inventory' $Root
    $inventory = @($output.records | Where-Object type -eq 'inventory')[0]
    if ($inventory.status -ne 'Qualified') { throw "Crash image not qualified: $($inventory.status)" }
    return $inventory
}

function Preserve([string] $Root, $Before) {
    foreach ($file in $Before.files) {
        $path = Join-Path $Root $file.path
        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ($bytes.LongLength -lt $file.completeLength) { throw "Complete prefix removed: $path" }
        $prefix = [byte[]]::new([int]$file.completeLength)
        [Array]::Copy($bytes, $prefix, $prefix.Length)
        $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($prefix))
        if ($hash -ne $file.sha256) { throw "Complete prefix changed: $path" }
    }
}

function VerifyRecovered($Graph, [string] $Kind) {
    $value = [int]$Graph.selected.value
    if ($value -notin @(11, 20, 21)) { throw "Unexpected recovered graph: $value" }
    if ($Graph.selected.type -ne $(if ($value -eq 11) { 'OldNode' } else { 'NewNode' })) { throw 'Recovered domain type mixed.' }
    if ($value -eq 11 -and ($Graph.kind -ne 'Event' -or $Graph.previous.value -ne 10)) { throw 'Acknowledged old graph mixed.' }
    if ($value -eq 20 -and ($Kind -ne 'state' -or $Graph.kind -ne 'State' -or $Graph.previous.value -ne 11)) { throw 'New State graph mixed.' }
    if ($value -eq 21 -and ($Kind -ne 'event' -or $Graph.kind -ne 'Event' -or $Graph.previous.value -ne 10)) { throw 'New Event graph mixed.' }
    if (@($Graph.events | Where-Object { $_.graph.value -eq 11 }).Count -ne 1) { throw 'Acknowledged initial Event lost.' }
    $old = @($Graph.events | Where-Object { $_.graph.value -eq 11 })[0]
    if ($old.graph.type -ne 'OldNode' -or $old.previousState.value -ne 10 -or $old.previousState.type -ne 'OldNode') { throw 'Acknowledged initial State/Event pair lost.' }
}

function RunCase([string] $Name, [string] $CommitPoint, [string] $Kind, [string] $RecoveryPoint = '') {
    if ($Name -notlike $CaseFilter) { return }
    $root = Join-Path $taskRoot ('fixture-' + [Guid]::NewGuid().ToString('N'))
    $seed = Child 'seed' $root
    $acknowledged = Inventory $root
    $crash = Child 'commit' $root $CommitPoint $Kind $true
    $completeFacts = Inventory $root
    Preserve $root $acknowledged
    $secondCrash = $null
    if ($RecoveryPoint) {
        $secondCrash = Child 'recover' $root $RecoveryPoint '' $true
        Preserve $root $completeFacts
    }
    $continued = Child 'continue' $root
    $recovered = @($continued.records | Where-Object type -eq 'recovered')[0].graph
    VerifyRecovered $recovered $Kind
    if ($CommitPoint -in @('written:ref', 'flushed:ref', 'commit:AfterPublication', 'commit:BeforeInstall', 'SuccessReturnBeforeAck') -and
        $recovered.selected.value -ne $(if ($Kind -eq 'state') { 20 } else { 21 })) { throw 'Complete published new graph lost.' }
    Preserve $root $completeFacts
    $after = Inventory $root
    $cold = Child 'inspect' $root
    $graph = @($cold.records | Where-Object type -eq 'inspect')[0].graph
    if ($graph.selected.value -ne 31 -or $graph.previous.value -ne 30 -or $graph.kind -ne 'Event') { throw 'Continuation cold graph incorrect.' }
    if (($graph.events | Where-Object { $_.graph.value -eq 11 }).Count -ne 1) { throw 'Cold open lost acknowledged Event.' }
    Preserve $root $completeFacts
    $results.Add([pscustomobject]@{ name = $Name; outcome = 'passed'; fixture = $root; commitPoint = $CommitPoint;
        kind = $Kind; recoveryPoint = $RecoveryPoint; firstCrash = $crash; secondCrash = $secondCrash;
        acknowledged = $acknowledged; completeFactsBeforeRecovery = $completeFacts; afterContinuation = $after;
        recoveredGraph = $recovered; coldGraph = $graph; recoveryLog = $continued.log; coldLog = $cold.log })
    Write-Host "PASS $Name"
}

try {
    $roles = @('schema', 'state', 'event', 'ref')
    if ($Suite -in @('all', 'commit')) {
        foreach ($kind in @('state', 'event')) {
            foreach ($role in $roles) {
                foreach ($window in @('partial', 'written', 'flushed')) {
                    RunCase "commit-$kind-$window-$role" "$($window):$role" $kind
                }
            }
            foreach ($point in @('AfterPrepare', 'BeforeStateAppend', 'AfterStateDurable', 'BeforeJournalAppend', 'AfterJournalDurable', 'BeforePublication', 'AfterPublication', 'BeforeInstall')) {
                RunCase "commit-$kind-$point" "commit:$point" $kind
            }
            RunCase "commit-$kind-SuccessReturnBeforeAck" 'SuccessReturnBeforeAck' $kind
        }
    }
    if ($Suite -in @('all', 'recovery')) {
        # Discover real reconfirmation paths from the same fresh seed layout; no guessed UUID paths.
        $discoveryRoot = Join-Path $taskRoot ('fixture-discovery-' + [Guid]::NewGuid().ToString('N'))
        $null = Child 'seed' $discoveryRoot
        $discovery = Child 'recover' $discoveryRoot
        $confirmationPaths = @($discovery.records | Where-Object { $_.type -eq 'checkpoint' -and $_.point.StartsWith('BeforeConfirmFile:') } | ForEach-Object { $_.point.Substring(18) })
        $fixedPoints = @('CandidateResourcesDisposed', 'BeforeAction', 'BeforeSetLength', 'AfterSetLengthBeforeFlush', 'AfterAction', 'BeforeStrictOpen', 'AfterHistoryOpen', 'AfterGraphResourcesOpen', 'BeforeJournalConfirmation', 'AfterStrictOpen', 'BeforeDelivery')
        foreach ($role in $roles) {
            # Each fixture's ref UUID differs, so inspect its own inventory when mapping reconfirm paths.
            foreach ($point in $fixedPoints) { RunCase "recover-$role-$point" "partial:$role" 'state' $point }
            foreach ($path in $confirmationPaths) {
                if ($path.StartsWith('journal/refs/objects/')) {
                    # The host accepts a role alias for this variable path; retains exact path in JSON.
                    $path = 'ref'
                }
                foreach ($when in @('Before', 'After')) {
                    RunCase "recover-$role-$when-$($path.Replace('/', '_'))" "partial:$role" 'state' "$($when)ConfirmFile:$path"
                }
            }
        }
        foreach ($point in @('AfterStrictOpen', 'BeforeDelivery')) {
            RunCase "recover-noaction-$point" 'SuccessReturnBeforeAck' 'state' $point
        }
        foreach ($path in $confirmationPaths) {
            if ($path.StartsWith('journal/refs/objects/')) { $path = 'ref' }
            foreach ($when in @('Before', 'After')) {
                RunCase "recover-noaction-$when-$($path.Replace('/', '_'))" 'SuccessReturnBeforeAck' 'state' "$($when)ConfirmFile:$path"
            }
        }
    }
    $evidence = [pscustomobject]@{ status = 'passed'; faultModel = 'F1 external process kill'; root = $taskRoot; suite = $Suite;
        caseFilter = $CaseFilter; count = $results.Count; cases = $results.ToArray(); f2Claim = $false }
    $evidence | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $taskRoot 'evidence.json')
    Write-Host "Evidence: $(Join-Path $taskRoot 'evidence.json')"
} catch {
    [pscustomobject]@{ status = 'failed'; error = $_.ToString(); root = $taskRoot; completed = $results.ToArray() } |
        ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $taskRoot 'evidence.json')
    throw
}
