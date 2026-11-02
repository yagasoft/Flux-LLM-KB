[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeSolutionTests-$([Guid]::NewGuid().ToString('N'))"
$passed = $false
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'global.json') -Destination $temporaryRoot
    $targetsPath = Join-Path $temporaryRoot 'Directory.Solution.targets'
    $projectXml = @'
<Project DefaultTargets="Build">
  <Target Name="VSTest">
    <WriteLinesToFile File="$(TraceFile)" Lines="$(MSBuildProjectName)|$(Configuration)|$(Platform)|$(TargetFramework)|$(VSTestNoBuild)|$(VSTestTestCaseFilter)|$([MSBuild]::Escape('$(VSTestLogger)'))|$(VSTestResultsDirectory)|$(SolutionName)|$(BuildingSolutionFile)" Overwrite="false" />
    <Error Condition="'$(FailProject)' == '$(MSBuildProjectName)'" Text="Intentional disposable failure $(MSBuildProjectName)" />
  </Target>
  <Target Name="Build">
    <WriteLinesToFile File="$(TraceFile)" Lines="Build:$(MSBuildProjectName)" Overwrite="false" />
  </Target>
</Project>
'@
    foreach ($name in 'Alpha', 'Beta', 'Gamma', 'Delta') {
        $projectXml | Set-Content -LiteralPath (Join-Path $temporaryRoot "$name.csproj") -Encoding utf8
    }
    @'
<Solution>
  <Project Path="Alpha.csproj" />
  <Project Path="Beta.csproj">
    <BuildType Solution="Release|*" Project="Debug" />
    <Platform Solution="Release|*" Project="x64" />
  </Project>
  <Project Path="Gamma.csproj" />
</Solution>
'@ | Set-Content -LiteralPath (Join-Path $temporaryRoot 'Probe.slnx') -Encoding utf8

    function Invoke-Probe([string]$Label, [string]$FailProject = '', [switch]$Build) {
        $trace = Join-Path $temporaryRoot "$Label.trace"
        $log = Join-Path $temporaryRoot "$Label.log"
        $common = @('Probe.slnx', '-c', 'Release', '-m:1', '--no-restore', "-p:TraceFile=$trace")
        if ($Build) {
            & dotnet build @common *> $log
        } else {
            & dotnet test @common --no-build --filter 'Category=Probe' --logger 'console;verbosity=minimal' --logger trx --framework net10.0 `
                --results-directory (Join-Path $temporaryRoot 'results') "-p:FailProject=$FailProject" *> $log
        }
        $exitCode = $LASTEXITCODE
        $lines = @(if (Test-Path -LiteralPath $trace) { Get-Content -LiteralPath $trace })
        [pscustomobject]@{ Label = $Label; ExitCode = $exitCode; Lines = $lines }
    }
    function Assert-Probe($Result, [int]$ExpectedExit, [string[]]$Projects) {
        $names = @($Result.Lines | ForEach-Object { ($_ -split '\|')[0] })
        if (($Result.ExitCode -eq 0) -ne ($ExpectedExit -eq 0) -or
            ($names -join ',') -cne ($Projects -join ',')) {
            throw "Unexpected exit/project traversal for $($Result.Label): $($Result | ConvertTo-Json -Compress)"
        }
    }

    Push-Location -LiteralPath $temporaryRoot
    try {
        # SDKless projects need no restore, packages, compiler, SQL or other services.
        $baseline = Invoke-Probe 'default-pass'
        Assert-Probe $baseline 0 @('Alpha', 'Beta', 'Gamma')
        $baselineFailure = Invoke-Probe 'default-first-failure' -FailProject 'Alpha'
        Assert-Probe $baselineFailure 1 @('Alpha', 'Beta', 'Gamma')
        $baselineBuild = Invoke-Probe 'default-build' -Build
        Assert-Probe $baselineBuild 0 @('Build:Alpha', 'Build:Beta', 'Build:Gamma')
        Copy-Item -LiteralPath (Join-Path $SourceRoot 'Directory.Solution.targets') -Destination $targetsPath
        $custom = Invoke-Probe 'custom-pass'
        Assert-Probe $custom 0 @('Alpha', 'Beta', 'Gamma')
        if (($baseline.Lines -join "`n") -cne ($custom.Lines -join "`n")) {
            throw 'The solution override changed forwarded project properties.'
        }
        $expectedResults = Join-Path $temporaryRoot 'results'
        $expected = @(
            "Alpha|Release|AnyCPU|net10.0|true|Category=Probe|console;verbosity=minimal;trx|$expectedResults|Probe|true",
            "Beta|Debug|x64|net10.0|true|Category=Probe|console;verbosity=minimal;trx|$expectedResults|Probe|true",
            "Gamma|Release|AnyCPU|net10.0|true|Category=Probe|console;verbosity=minimal;trx|$expectedResults|Probe|true"
        )
        if (($custom.Lines -join "`n") -cne ($expected -join "`n")) {
            throw 'Configuration mapping or CLI/solution properties were lost.'
        }
        Assert-Probe (Invoke-Probe 'custom-first-failure' -FailProject 'Alpha') 1 @('Alpha')
        Assert-Probe (Invoke-Probe 'custom-middle-failure' -FailProject 'Beta') 1 @('Alpha', 'Beta')
        Assert-Probe (Invoke-Probe 'custom-build' -Build) 0 @('Build:Alpha', 'Build:Beta', 'Build:Gamma')

        # Discovery must include newly added solution projects without a test manifest.
        $solutionPath = Join-Path $temporaryRoot 'Probe.slnx'
        (Get-Content -LiteralPath $solutionPath -Raw).Replace('</Solution>', '  <Project Path="Delta.csproj" /></Solution>') |
            Set-Content -LiteralPath $solutionPath -Encoding utf8
        Remove-Item -LiteralPath $targetsPath
        $expandedBaseline = Invoke-Probe 'default-new-project'
        Assert-Probe $expandedBaseline 0 @('Alpha', 'Beta', 'Delta', 'Gamma')
        Copy-Item -LiteralPath (Join-Path $SourceRoot 'Directory.Solution.targets') -Destination $targetsPath
        $expandedCustom = Invoke-Probe 'custom-new-project'
        if ($expandedCustom.ExitCode -ne 0 -or
            ($expandedCustom.Lines -join "`n") -cne ($expandedBaseline.Lines -join "`n")) {
            throw 'New solution project discovery/order differs from the SDK default.'
        }
        $invalidTrace = Join-Path $temporaryRoot 'invalid.trace'
        & dotnet test Probe.slnx -c Invalid --no-build --no-restore "-p:TraceFile=$invalidTrace" *> (Join-Path $temporaryRoot 'invalid.log')
        if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $invalidTrace)) {
            throw 'Initial solution configuration validation was bypassed.'
        }
    } finally { Pop-Location }
    $passed = $true
    Write-Output 'Solution test fail-fast contract passed: discovery, configuration, forwarding, failure and build isolation.'
} finally {
    if ($passed -and (Test-Path -LiteralPath $temporaryRoot)) {
        $resolved = (Resolve-Path -LiteralPath $temporaryRoot).Path
        if ((Split-Path -Leaf $resolved) -notlike 'FluxKnowledgeSolutionTests-*' -or
            -not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unsafe solution fixture cleanup path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } elseif (Test-Path -LiteralPath $temporaryRoot) {
        Write-Warning "Failed disposable solution evidence preserved at $temporaryRoot"
    }
}
