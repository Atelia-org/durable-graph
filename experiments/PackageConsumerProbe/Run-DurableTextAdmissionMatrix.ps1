[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OldReaderResult,
    [Parameter(Mandatory)][string] $NewReaderResult,
    [Parameter(Mandatory)][string] $NewConsumerResult,
    [Parameter(Mandatory)][string] $WorkDirectory
)
$ErrorActionPreference = 'Stop'
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'Choose a new evidence directory.' }
$inputs = @{}
foreach ($item in @(@('OldReader', $OldReaderResult), @('NewReader', $NewReaderResult), @('NewConsumer', $NewConsumerResult))) {
    $path = (Resolve-Path -LiteralPath $item[1]).Path
    $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if (!$value.Passed) { throw "Input preparation failed: $path" }
    $inputs[$item[0]] = @{ Path=$path; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; Value=$value }
}
$old = $inputs.OldReader.Value
$new = $inputs.NewReader.Value
$consumer = $inputs.NewConsumer.Value
if ($old.Version -ceq $new.Version -or $new.Version -cne $consumer.Version -or $new.SourceCommit -cne $consumer.ExpectedSourceCommit) {
    throw 'Expected distinct old/new versions and the same new reader/consumer package source.'
}
$oldControl = Join-Path (Split-Path $inputs.OldReader.Path) 'old-control'
$newText = Join-Path (Split-Path $inputs.NewConsumer.Path) 'database'
[IO.Directory]::CreateDirectory($work) | Out-Null
$result = [ordered]@{ Passed=$false; Inputs=@(); Cells=@(); Failure=$null }
foreach ($key in $inputs.Keys) { $result.Inputs += @{ Name=$key; Path=$inputs[$key].Path; Sha256=$inputs[$key].Sha256 } }
function Snapshot([string] $Path) {
    return @(Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName | ForEach-Object {
        $_.FullName + '|' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n"
}
try {
    $positiveOld = @($old.Lanes | Where-Object Name -ceq 'control-read')
    if ($positiveOld.Count -ne 1 -or $positiveOld[0].Rejected -or $positiveOld[0].Stage -cne 'Completed') { throw 'Missing old/old positive control.' }
    if (@($consumer.Lanes.Name | Sort-Object -Unique).Count -ne 3) { throw 'Missing new/new three-process acceptance.' }
    $result.Cells += @{ Reader='Old'; Data='Old control'; Accepted=$true; Stage='Completed'; Evidence=$positiveOld[0].Evidence }
    $result.Cells += @{ Reader='New'; Data='New Text'; Accepted=$true; Stage='Completed'; Evidence=$inputs.NewConsumer.Path }
    foreach ($case in @(@{ Name='old-reader-new-text'; Reader=$old; Database=$newText; MustReject=$true },
                         @{ Name='new-reader-old-control'; Reader=$new; Database=$oldControl; MustReject=$false })) {
        $before = Snapshot $case.Database
        $evidence = Join-Path $work ($case.Name + '.json')
        $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.WorkingDirectory=$work; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($arg in @($case.Reader.Assembly, 'read', $case.Database, $evidence)) { $start.ArgumentList.Add($arg) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(60000)) { throw 'Reader observation timed out.' }
            [IO.File]::WriteAllText((Join-Path $work ($case.Name+'.log')), $stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult())
            if ($process.ExitCode -ne 0) { throw 'Reader observation process failed.' }
        } finally {
            if (!$process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
            $process.Dispose()
        }
        $actual=Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json
        if ((Snapshot $case.Database) -cne $before) { throw 'Read-only admission changed database bytes.' }
        foreach ($loaded in $actual.LoadedAssemblies) {
            $expected=@($case.Reader.Packages | Where-Object { $_.Identity.StartsWith($loaded.Name+'/',[StringComparison]::Ordinal) })
            if ($expected.Count -ne 1 -or $expected[0].RuntimeSha256 -cne $loaded.Sha256 -or
                $loaded.Path -ine (Join-Path (Split-Path $case.Reader.Assembly) ($loaded.Name+'.dll')) -or
                (Get-FileHash -LiteralPath $loaded.Path -Algorithm SHA256).Hash -cne $loaded.Sha256) { throw 'Reader loaded unproven package bytes.' }
        }
        foreach ($id in @('Atelia.DurableGraph','Atelia.DurableGraph.Persistence','Atelia.DurableGraph.Serialization','Atelia.DurableGraph.Storage')) {
            if (@($actual.LoadedAssemblies | Where-Object Name -ceq $id).Count -ne 1) { throw "Reader did not actually load $id" }
        }
        if ($actual.Rejected) {
            $messages=$actual.Exceptions.Message -join ' '
            if ($messages -notmatch '(?i)(unknown|unsupported)' -or $messages -notmatch '(?i)(type|schema|ABI|format)') { throw "Not an explicit format/type rejection: $messages" }
        } elseif ($case.MustReject -or $actual.Stage -cne 'Completed' -or $actual.Observed -cne 'plain-before-text') { throw 'Unexpected admission outcome.' }
        $result.Cells += @{ Name=$case.Name; Accepted=!$actual.Rejected; Stage=$actual.Stage; Exceptions=$actual.Exceptions;
            ProcessId=$actual.ProcessId; Evidence=$evidence; Sha256=(Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash; DatabaseBytesUnchanged=$true }
    }
    $result.Passed=$true
} catch { $result.Failure=$_.Exception.ToString(); throw }
finally { $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $work 'result.json') -Encoding utf8NoBOM }
