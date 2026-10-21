[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsRoot,
    [ValidateRange(1, 100)][int]$Top = 20
)

$ErrorActionPreference = 'Stop'
$ResultsRoot = (Resolve-Path -LiteralPath $ResultsRoot).Path
$projects = @()
$tests = @()
foreach ($file in Get-ChildItem -LiteralPath $ResultsRoot -Filter '*.trx' -File -Recurse) {
    [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
    $run = $trx.TestRun
    $classes = @{}
    foreach ($definition in $run.TestDefinitions.UnitTest) {
        $classes[[string]$definition.id] = [string]$definition.TestMethod.className
    }
    $results = @($run.Results.UnitTestResult)
    foreach ($result in $results) {
        $tests += [pscustomobject]@{
            name = [string]$result.testName
            class = $classes[[string]$result.testId]
            seconds = [TimeSpan]::Parse([string]$result.duration, [Globalization.CultureInfo]::InvariantCulture).TotalSeconds
            outcome = [string]$result.outcome
        }
    }
    $projects += [pscustomobject]@{
        trx = $file.FullName
        assembly = [IO.Path]::GetFileName([string]@($run.TestDefinitions.UnitTest)[0].TestMethod.codeBase)
        elapsed_seconds = ([DateTimeOffset]::Parse($run.Times.finish) - [DateTimeOffset]::Parse($run.Times.start)).TotalSeconds
        passed = [int]$run.ResultSummary.Counters.passed
        failed = [int]$run.ResultSummary.Counters.failed
        # xUnit TRX can leave this header counter at zero while listing skipped results.
        not_executed = [math]::Max([int]$run.ResultSummary.Counters.notExecuted,
            @($results | Where-Object { $_.outcome -eq 'NotExecuted' }).Count)
    }
}
if (-not $projects.Count) { throw 'No TRX results were found.' }
$fixtureRecords = @(
    foreach ($file in Get-ChildItem -LiteralPath $ResultsRoot -Filter 'sql-fixtures-*.jsonl' -File -Recurse) {
        Get-Content -LiteralPath $file.FullName | ForEach-Object { $_ | ConvertFrom-Json }
    }
)
$fixturePhases = @(
    $fixtureRecords | Group-Object phase | ForEach-Object {
        [pscustomobject]@{
            phase = $_.Name
            count = $_.Count
            summed_seconds = ($_.Group | Measure-Object seconds -Sum).Sum
            failed = @($_.Group | Where-Object { -not $_.succeeded }).Count
        }
    } | Sort-Object summed_seconds -Descending
)
$slowClasses = @(
    $tests | Group-Object class | ForEach-Object {
        [pscustomobject]@{
            class = $_.Name
            count = $_.Count
            summed_seconds = ($_.Group | Measure-Object seconds -Sum).Sum
        }
    } | Sort-Object summed_seconds -Descending | Select-Object -First $Top
)
[ordered]@{
    note = 'TRX elapsed includes host/fixture overhead. Test and fixture sums can overlap; do not subtract them from wall time.'
    projects = $projects
    slowest_tests = @($tests | Sort-Object seconds -Descending | Select-Object -First $Top)
    slowest_classes = $slowClasses
    sql_fixture_phases = $fixturePhases
    sql_catalogues = @($fixtureRecords | Select-Object -ExpandProperty catalog -Unique).Count
} | ConvertTo-Json -Depth 8
