param(
    [Parameter(Mandatory)][string] $HostDll,
    [Parameter(Mandatory)][string] $CliExe
)
$ErrorActionPreference = 'Stop'
$HostDll = (Resolve-Path -LiteralPath $HostDll).Path
$CliExe = (Resolve-Path -LiteralPath $CliExe).Path
$allowedTemp = 'C:/Users/gdtut/AppData/Local/Temp/db085-s0d-20260929'
if ([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -ne [IO.Path]::GetFullPath($allowedTemp).TrimEnd('\')) { throw 'Use the dedicated S0-D TEMP/TMP directory.' }
$taskRoot = Join-Path $allowedTemp ('cli-kill-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Child([string] $Mode, [string] $Root, [string] $Point = '', [string] $Kind = '', [bool] $Kill = $false, [int] $ExpectedExit = 0) {
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
        if (!$Kill -and $process.ExitCode -ne $ExpectedExit) { throw "Child failed: $Mode; $($lines -join ' ') $stderr" }
        return [pscustomobject]@{ records = $records.ToArray(); pid = $process.Id; killed = $killed; exitCode = $process.ExitCode; log = $log }
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

function InvokeRescueCli([string] $Root, [string] $Mode) {
    $start = [Diagnostics.ProcessStartInfo]::new($CliExe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('rescue', $Root, $Mode, '--json')) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (!$process.Start()) { throw 'CLI did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(60000)) { throw 'CLI timeout.' }
        $raw = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        $log = Join-Path $taskRoot ('cli-' + [Guid]::NewGuid().ToString('N') + '.stdout')
        [IO.File]::WriteAllText($log, $raw)
        [IO.File]::WriteAllText($log + '.stderr', $errors)
        if ($process.ExitCode -ne 0) { throw "CLI failed ($($process.ExitCode)): $raw $errors" }
        return [pscustomobject]@{ pid = $process.Id; executable = $CliExe; mode = $Mode; exitCode = $process.ExitCode; report = ($raw | ConvertFrom-Json); log = $log }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function ExactSnapshot([string] $Root) {
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object {
        [pscustomobject]@{ path = [IO.Path]::GetRelativePath($Root, $_.FullName); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    } | Sort-Object path)
}

function RunCliCase([string] $Role, [string] $ReportBoundary = '', [string] $Kind = 'state') {
    $root = Join-Path $taskRoot ('fixture-' + [Guid]::NewGuid().ToString('N'))
    $seed = Child 'seed' $root
    $acknowledged = Inventory $root
    $crash = Child 'commit' $root "partial:$Role" $Kind $true
    $completeFacts = Inventory $root
    Preserve $root $acknowledged
    $beforeCheck = ExactSnapshot $root
    $check = InvokeRescueCli $root '--check'
    if ($check.report.status -ne 'Qualified' -or !$check.report.resourcesClosed -or $check.report.actionAttempted) { throw 'Check report contradicts read-only qualified completion.' }
    $afterCheck = ExactSnapshot $root
    if (($beforeCheck | ConvertTo-Json -Depth 4 -Compress) -ne ($afterCheck | ConvertTo-Json -Depth 4 -Compress)) { throw 'CLI --check changed repository bytes or file set.' }
    $boundary = $null
    $apply = $null
    if ($ReportBoundary -eq 'kill') {
        $boundary = Child 'cli-report-kill' $root 'cli:BeforeReport' '' $true
        $ready = @($boundary.records | Where-Object type -eq 'READY')[0]
        if (!$ready.detail.resourcesReleased -or $ready.detail.exclusiveFilesChecked -lt 1) { throw 'Output boundary did not demonstrate released resources.' }
    } elseif ($ReportBoundary -eq 'failure') {
        $boundary = Child 'cli-report-fail' $root '' '' $false 6
        $failure = @($boundary.records | Where-Object type -eq 'output-failure')[0]
        $exit = @($boundary.records | Where-Object type -eq 'cli-exit')[0]
        if (!$failure.resourcesReleased -or $exit.exit -ne 6) { throw 'Output failure did not demonstrate released resources/non-success.' }
    } else {
        $apply = InvokeRescueCli $root '--apply'
        if ($apply.report.status -ne 'Succeeded' -or !$apply.report.resourcesClosed -or !$apply.report.actionAttempted -or !$apply.report.lengthChanged -or !$apply.report.barrierCompleted) { throw 'Apply report contradicts successful truncation and closed resources.' }
    }
    Preserve $root $completeFacts
    $afterApply = Inventory $root
    if (@($afterApply.files | Where-Object { $_.length -ne $_.completeLength }).Count -ne 0) { throw 'Successful maintenance left a short tail.' }
    foreach ($file in $completeFacts.files) {
        $observed = @($afterApply.files | Where-Object path -eq $file.path)[0]
        if ($observed.length -ne $file.completeLength) { throw 'Maintenance did not retain exactly the complete prefix.' }
    }
    # Prove first maintenance completion alone permits normal strict continuation.
    # No second maintenance entry may repair the image before this open.
    $continued = Child 'continue-strict' $root
    $recovered = @($continued.records | Where-Object type -eq 'recovered')[0].graph
    VerifyRecovered $recovered $Kind
    Preserve $root $completeFacts
    $beforeRepeat = ExactSnapshot $root
    $repeat = InvokeRescueCli $root '--apply'
    if ($repeat.report.status -ne 'Succeeded' -or !$repeat.report.resourcesClosed -or !$repeat.report.noAction -or $repeat.report.actionAttempted) { throw 'Repeat apply report contradicts no-action completion.' }
    if (($beforeRepeat | ConvertTo-Json -Depth 4 -Compress) -ne ((ExactSnapshot $root) | ConvertTo-Json -Depth 4 -Compress)) { throw 'Repeat CLI --apply changed bytes or file set.' }
    $cold = Child 'inspect' $root
    $graph = @($cold.records | Where-Object type -eq 'inspect')[0].graph
    if ($graph.selected.value -ne 31 -or $graph.previous.value -ne 30 -or $graph.kind -ne 'Event') { throw 'Continuation cold graph incorrect.' }
    if (@($graph.events | Where-Object { $_.graph.value -eq 11 }).Count -ne 1) { throw 'Cold open lost acknowledged Event.' }
    $seedGraph = @($seed.records | Where-Object type -eq 'seed')[0].graph
    $seedEvent = @($seedGraph.events | Where-Object { $_.graph.value -eq 11 })[0]
    foreach ($verifiedGraph in @($recovered, $graph)) {
        $event = @($verifiedGraph.events | Where-Object { $_.graph.value -eq 11 })[0]
        if ($event.address -ne $seedEvent.address -or $event.revision -ne $seedEvent.revision) { throw 'Acknowledged history addresses changed.' }
    }
    Preserve $root $completeFacts
    $results.Add([pscustomobject]@{ role = $Role; kind = $Kind; reportBoundary = $ReportBoundary; outcome = 'passed'; fixture = $root;
        seedLog = $seed.log; firstCrash = $crash; acknowledged = $acknowledged; completeFactsBeforeRescue = $completeFacts;
        check = $check; apply = $apply; hostedCliBoundary = $boundary; afterApply = $afterApply; repeatApply = $repeat;
        recoveredGraph = $recovered; coldGraph = $graph; continuedLog = $continued.log; coldLog = $cold.log })
    Write-Host "PASS cli-$Role-$ReportBoundary"
}

try {
    foreach ($role in @('schema', 'state', 'event', 'ref')) { RunCliCase $role }
    RunCliCase 'event' '' 'event'
    RunCliCase 'state' 'kill'
    RunCliCase 'state' 'failure'
    [pscustomobject]@{ status = 'passed'; faultModel = 'F1 external process kill'; root = $taskRoot; count = $results.Count;
        cases = $results.ToArray(); hostedOutputBoundary = 'actual MaintenanceCli.Run with failing/blocking TextWriter'; f2Claim = $false } |
        ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $taskRoot 'evidence.json')
    Write-Host "Evidence: $(Join-Path $taskRoot 'evidence.json')"
} catch {
    [pscustomobject]@{ status = 'failed'; error = $_.ToString(); root = $taskRoot; completed = $results.ToArray() } |
        ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $taskRoot 'evidence.json')
    throw
}
