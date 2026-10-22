[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeTimingSummary-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times start="2026-10-21T12:00:00Z" finish="2026-10-21T12:00:10Z"/>
  <Results>
    <UnitTestResult testId="one" testName="Synthetic.Tests.Slow" duration="00:00:06" outcome="Passed"/>
    <UnitTestResult testId="two" testName="Synthetic.Tests.Fails" duration="00:00:05" outcome="Failed"/>
    <UnitTestResult testId="three" testName="Synthetic.Tests.Skipped" duration="00:00:00" outcome="NotExecuted"/>
    <UnitTestResult testId="four" testName="Synthetic.Tests.SkippedAgain" duration="00:00:00" outcome="NotExecuted"/>
  </Results>
  <TestDefinitions>
    <UnitTest id="one"><TestMethod className="Synthetic.Tests" codeBase="Synthetic.Tests.dll"/></UnitTest>
    <UnitTest id="two"><TestMethod className="Synthetic.Tests" codeBase="Synthetic.Tests.dll"/></UnitTest>
    <UnitTest id="three"><TestMethod className="Synthetic.Tests" codeBase="Synthetic.Tests.dll"/></UnitTest>
    <UnitTest id="four"><TestMethod className="Synthetic.Tests" codeBase="Synthetic.Tests.dll"/></UnitTest>
  </TestDefinitions>
  <ResultSummary><Counters total="4" executed="2" passed="1" failed="1" notExecuted="0"/></ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath (Join-Path $temporaryRoot 'synthetic.trx')
    @(
        '{"phase":"create","catalog":"synthetic","seconds":1,"succeeded":true}',
        '{"phase":"migrate","catalog":"synthetic","seconds":3,"succeeded":true,"started_at_utc":"2026-10-22T00:00:02Z","finished_at_utc":"2026-10-22T00:00:05Z"}',
        '{"phase":"migrate","catalog":"synthetic","seconds":4,"succeeded":false,"started_at_utc":"2026-10-22T00:00:00Z","finished_at_utc":"2026-10-22T00:00:04Z"}',
        '{"phase":"migrate","catalog":"synthetic","seconds":1,"succeeded":true,"started_at_utc":"2026-10-22T00:00:07Z","finished_at_utc":"2026-10-22T00:00:08Z"}'
    ) | Set-Content -LiteralPath (Join-Path $temporaryRoot 'sql-fixtures-1.jsonl')
    $summary = & (Join-Path $SourceRoot 'scripts\dev\summarise-test-timings.ps1') -ResultsRoot $temporaryRoot | ConvertFrom-Json
    if ($summary.projects.Count -ne 1 -or $summary.projects[0].elapsed_seconds -ne 10 -or
        $summary.projects[0].failed -ne 1 -or $summary.projects[0].not_executed -ne 2 -or
        $summary.slowest_tests[0].name -ne 'Synthetic.Tests.Slow' -or
        $summary.slowest_classes[0].summed_seconds -ne 11 -or
        $summary.sql_fixture_phases[0].phase -ne 'migrate' -or
        $summary.sql_fixture_phases[0].failed -ne 1 -or $summary.sql_catalogues -ne 1 -or
        $summary.sql_fixture_phases[0].summed_seconds -ne 8 -or
        $summary.sql_fixture_phases[0].interval_union_seconds -ne 6 -or
        $summary.sql_fixture_phases[0].timed_records -ne 3 -or
        $null -ne $summary.sql_fixture_phases[1].interval_union_seconds) {
        throw 'Timing summary lost outcomes, ranking, overlapping durations or fixture phases.'
    }
    Write-Output 'Test timing summary contract passed.'
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolved = (Resolve-Path -LiteralPath $temporaryRoot).Path
        if ((Split-Path -Leaf $resolved) -notlike 'FluxKnowledgeTimingSummary-*' -or
            -not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unsafe timing fixture cleanup path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
