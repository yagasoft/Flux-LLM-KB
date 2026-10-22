param(
    [string]$StartGate, [string]$ScriptPath, [string]$SourceRoot,
    [string]$CloseoutScript, [string]$Scenario
)
$ErrorActionPreference = 'Stop'
# No scenario code or external command runs until descendant ownership is set.
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
while (-not [IO.File]::Exists($StartGate)) {
    if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'The scenario parent did not release the start gate.' }
    [Threading.Thread]::Sleep(20)
}
& $ScriptPath -SourceRoot $SourceRoot -CloseoutScript $CloseoutScript -Scenario $Scenario -MaxParallelScenarios 1
if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }
