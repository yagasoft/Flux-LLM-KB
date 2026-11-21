[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot, [string]$HeadlessChromiumPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $SourceRoot 'scripts/deploy/source-journal-allowlist-operator-http.psm1'
if (!(Test-Path -LiteralPath $modulePath)) { throw 'The source journal operator HTTP surface is absent.' }
$module = Import-Module $modulePath -Force -PassThru

function Assert-Equal($Expected, $Actual, [string]$Contract) {
    if ($Expected -cne $Actual) { throw "$Contract (expected $Expected, observed $Actual)." }
}

function Get-UnusedLoopbackPort {
    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $socket.Start(); $socket.LocalEndpoint.Port } finally { $socket.Stop() }
}

function Get-EncodedDocument([string]$Text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    @{ Base64 = [Convert]::ToBase64String($bytes); Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) }
}

$old = '\\?\VOLUME{11111111-1111-1111-1111-111111111111}\'
$new = '\\?\VOLUME{22222222-2222-2222-2222-222222222222}\'
$proposalText = ConvertTo-Json -Compress -InputObject @{ ExpectedApprovedVolumes = @($old); ApprovedVolumes = @($old, $new) }
$proposal = Get-EncodedDocument $proposalText
$impact = Get-EncodedDocument '["I:\\Samples <script>alert(1)</script>"]'
$form = @{ proposal = $proposal.Base64; sha256 = $proposal.Sha256; impact = $impact.Base64; impactSha256 = $impact.Sha256 }
$now = [DateTimeOffset]::UtcNow

& $module {
    param($Form, $Now)
    $preview = ConvertFrom-SourceJournalOperatorReview $Form
    $references = @{}
    $owner = 'S-1-5-21-1-2-3-1001'
    $other = 'S-1-5-21-1-2-3-1002'
    $reference = New-SourceJournalOperatorConfirmation $references $owner $preview $Now
    if ($null -ne (Use-SourceJournalOperatorConfirmation $references $other $reference $Now)) { throw 'Another SID used a confirmation.' }
    if ($null -eq (Use-SourceJournalOperatorConfirmation $references $owner $reference $Now)) { throw 'The owner could not use a confirmation.' }
    if ($null -ne (Use-SourceJournalOperatorConfirmation $references $owner $reference $Now)) { throw 'A used confirmation was accepted.' }
    $reference = New-SourceJournalOperatorConfirmation $references $owner $preview $Now
    if ($null -ne (Use-SourceJournalOperatorConfirmation $references $owner $reference $Now.AddMinutes(10))) { throw 'An expired confirmation was accepted.' }
} $form $now

$fixture = Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('SourceJournalOperatorHttp-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$worker = $null
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$operatorOrigin = "http://localhost:$(Get-UnusedLoopbackPort)"
do { $dashboardOrigin = "http://127.0.0.1:$(Get-UnusedLoopbackPort)" } while (([uri]$dashboardOrigin).Port -eq ([uri]$operatorOrigin).Port)

function Invoke-OperatorRequest([string]$Path, [string]$Method = 'GET', [hashtable]$Headers = @{}, $Body = $null, [switch]$Anonymous) {
    $parameters = @{ Uri = $operatorOrigin + $Path; Method = $Method; Headers = $Headers; NoProxy = $true;
        SkipHttpErrorCheck = $true; TimeoutSec = 10; MaximumRedirection = 0 }
    if (!$Anonymous) { $parameters.UseDefaultCredentials = $true; $parameters.AllowUnencryptedAuthentication = $true }
    if ($null -ne $Body) { $parameters.Body = $Body; $parameters.ContentType = 'application/x-www-form-urlencoded' }
    Invoke-WebRequest @parameters
}

function Open-Review([hashtable]$Body = $form) {
    Invoke-OperatorRequest '/review' 'POST' @{ Origin = $dashboardOrigin } $Body
}

function Start-OperatorFixture([string]$AllowedSid) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [Environment]::ProcessPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('-NoProfile','-NonInteractive','-File',(Join-Path $fixture 'fixture.ps1'),$fixture,$operatorOrigin,$dashboardOrigin,$AllowedSid)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
        do {
            if ($process.HasExited) { throw "HTTP fixture exited with code $($process.ExitCode)." }
            try {
                $response = Invoke-OperatorRequest '/status/00000000-0000-0000-0000-000000000001' -Anonymous
                if ($response.StatusCode -eq 401) { return $process }
            } catch { Start-Sleep -Milliseconds 125 }
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        throw 'The isolated HTTP fixture did not start.'
    } catch {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
        throw
    }
}

try {
    Copy-Item -LiteralPath $modulePath -Destination $fixture
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'scripts/deploy/source-journal-allowlist-transport.cs') -Destination $fixture
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Destination $fixture
    [IO.File]::WriteAllText((Join-Path $fixture 'preview.json'), (ConvertTo-Json -InputObject $form -Compress))
    Set-Content -LiteralPath (Join-Path $fixture 'source-journal-allowlist-activation.psm1') -Value @'
function Invoke-ConfiguredSourceJournalAllowlist([hashtable]$Binding, [guid]$OperationId, [string[]]$ExpectedApprovedVolumes, [string[]]$ApprovedVolumes) {
    $expected = '\\?\VOLUME{11111111-1111-1111-1111-111111111111}\'
    $added = '\\?\VOLUME{22222222-2222-2222-2222-222222222222}\'
    if ($ExpectedApprovedVolumes.Count -ne 1 -or $ExpectedApprovedVolumes[0] -cne $expected -or
        $ApprovedVolumes.Count -ne 2 -or $ApprovedVolumes[0] -cne $expected -or $ApprovedVolumes[1] -cne $added) {
        return @{ Status = 'rejected'; Code = 'wrong-volume-set' }
    }
    $path = Join-Path $Binding.ReceiptDirectory ('http-' + $OperationId.ToString('D') + '.json')
    if (!(Test-Path -LiteralPath $path)) { return @{ Status = 'rejected'; Code = 'missing-admission-receipt' } }
    [IO.File]::AppendAllText((Join-Path $Binding.ReceiptDirectory 'activations.txt'), $OperationId.ToString('D') + [Environment]::NewLine)
    $release = Join-Path $Binding.ReceiptDirectory 'finish-activation'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while (!(Test-Path -LiteralPath $release)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Fixture did not release activation.' }
        Start-Sleep -Milliseconds 25
    }
    [IO.File]::WriteAllText((Join-Path $Binding.ReceiptDirectory ('native-' + $OperationId.ToString('D') + '.txt')), 'verified')
    @{ Status = 'succeeded'; Code = 'fixture-activated' }
}
function Get-ConfiguredSourceJournalAllowlistResult([hashtable]$Binding, [guid]$OperationId, [string[]]$ExpectedApprovedVolumes, [string[]]$ApprovedVolumes) {
    $record = Join-Path $Binding.ReceiptDirectory ('native-' + $OperationId.ToString('D') + '.txt')
    if ($ExpectedApprovedVolumes.Count -eq 1 -and $ApprovedVolumes.Count -eq 2 -and (Test-Path -LiteralPath $record)) {
        if ([IO.File]::ReadAllText($record) -ceq 'reverted') {
            return @{ Status = 'rolled-back'; Code = 'fixture-verified-rollback' }
        }
        return @{ Status = 'succeeded'; Code = 'fixture-verified-native-receipt' }
    }
}
function Invoke-ConfiguredSourceRepairPolicy([hashtable]$Binding,[hashtable]$Request) {
    $path=Join-Path $Binding.ReceiptDirectory 'repair-fixture.json'
    $grant=if(Test-Path -LiteralPath $path){Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable}else{$null}
    if($Request.ContainsKey('Enabled')){
        $prior=if($grant){[guid]$grant.Generation}else{[guid]::Empty}
        if([guid]$Request.ExpectedGeneration -ne $prior){throw 'stale fixture grant'}
        $grant=@{SourceId=$Request.SourceId.ToString('D');Generation=[guid]::NewGuid().ToString('D');Enabled=$Request.Enabled
            Path='I:\Synthetic\Source';IdentityFingerprint=('a'*64)}
        [IO.File]::WriteAllText($path,(ConvertTo-Json -InputObject $grant -Compress))
    }
    return @{Available=$true;Grant=$grant;ManagedByInstallation=$false}
}
Export-ModuleMember -Function Invoke-ConfiguredSourceJournalAllowlist,Get-ConfiguredSourceJournalAllowlistResult,Invoke-ConfiguredSourceRepairPolicy
'@
    Set-Content -LiteralPath (Join-Path $fixture 'fixture.ps1') -Value @'
param($Fixture, $OperatorOrigin, $DashboardOrigin, $AllowedSid)
$ErrorActionPreference = 'Stop'
$dashboard = Start-ThreadJob -ArgumentList $DashboardOrigin,$Fixture,$OperatorOrigin -ScriptBlock {
    param($Origin, $Fixture, $OperatorOrigin)
    $expected = Get-Content -LiteralPath (Join-Path $Fixture 'preview.json') -Raw | ConvertFrom-Json -AsHashtable
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add($Origin + '/')
    $listener.Start()
    [IO.File]::WriteAllText((Join-Path $Fixture 'dashboard-ready'), 'ready')
    try {
        while ($listener.IsListening) {
            $ctx = $listener.GetContext()
            try {
                $ctx.Response.StatusCode = 403
                if ($ctx.Request.HttpMethod -ceq 'GET' -and $ctx.Request.RawUrl -ceq '/browser-review') {
                    $fields = ($expected.GetEnumerator() | ForEach-Object {
                        '<input type="hidden" name="' + [Net.WebUtility]::HtmlEncode($_.Key) +
                            '" value="' + [Net.WebUtility]::HtmlEncode($_.Value) + '">'
                    }) -join ''
                    $html = '<!doctype html><form method="post" action="' + $OperatorOrigin +
                        '/review">' + $fields + '</form><script>document.forms[0].submit()</script>'
                    $bytes = [Text.Encoding]::UTF8.GetBytes($html)
                    $ctx.Response.StatusCode = 200
                    $ctx.Response.ContentType = 'text/html; charset=utf-8'
                    $ctx.Response.ContentLength64 = $bytes.Length
                    $ctx.Response.OutputStream.Write($bytes)
                } elseif ($ctx.Request.HttpMethod -ceq 'GET' -and
                    $ctx.Request.RawUrl -ceq '/sources/2b014fd9-007d-412d-8a46-8ed710372600/repair-authority') {
                    $ctx.Response.StatusCode=404
                    if(!(Test-Path -LiteralPath (Join-Path $Fixture 'source-deleted'))){
                        $revision=if(Test-Path -LiteralPath (Join-Path $Fixture 'source-changed')){2}else{1}
                        $body=@{sourceId='2b014fd9-007d-412d-8a46-8ed710372600';path='I:\Synthetic\Source'
                            displayName='Source <fixture>';fingerprint=('a'*64);revision=$revision}
                        $data=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $body -Compress))
                        $ctx.Response.ContentType='application/json'
                        $ctx.Response.StatusCode=200
                        $ctx.Response.OutputStream.Write($data)
                    }
                } elseif ($ctx.Request.RawUrl -ceq '/return-no-content') {
                    $ctx.Response.StatusCode = 204
                } elseif ($ctx.Request.HttpMethod -ceq 'POST' -and $ctx.Request.RawUrl -ceq '/sources/journal/verify-preview' -and
                    !(Test-Path -LiteralPath (Join-Path $Fixture 'reject-preview'))) {
                    $reader = [IO.StreamReader]::new($ctx.Request.InputStream)
                    try { $preview = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
                    if ($preview.Count -eq 3 -and $preview.proposal -ceq $expected.proposal -and
                        $preview.sha256 -ceq $expected.sha256 -and $preview.impactSha256 -ceq $expected.impactSha256) {
                        $ctx.Response.StatusCode = 204
                        if (Test-Path -LiteralPath (Join-Path $Fixture 'redirect-preview')) {
                            $ctx.Response.StatusCode = 307
                            $ctx.Response.RedirectLocation = '/return-no-content'
                        }
                    }
                }
            } finally { $ctx.Response.Close() }
        }
    } finally { $listener.Close() }
}
try {
    $ready = Join-Path $Fixture 'dashboard-ready'
    while (!(Test-Path -LiteralPath $ready)) { Start-Sleep -Milliseconds 25 }
    Import-Module (Join-Path $Fixture 'source-journal-allowlist-operator-http.psm1') -Force
    Start-SourceJournalAllowlistOperatorListener -Binding @{
        Version = 1; OperatorOrigin = $OperatorOrigin; DashboardOrigin = $DashboardOrigin;
        OperatorSids = @($AllowedSid); ReceiptDirectory = $Fixture
    }
} finally {
    Stop-Job -Job $dashboard
    Remove-Job -Job $dashboard -Force
}
'@

    $worker = Start-OperatorFixture $currentSid
    $sourceId='2b014fd9-007d-412d-8a46-8ed710372600'
    $repairForm=@{sourceId=$sourceId.Replace('-','')}
    Assert-Equal 401 (Invoke-OperatorRequest '/review-source-repair' 'POST' @{Origin=$dashboardOrigin} $repairForm -Anonymous).StatusCode 'Repair review requires Windows authentication'
    Assert-Equal 403 (Invoke-OperatorRequest '/review-source-repair' 'POST' @{Origin=$operatorOrigin} $repairForm).StatusCode 'Repair review requires the exact dashboard Origin'
    $repairReview=Invoke-OperatorRequest '/review-source-repair' 'POST' @{Origin=$dashboardOrigin} $repairForm
    Assert-Equal 200 $repairReview.StatusCode 'An existing source can review file repair'
    if(!$repairReview.Content.Contains('Enable automatic repair') -or !$repairReview.Content.Contains('&lt;fixture&gt;')){
        throw 'Repair review did not present a safe exact source summary.'
    }
    $repairToken=[regex]::Match($repairReview.Content,"name='token' value='([A-F0-9]{64})'").Groups[1].Value
    $repairActivate=@{token=$repairToken}
    Assert-Equal 403 (Invoke-OperatorRequest '/activate' 'POST' @{Origin=$operatorOrigin} $repairActivate).StatusCode 'A repair token is not a journal token'
    [IO.File]::WriteAllText((Join-Path $fixture 'source-changed'), 'true')
    Assert-Equal 409 (Invoke-OperatorRequest '/activate-source-repair' 'POST' @{Origin=$operatorOrigin} $repairActivate).StatusCode 'Stale repair preview does not reach the writer'
    Remove-Item -LiteralPath (Join-Path $fixture 'source-changed')

    $repairReview=Invoke-OperatorRequest '/review-source-repair' 'POST' @{Origin=$dashboardOrigin} $repairForm
    $repairActivate.token=[regex]::Match($repairReview.Content,"name='token' value='([A-F0-9]{64})'").Groups[1].Value
    Assert-Equal 200 (Invoke-OperatorRequest '/activate-source-repair' 'POST' @{Origin=$operatorOrigin} $repairActivate).StatusCode 'Enable saves from the protected confirmation'
    Assert-Equal 403 (Invoke-OperatorRequest '/activate-source-repair' 'POST' @{Origin=$operatorOrigin} $repairActivate).StatusCode 'The repair confirmation is single-use'
    [IO.File]::WriteAllText((Join-Path $fixture 'source-deleted'), 'true')
    $staleRepair=Invoke-OperatorRequest ('/source-repair/'+$sourceId)
    if(!$staleRepair.Content.Contains('Stale source registration') -or !$staleRepair.Content.Contains('Disable automatic repair')){
        throw 'A grant on a deleted or replaced registration could not be reviewed for revocation.'
    }
    $repairActivate.token=[regex]::Match($staleRepair.Content,"name='token' value='([A-F0-9]{64})'").Groups[1].Value
    Assert-Equal 200 (Invoke-OperatorRequest '/activate-source-repair' 'POST' @{Origin=$operatorOrigin} $repairActivate).StatusCode 'A stale grant can be revoked without an active registration'
    if ($HeadlessChromiumPath) {
        $browser = Get-Item -LiteralPath $HeadlessChromiumPath
        if ($browser.Name -cne 'chrome-headless-shell.exe' -or
            $browser.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Only an already-installed headless Chromium shell may run this fixture.'
        }
        $start = [Diagnostics.ProcessStartInfo]::new($browser.FullName)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('--dump-dom','--timeout=8000','--virtual-time-budget=5000','--disable-background-networking',
                ('--user-data-dir=' + (Join-Path $fixture 'Chromium')),($dashboardOrigin + '/browser-review'))) {
            $start.ArgumentList.Add($argument)
        }
        $headless = [Diagnostics.Process]::Start($start)
        try {
            $html = $headless.StandardOutput.ReadToEndAsync()
            $stderr = $headless.StandardError.ReadToEndAsync()
            if (!$headless.WaitForExit(15000)) { throw 'Headless browser confirmation timed out.' }
            $qualified = $html.GetAwaiter().GetResult()
            $null = $stderr.GetAwaiter().GetResult()
            if ($headless.ExitCode -ne 0 -or !$qualified.Contains('Apply allowlist and restart')) {
                $title = [regex]::Match($qualified, '<title>([^<]*)</title>').Groups[1].Value
                throw "Default headless browser did not silently reach authenticated confirmation (exit=$($headless.ExitCode), title=$title, sourceForm=$($qualified.Contains('document.forms[0].submit()')))."
            }
            'Default Chromium reached authenticated review without a credential override.'
        } finally {
            if (!$headless.HasExited) { $headless.Kill($true); $headless.WaitForExit() }
            $headless.Dispose()
        }
    }
    $anonymous = Invoke-OperatorRequest '/review' 'POST' @{ Origin = $dashboardOrigin } $form -Anonymous
    Assert-Equal 401 $anonymous.StatusCode 'Anonymous requests must negotiate Windows authentication'
    $crossOrigin = Invoke-OperatorRequest '/review' 'POST' @{ Origin = 'http://127.0.0.1:1' } $form -Anonymous
    Assert-Equal 403 $crossOrigin.StatusCode 'Review requires the pinned dashboard Origin'
    $forwarded = Invoke-OperatorRequest '/review' 'POST' @{ Origin = $dashboardOrigin; 'X-Forwarded-For' = '127.0.0.1' } $form -Anonymous
    Assert-Equal 403 $forwarded.StatusCode 'Forwarded requests are rejected'
    foreach ($response in @($crossOrigin, $forwarded, (Invoke-OperatorRequest '/unregistered' -Anonymous))) {
        Assert-Equal 403 $response.StatusCode 'Invalid requests are denied before negotiation'
        if ($response.Headers.ContainsKey('WWW-Authenticate')) { throw 'An invalid request elicited a credential challenge.' }
    }
    $oversized = Invoke-OperatorRequest '/review' 'POST' @{ Origin = $dashboardOrigin } ('proposal=' + ('A' * 65536))
    Assert-Equal 413 $oversized.StatusCode 'Review rejects an oversized HTTP body'
    $forgedHash = $form.Clone(); $forgedHash.sha256 = '0' * 64
    Assert-Equal 400 (Open-Review $forgedHash).StatusCode 'Review rejects tampered proposal bytes'
    $forgedImpact = $form.Clone(); $forgedImpact.impactSha256 = '0' * 64
    Assert-Equal 400 (Open-Review $forgedImpact).StatusCode 'Review rejects tampered source impact'
    $duplicate = Get-EncodedDocument ($proposalText.TrimEnd('}') + ',"ApprovedVolumes":[]}')
    $duplicateForm = $form.Clone(); $duplicateForm.proposal = $duplicate.Base64; $duplicateForm.sha256 = $duplicate.Sha256
    Assert-Equal 400 (Open-Review $duplicateForm).StatusCode 'Review rejects duplicate JSON properties'

    $review = Open-Review
    Assert-Equal 200 $review.StatusCode 'The allowed Windows user can review the proposal'
    Assert-Equal 'same-origin' @($review.Headers['Referrer-Policy'])[0] 'The confirmation form can send its Origin'
    Assert-Equal 'no-store' @($review.Headers['Cache-Control'])[0] 'The confirmation page cannot be reused from cache'
    if (!$review.Content.Contains($old) -or !$review.Content.Contains($new) -or !$review.Content.Contains('&lt;script&gt;')) {
        throw 'The confirmation did not show the exact volume sets and safely encoded source impact.'
    }
    $token = [regex]::Match($review.Content, 'name="token" value="([A-F0-9]{64})"').Groups[1].Value
    if (!$token) { throw 'Review did not present a confirmation token.' }
    $activationHeaders = @{ Origin = $operatorOrigin }
    Assert-Equal 403 (Invoke-OperatorRequest '/activate' 'POST' @{ Origin = $dashboardOrigin } @{ token = $token }).StatusCode 'Activation requires the pinned operator Origin'
    Assert-Equal 403 (Invoke-OperatorRequest '/activate' 'POST' $activationHeaders @{ token = '0' * 64 }).StatusCode 'Activation rejects an unknown confirmation token'
    [IO.File]::WriteAllText((Join-Path $fixture 'reject-preview'), 'reject')
    Assert-Equal 409 (Invoke-OperatorRequest '/activate' 'POST' $activationHeaders @{ token = $token }).StatusCode 'Stale impact cannot reach activation'
    Remove-Item -LiteralPath (Join-Path $fixture 'reject-preview')
    $review = Open-Review
    $token = [regex]::Match($review.Content, 'name="token" value="([A-F0-9]{64})"').Groups[1].Value
    [IO.File]::WriteAllText((Join-Path $fixture 'redirect-preview'), 'redirect')
    Assert-Equal 409 (Invoke-OperatorRequest '/activate' 'POST' $activationHeaders @{ token = $token }).StatusCode 'Dashboard validation cannot follow redirects'
    Remove-Item -LiteralPath (Join-Path $fixture 'redirect-preview')
    $review = Open-Review
    $token = [regex]::Match($review.Content, 'name="token" value="([A-F0-9]{64})"').Groups[1].Value
    $accepted = Invoke-OperatorRequest '/activate' 'POST' $activationHeaders @{ token = $token }
    Assert-Equal 202 $accepted.StatusCode 'A valid review receives an accepted receipt'
    $statusPath = [regex]::Match($accepted.Content, 'href="(/status/[a-f0-9-]{36})"').Groups[1].Value
    if (!$statusPath) { throw 'Acceptance did not present a durable receipt URL.' }
    if (!$accepted.Content.Contains('http-equiv="refresh" content="1;url=' + $statusPath + '"')) {
        throw 'The browser must open the durable receipt automatically after confirmation.'
    }
    $pending = Invoke-OperatorRequest $statusPath
    Assert-Equal 200 $pending.StatusCode 'Receipt lookup remains available during activation'
    if (!$pending.Content.Contains('<dd>accepted</dd>')) { throw 'An in-flight operation lost its pending status.' }
    Assert-Equal 403 (Invoke-OperatorRequest '/activate' 'POST' $activationHeaders @{ token = $token }).StatusCode 'Activation cannot replay a used confirmation'
    $overlap = Open-Review
    $overlapToken = [regex]::Match($overlap.Content, 'name="token" value="([A-F0-9]{64})"').Groups[1].Value
    Assert-Equal 409 (Invoke-OperatorRequest '/activate' 'POST' $activationHeaders @{ token = $overlapToken }).StatusCode 'An overlapping activation cannot reach the runner'
    [IO.File]::WriteAllText((Join-Path $fixture 'finish-activation'), 'release')
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(12)
    do {
        $status = Invoke-OperatorRequest $statusPath
        if ($status.Content.Contains('succeeded')) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (!$status.Content.Contains('succeeded')) { throw 'The durable receipt did not record success.' }
    if (!$status.Content.Contains('href="' + $dashboardOrigin + '/sources/journal"')) {
        throw 'The terminal receipt did not offer a return to the dashboard.'
    }
    Assert-Equal 1 @(Get-Content -LiteralPath (Join-Path $fixture 'activations.txt')).Count 'One submission invokes the privileged runner once'
    $worker.Kill($true); $worker.WaitForExit(); $worker.Dispose(); $worker = $null
    Remove-Item -LiteralPath (Join-Path $fixture 'dashboard-ready')

    $worker = Start-OperatorFixture 'S-1-5-21-1-2-3-1002'
    Assert-Equal 403 (Open-Review).StatusCode 'An authenticated but unlisted Windows SID cannot use the operator'
    Assert-Equal 403 (Invoke-OperatorRequest $statusPath).StatusCode 'An authenticated but unlisted Windows SID cannot read receipts'
    $worker.Kill($true); $worker.WaitForExit(); $worker.Dispose(); $worker = $null
    Remove-Item -LiteralPath (Join-Path $fixture 'dashboard-ready')

    # Simulate the listener exiting before it could mirror the native terminal receipt.
    $id = $statusPath.Split('/')[-1]
    $knownPath = Join-Path $fixture ("http-$id.json")
    $known = Get-Content -LiteralPath $knownPath -Raw | ConvertFrom-Json -AsHashtable
    $known.Status = 'accepted'
    [IO.File]::WriteAllText($knownPath, (ConvertTo-Json -InputObject $known -Compress))
    $unknown = $known.Clone()
    $unknown.OperationId = [guid]::NewGuid().ToString('D')
    $unknownPath = Join-Path $fixture ('http-' + $unknown.OperationId + '.json')
    [IO.File]::WriteAllText($unknownPath, (ConvertTo-Json -InputObject $unknown -Compress))

    $worker = Start-OperatorFixture $currentSid
    if (!(Invoke-OperatorRequest $statusPath).Content.Contains('fixture-verified-native-receipt')) {
        throw 'An interrupted HTTP receipt did not recover the verified native result.'
    }
    if (!(Invoke-OperatorRequest ('/status/' + $unknown.OperationId)).Content.Contains('recovery-required')) {
        throw 'An interrupted HTTP receipt without a verified native result did not fail closed.'
    }
    $known.Status = 'recovery-required'
    $known.Code = 'review-protected-receipt'
    [IO.File]::WriteAllText($knownPath, (ConvertTo-Json -InputObject $known -Compress))
    [IO.File]::WriteAllText((Join-Path $fixture ("native-$id.txt")), 'reverted')
    if (!(Invoke-OperatorRequest $statusPath).Content.Contains('rolled-back')) {
        throw 'The operator did not reconcile a recovery-required receipt with verified rollback.'
    }
    Assert-Equal 1 @(Get-Content -LiteralPath (Join-Path $fixture 'activations.txt')).Count 'Interrupted receipt lookup cannot restart activation'
    'source-journal-allowlist-operator-http-contract passed'
} finally {
    if ($null -ne $worker) {
        if (!$worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }
        $worker.Dispose()
    }
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\SourceJournalOperatorHttp-', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe fixture cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
