param([string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/source-journal-web-bootstrap.psm1') -Force

foreach ($fault in 'none', 'install', 'ready', 'release-before', 'release-after', 'post') {
    $state = @{ Held = $false; Durable = ''; Started = $false; ReleaseCalls = 0; Ready = $false; Attempted = $false }
    $ports = @{
        WritePhase = { param($phase) $state.Durable = $phase }
        CreateHold = { $state.Held = $true }
        AssertHold = { if (!$state.Held) { throw 'hold-missing' } }
        StopWeb = { if (!$state.Held) { throw 'stop-without-hold' } }
        InstallWebAndOperator = {
            $state.Attempted = $true
            if ($fault -eq 'install') { throw 'injected-install' }
        }
        StartWeb = { $state.Started = $true }
        VerifyHeld = {
            if (!$state.Started -or !$state.Held) { throw 'unexpected-readiness-state' }
            if ($fault -eq 'ready') { throw 'injected-ready' }
            $state.Ready = $true
        }
        ReleaseHold = {
            $state.ReleaseCalls++
            if (!$state.Ready -or $state.Durable -ne 'ReleaseIntent') { throw 'unguarded-release' }
            if ($fault -eq 'release-before') { throw 'injected-release-before' }
            $state.Held = $false
            if ($fault -eq 'release-after') { throw 'injected-release-after' }
        }
        VerifyReleased = {
            if ($state.Held) { throw 'hold-remains' }
            if ($fault -eq 'post') { throw 'injected-post' }
        }
    }
    $failure = $null
    try { Invoke-SourceJournalWebBootstrap -Ports $ports }
    catch { $failure = $_.Exception.Message }
    switch ($fault) {
        'none' {
            if ($failure -or $state.Held -or $state.ReleaseCalls -ne 1 -or $state.Durable -ne 'Completed') {
                throw 'normal release did not complete'
            }
        }
        { $_ -in 'install', 'ready' } {
            if ($failure -notlike '*failed-held*' -or !$state.Held -or $state.ReleaseCalls -ne 0 -or !$state.Attempted) {
                throw "pre-release fault escaped the hold: $fault"
            }
        }
        default {
            if ($failure -notlike '*release-uncertain*' -or $state.ReleaseCalls -ne 1 -or $state.Durable -ne 'ReleaseIntent' -or
                $state.Held -ne ($fault -eq 'release-before')) { throw "release uncertainty was misclassified: $fault" }
        }
    }
}
$resumed = @{ Phase = 'InstallIntent'; Started = 0; Released = 0 }
Invoke-SourceJournalWebBootstrap -ResumeInstalled -Ports @{
    WritePhase = { param($phase) $resumed.Phase = $phase }
    AssertHold = {}
    PrepareInstalled = { if ($resumed.Phase -cne 'InstallIntent') { throw 'recovery intent changed' } }
    CreateHold = { throw 'recovery attempted to replace the hold' }
    StopWeb = { throw 'recovery attempted to stop a running owner' }
    InstallWebAndOperator = { throw 'recovery attempted reinstallation' }
    StartWeb = { $resumed.Started++ }
    VerifyHeld = { if ($resumed.Started -ne 1) { throw 'recovery started incorrectly' } }
    ReleaseHold = { if ($resumed.Phase -cne 'ReleaseIntent') { throw 'recovery release unrecorded' }; $resumed.Released++ }
    VerifyReleased = {}
}
if ($resumed.Started -ne 1 -or $resumed.Released -ne 1 -or $resumed.Phase -cne 'Completed') {
    throw 'installed-only recovery did not complete'
}

. (Join-Path $SourceRoot 'scripts/deploy/loopback-deployment-safety.ps1')
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'), [ref]$tokens, [ref]$errors)
if ($errors) { throw 'Updater must parse' }
$probeDefinition = $ast.Find({param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Invoke-RequiredLoopbackProbes'
}, $true)
. ([scriptblock]::Create($probeDefinition.Extent.Text))

$socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$socket.Start()
$origin = 'http://127.0.0.1:' + $socket.LocalEndpoint.Port
$socket.Stop()
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add($origin + '/')
$listener.Start()
$fixture = [hashtable]::Synchronized(@{ ReadyStatus = 503; Held = $false; ObservedReadyUnderHold = $false })
$server = [PowerShell]::Create()
$null = $server.AddScript({
    param($listener, $fixture)
    while ($listener.IsListening) {
        try { $request = $listener.GetContext() } catch [Net.HttpListenerException] { break }
        $response = $request.Response
        if ($request.Request.Url.AbsolutePath -ceq '/health/ready') {
            $response.StatusCode = $fixture.ReadyStatus
            if ($response.StatusCode -eq 200 -and $fixture.Held) { $fixture.ObservedReadyUnderHold = $true }
        } else { $response.StatusCode = 200 }
        $response.Close()
    }
}).AddArgument($listener).AddArgument($fixture)
$job = $server.BeginInvoke()
$lastPhase = @{ Value = '' }
$ports = @{
    WritePhase = { param($phase) $lastPhase.Value = $phase }
    CreateHold = { $fixture.Held = $true }
    AssertHold = { if (!$fixture.Held) { throw 'fixture hold missing' } }
    StopWeb = {}
    InstallWebAndOperator = {}
    StartWeb = {}
    VerifyHeld = { Invoke-RequiredLoopbackProbes -Origin $origin -TimeoutSeconds 2 }
    ReleaseHold = {
        if (!$fixture.ObservedReadyUnderHold -or $lastPhase.Value -cne 'ReleaseIntent') { throw 'release without actual held readiness' }
        $fixture.Held = $false
    }
    VerifyReleased = { Invoke-RequiredLoopbackProbes -Origin $origin -TimeoutSeconds 2 }
}
try {
    $blocked = $false
    try { Invoke-SourceJournalWebBootstrap -Ports $ports }
    catch { if ($_.Exception.Message -notlike '*failed-held*') { throw }; $blocked = $true }
    if (!$blocked -or !$fixture.Held -or $lastPhase.Value -ceq 'ReleaseIntent') { throw 'real HTTP 503 released admission' }
    $fixture.Held = $false
    $fixture.ReadyStatus = 200
    Invoke-SourceJournalWebBootstrap -Ports $ports
    if ($fixture.Held -or !$fixture.ObservedReadyUnderHold -or $lastPhase.Value -cne 'Completed') {
        throw 'real HTTP 200 was not proved under hold before completing'
    }
}
finally {
    $listener.Close()
    try { $null = $server.EndInvoke($job) } finally { $server.Dispose() }
}
'Web-only hold, real HTTP readiness and uncertain-release contracts passed.'
