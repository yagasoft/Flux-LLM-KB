Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'incremental-source-access-config.psm1') -ErrorAction Stop

if (!('FluxSourceJournalOperatorTransport' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'source-journal-allowlist-transport.cs') -ErrorAction Stop
}

function Assert-SourceJournalOperatorOrigin([string]$Origin, [string]$RequiredHost) {
    if ($Origin -cnotmatch ('\Ahttp://' + [regex]::Escape($RequiredHost) + ':([1-9][0-9]{0,4})\z') -or
        [int]$Matches[1] -lt 1024 -or [int]$Matches[1] -gt 65535) {
        throw 'Invalid exact journal operator loopback origin.'
    }
}

function Get-SourceJournalOperatorSid([Net.HttpListenerContext]$Context, $State) {
    if (![FluxSourceJournalOperatorTransport]::IsDirectLoopback($Context.Request, $State.Authority)) { return }
    if ($null -ne $Context.User -and $Context.User.Identity -is [Security.Principal.WindowsIdentity]) {
        $identity = $Context.User.Identity
        if ($identity.IsAuthenticated -and $null -ne $identity.User -and $State.OperatorSids.Contains($identity.User.Value)) {
            return $identity.User.Value
        }
    }
}

function Read-SourceJournalOperatorForm([Net.HttpListenerRequest]$Request, [int]$Limit, [string[]]$Names) {
    if ($Request.ContentLength64 -gt $Limit) { throw [IO.InvalidDataException]::new('request-too-large') }
    if ($Request.ContentLength64 -le 0 -or $Request.Headers['Transfer-Encoding'] -or $Request.Headers['Content-Encoding'] -or
        $Request.ContentType -notmatch '\Aapplication/x-www-form-urlencoded(?:;\s*charset=(?:utf-8|"utf-8"))?\z') {
        throw [IO.InvalidDataException]::new('invalid-form')
    }

    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
    try {
        $bytes = [byte[]]::new([int]$Request.ContentLength64)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $Request.InputStream.ReadAsync($bytes, $offset, $bytes.Length - $offset, $deadline.Token).
                WaitAsync($deadline.Token).GetAwaiter().GetResult()
            if ($read -le 0) { throw [IO.InvalidDataException]::new('incomplete-form') }
            $offset += $read
        }
        $body = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    } finally { $deadline.Dispose() }

    $fields = $body.Split('&', $Names.Length + 1)
    if ($fields.Length -ne $Names.Length) { throw [IO.InvalidDataException]::new('invalid-form') }
    $form = @{}
    foreach ($field in $fields) {
        $pair = $field.Split('=', 2)
        if ($pair.Length -ne 2 -or $pair[0] -cnotin $Names -or $form.ContainsKey($pair[0]) -or
            $pair[1] -cnotmatch '\A(?:[A-Za-z0-9+/=]|%[0-9A-Fa-f]{2})+\z') {
            throw [IO.InvalidDataException]::new('invalid-form')
        }
        $form[$pair[0]] = [Net.WebUtility]::UrlDecode($pair[1])
    }
    return $form
}

function Read-SourceJournalOperatorDocument([string]$Encoded, [string]$Sha256, [int]$Limit) {
    if ($Sha256 -cnotmatch '\A[0-9A-F]{64}\z' -or $Encoded.Length -gt (4 * [Math]::Ceiling($Limit / 3.0)) -or
        $Encoded -cnotmatch '\A[A-Za-z0-9+/]+={0,2}\z') { throw 'invalid-review-document' }
    $bytes = [Convert]::FromBase64String($Encoded)
    if ($bytes.Length -gt $Limit -or [Convert]::ToBase64String($bytes) -cne $Encoded -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne $Sha256) { throw 'invalid-review-document' }
    return ,$bytes
}

function ConvertFrom-SourceJournalOperatorReview([hashtable]$Form) {
    $proposal = ConvertFrom-UniqueConfigurationJson (Read-SourceJournalOperatorDocument $Form.proposal $Form.sha256 3072)
    if ($proposal.Count -ne 2 -or @($proposal.Keys | Where-Object { $_ -cnotin @('ExpectedApprovedVolumes','ApprovedVolumes') }).Count) {
        throw 'invalid-review-volume-fields'
    }
    foreach ($key in @('ExpectedApprovedVolumes','ApprovedVolumes')) {
        $volumes = $proposal[$key]
        if ($volumes -isnot [array] -or $volumes.Count -lt 1 -or $volumes.Count -gt 16) { throw 'invalid-review-volume-count' }
        $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($volume in $volumes) {
            if ($volume -isnot [string] -or $volume -inotmatch '\A\\\\\?\\VOLUME\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}\\\z' -or
                !$unique.Add($volume)) { throw 'invalid-review-volume' }
        }
    }
    $before = [Collections.Generic.HashSet[string]]::new([string[]]$proposal.ExpectedApprovedVolumes,[StringComparer]::OrdinalIgnoreCase)
    if ($before.SetEquals([string[]]$proposal.ApprovedVolumes)) { throw 'unchanged-review-volume-set' }

    $impactBytes = Read-SourceJournalOperatorDocument $Form.impact $Form.impactSha256 12288
    $impactJson = [Text.Json.JsonDocument]::Parse([Text.UTF8Encoding]::new($false, $true).GetString($impactBytes))
    try {
        $element = $impactJson.RootElement
        if ($element.ValueKind -ne [Text.Json.JsonValueKind]::Array -or $element.GetArrayLength() -gt 128) { throw 'invalid-review-impact' }
        $impact = @(foreach ($entry in $element.EnumerateArray()) {
            if ($entry.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'invalid-review-impact-entry' }
            $value = $entry.GetString()
            if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -gt 1024 -or $value -match '[\p{Cc}\u202A-\u202E\u2066-\u2069]') {
                throw 'invalid-review-impact-entry'
            }
            # Source IDs are part of the exact review digest. The confirmation
            # shows the source's readable label from the same protected bytes.
            if ($value -cmatch '\ASource impact SHA-256 [0-9A-F]{64}\z') { continue }
            if ($value -cmatch '\A[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12} ') { $value.Substring(37) } else { $value }
        })
    } finally { $impactJson.Dispose() }
    return @{
        ExpectedApprovedVolumes = [string[]]$proposal.ExpectedApprovedVolumes
        ApprovedVolumes = [string[]]$proposal.ApprovedVolumes
        Impact = [string[]]$impact
        Proposal = $Form.proposal
        Sha256 = $Form.sha256
        ImpactSha256 = $Form.impactSha256
    }
}

function New-SourceJournalOperatorConfirmation([hashtable]$References, [string]$Sid, [hashtable]$Preview, [DateTimeOffset]$Now) {
    foreach ($key in @($References.Keys)) {
        if ($References[$key].Expires -le $Now) { $References.Remove($key) }
    }
    $mine = @($References.Values | Where-Object Sid -CEQ $Sid | Sort-Object Expires)
    if ($mine.Count -ge 8) { $References.Remove($mine[0].Token) }
    $token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $References.Add($token, @{ Token = $token; Sid = $Sid; Preview = $Preview; Expires = $Now.AddMinutes(5) })
    return $token
}

function Use-SourceJournalOperatorConfirmation([hashtable]$References, [string]$Sid, [string]$Token, [DateTimeOffset]$Now) {
    if ($Token -cnotmatch '\A[0-9A-F]{64}\z' -or !$References.ContainsKey($Token) -or $References[$Token].Sid -cne $Sid) { return }
    $reference = $References[$Token]
    $References.Remove($Token)
    if ($reference.Expires -gt $Now) { return $reference.Preview }
}

function Test-SourceJournalOperatorCurrentPreview($State, $Preview) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $State.Binding.DashboardOrigin + '/sources/journal/verify-preview')
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(4))
    try {
        $payload = @{ proposal = $Preview.Proposal; sha256 = $Preview.Sha256; impactSha256 = $Preview.ImpactSha256 }
        $request.Content = [Net.Http.StringContent]::new((ConvertTo-Json -InputObject $payload -Compress), [Text.Encoding]::UTF8, 'application/json')
        $response = $State.Dashboard.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
        try { return $response.StatusCode -eq [Net.HttpStatusCode]::NoContent } finally { $response.Dispose() }
    } catch { return $false } finally { $request.Dispose(); $deadline.Dispose() }
}

function Write-SourceJournalOperatorPage([Net.HttpListenerResponse]$Response, [int]$Status, [string]$Title, [string]$Markup, [switch]$Refresh, [string]$ReceiptPath) {
    $Response.StatusCode = $Status
    $Response.ContentType = 'text/html; charset=utf-8'
    $Response.Headers['Cache-Control'] = 'no-store'
    $Response.Headers['Referrer-Policy'] = 'same-origin'
    $Response.Headers['X-Content-Type-Options'] = 'nosniff'
    $Response.Headers['X-Frame-Options'] = 'DENY'
    $Response.Headers['Cross-Origin-Opener-Policy'] = 'same-origin'
    $Response.Headers['Cross-Origin-Resource-Policy'] = 'same-origin'
    $Response.Headers['Content-Security-Policy'] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'; style-src 'unsafe-inline'"
    $refreshTag = if ($ReceiptPath) {
        if ($ReceiptPath -cnotmatch '\A/status/[0-9a-f-]{36}\z') { throw 'invalid-receipt-path' }
        '<meta http-equiv="refresh" content="1;url=' + [Net.WebUtility]::HtmlEncode($ReceiptPath) + '">'
    } elseif ($Refresh) { '<meta http-equiv="refresh" content="3">' } else { '' }
    $html = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">$refreshTag<title>$Title | FluxKnowledge</title>
<style>body{font:1rem/1.5 system-ui,sans-serif;margin:2rem auto;padding:0 1rem;max-width:55rem;color:#17202b;background:#fff}code{overflow-wrap:anywhere}button{padding:.6rem .9rem;font:inherit}li{overflow-wrap:anywhere}</style></head>
<body><main><h1>$Title</h1>$Markup</main></body></html>
"@
    $bytes = [Text.Encoding]::UTF8.GetBytes($html)
    $Response.ContentLength64 = $bytes.Length
    $Response.Close($bytes, $true)
}

function Write-SourceJournalOperatorReview($Response, $Preview, [string]$Token, [string]$DashboardOrigin) {
    $returnUrl = [Net.WebUtility]::HtmlEncode($DashboardOrigin + '/sources/journal')
    $current = ($Preview.ExpectedApprovedVolumes | ForEach-Object { '<li><code>' + [Net.WebUtility]::HtmlEncode($_) + '</code></li>' }) -join ''
    $next = ($Preview.ApprovedVolumes | ForEach-Object { '<li><code>' + [Net.WebUtility]::HtmlEncode($_) + '</code></li>' }) -join ''
    $impact = ($Preview.Impact | ForEach-Object { '<li>' + [Net.WebUtility]::HtmlEncode($_) + '</li>' }) -join ''
    $impactMarkup = if ($impact) { "<ul>$impact</ul>" } else { '<p>The dashboard preview lists no affected or unmapped sources.</p>' }
    $markup = @"
<p>Confirm the reviewed change. IIS and the Source Access helper will restart.</p>
<h2>Currently approved journal volumes</h2><ul>$current</ul>
<h2>Proposed journal volumes</h2><ul>$next</ul>
<h2>Source impact from the dashboard review</h2>$impactMarkup
<form action="/activate" method="post"><input type="hidden" name="token" value="$Token"><button type="submit">Apply allowlist and restart</button></form>
<p><a href="$returnUrl">Return to journal allowlist</a></p>
"@
    Write-SourceJournalOperatorPage $Response 200 'Review journal allowlist change' $markup
}

function Get-SourceJournalOperatorReceiptPath($State, [guid]$OperationId) {
    Join-Path $State.Binding.ReceiptDirectory ('http-' + $OperationId.ToString('D') + '.json')
}

function Write-SourceJournalOperatorReceipt($State, $Receipt, [switch]$Replace) {
    $path = Get-SourceJournalOperatorReceiptPath $State ([guid]$Receipt.OperationId)
    $temporary = Join-Path $State.Binding.ReceiptDirectory ('.http-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Receipt -Compress))
        $file = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $file.Write($bytes); $file.Flush($true) } finally { $file.Dispose() }
        [IO.File]::Move($temporary, $path, [bool]$Replace)
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Complete-SourceJournalOperatorReceipt($State, $Receipt, $Result, [switch]$DisplayOnly) {
    $status = if ($Result -is [Collections.IDictionary]) { $Result['Status'] } elseif ($null -ne $Result) { $Result.PSObject.Properties['Status']?.Value }
    $code = if ($Result -is [Collections.IDictionary]) { $Result['Code'] } elseif ($null -ne $Result) { $Result.PSObject.Properties['Code']?.Value }
    if ($status -cnotin @('succeeded','rejected','rolled-back','recovery-required')) {
        $status = 'recovery-required'
        $code = 'transaction-status-uncertain'
    }
    $Receipt.Status = $status
    $Receipt.Code = if ($code -is [string] -and $code -cmatch '\A[a-z0-9][a-z0-9-]{0,79}\z') { $code } else { $status }
    $Receipt.UpdatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    if (!$DisplayOnly) { Write-SourceJournalOperatorReceipt $State $Receipt -Replace }
}

function Get-InterruptedSourceJournalOperatorResult($State, $Receipt) {
    try {
        Import-Module $State.ActivationModule -ErrorAction Stop
        $result = Get-ConfiguredSourceJournalAllowlistResult -Binding $State.Binding -OperationId ([guid]$Receipt.OperationId) `
            -ExpectedApprovedVolumes $Receipt.ExpectedApprovedVolumes -ApprovedVolumes $Receipt.ApprovedVolumes
        if ($null -ne $result) { return $result }
    } catch { Write-Warning ('Journal allowlist receipt readback ended with ' + $_.Exception.GetType().Name + '.') }
    if ($Receipt.Status -ceq 'recovery-required') { return @{ Status = $Receipt.Status; Code = $Receipt.Code } }
    return @{ Status = 'recovery-required'; Code = 'operator-interrupted' }
}

function Write-SourceJournalOperatorStatus($State, $Response, [string]$Sid, [guid]$OperationId) {
    $path = Get-SourceJournalOperatorReceiptPath $State $OperationId
    $receipt = if ([IO.File]::Exists($path)) { ConvertFrom-UniqueConfigurationJson (Get-SourceAccessConfigurationBytes $path) }
    if ($null -eq $receipt -or $receipt.OperatorSid -cne $Sid -or $receipt.OperationId -cne $OperationId.ToString('D')) {
        Write-SourceJournalOperatorPage $Response 404 'Receipt unavailable' '<p>This receipt is unavailable for your Windows account.</p>'
        return
    }
    if ($receipt.Status -cin @('accepted','recovery-required') -and ($null -eq $State.Active -or $State.Active.Receipt.OperationId -cne $receipt.OperationId)) {
        Complete-SourceJournalOperatorReceipt $State $receipt (Get-InterruptedSourceJournalOperatorResult $State $receipt) -DisplayOnly
    }
    $status = [Net.WebUtility]::HtmlEncode($receipt.Status)
    $code = [Net.WebUtility]::HtmlEncode($receipt.Code)
    $message = switch -CaseSensitive ($receipt.Status) {
        'accepted' { 'The operator accepted the change. This page will refresh while activation is running.' }
        'succeeded' { 'The approved journal allowlist is active.' }
        'rejected' { 'The transaction rejected this change. Reopen the dashboard to review the current state.' }
        'rolled-back' { 'The transaction restored and verified the previous allowlist.' }
        default { 'The transaction needs administrator review. Keep this receipt and its operation ID.' }
    }
    $markup = "<p>$message</p><dl><dt>Operation ID</dt><dd><code>$OperationId</code></dd><dt>Status</dt><dd>$status</dd><dt>Result code</dt><dd><code>$code</code></dd></dl>"
    if ($receipt.Status -cne 'accepted') {
        $returnUrl = [Net.WebUtility]::HtmlEncode($State.Binding.DashboardOrigin + '/sources/journal')
        $markup += '<p><a href="' + $returnUrl + '">Return to journal allowlist</a></p>'
    }
    Write-SourceJournalOperatorPage $Response 200 'Journal allowlist receipt' $markup -Refresh:($receipt.Status -ceq 'accepted')
}

function Start-SourceJournalOperatorActivation($State, $Receipt, $Preview) {
    $worker = [PowerShell]::Create()
    try {
        [void]$worker.AddScript({
            param($ActivationModule, $Binding, $OperationId, $Expected, $Approved)
            $ErrorActionPreference = 'Stop'
            Import-Module $ActivationModule -Force
            Invoke-ConfiguredSourceJournalAllowlist -Binding $Binding -OperationId $OperationId -ExpectedApprovedVolumes $Expected -ApprovedVolumes $Approved
        }).AddParameters(@{
            ActivationModule = $State.ActivationModule; Binding = $State.Binding; OperationId = [guid]$Receipt.OperationId
            Expected = $Preview.ExpectedApprovedVolumes; Approved = $Preview.ApprovedVolumes
        })
        $State.Active = @{ Worker = $worker; Task = $worker.BeginInvoke(); Receipt = $Receipt }
    } catch {
        $worker.Dispose()
        Complete-SourceJournalOperatorReceipt $State $Receipt @{ Status = 'rejected'; Code = 'transaction-worker-unavailable' }
    }
}

function Receive-SourceJournalOperatorActivation($State) {
    $active = $State.Active
    if ($null -eq $active -or !$active.Task.IsCompleted) { return }
    try {
        $result = $null
        try {
            $output = $active.Worker.EndInvoke($active.Task)
            if (!$active.Worker.HadErrors -and $output.Count -eq 1) { $result = $output[0] }
        } catch { Write-Warning ('Journal allowlist transaction ended with ' + $_.Exception.GetType().Name + '.') }
        Complete-SourceJournalOperatorReceipt $State $active.Receipt $result
    } finally {
        $active.Worker.Dispose()
        $State.Active = $null
    }
}

function Invoke-SourceJournalOperatorRequest($State, [Net.HttpListenerContext]$Context) {
    $response = $Context.Response
    $request = $Context.Request
    $sid = Get-SourceJournalOperatorSid $Context $State
    if (!$sid) {
        Write-SourceJournalOperatorPage $response 403 'Operator access denied' '<p>Use a Windows account authorised for this local journal operator.</p>'
        return
    }
    if ($request.HttpMethod -ceq 'GET' -and $request.RawUrl -cmatch '\A/status/([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})\z') {
        Write-SourceJournalOperatorStatus $State $response $sid ([guid]$Matches[1])
        return
    }
    if ($request.HttpMethod -cne 'POST' -or $request.RawUrl -cnotin @('/review','/activate')) {
        Write-SourceJournalOperatorPage $response 404 'Page unavailable' '<p>Open the journal allowlist from the local FluxKnowledge dashboard.</p>'
        return
    }

    $review = $request.RawUrl -ceq '/review'
    $origin = if ($review) { $State.Binding.DashboardOrigin } else { $State.Binding.OperatorOrigin }
    if ($request.Headers['Origin'] -cne $origin) {
        Write-SourceJournalOperatorPage $response 403 'Request origin denied' '<p>Open a fresh confirmation from the local FluxKnowledge dashboard.</p>'
        return
    }
    try {
        if ($review) {
            $form = Read-SourceJournalOperatorForm $request 65536 @('proposal','sha256','impact','impactSha256')
            $preview = ConvertFrom-SourceJournalOperatorReview $form
        } else { $form = Read-SourceJournalOperatorForm $request 256 @('token') }
    } catch {
        $status = if ($_.Exception.Message -ceq 'request-too-large') { 413 } else { 400 }
        Write-SourceJournalOperatorPage $response $status 'Invalid review request' '<p>Reopen the allowlist preview from the local FluxKnowledge dashboard.</p>'
        return
    }
    if ($review) {
        $token = New-SourceJournalOperatorConfirmation $State.References $sid $preview ([DateTimeOffset]::UtcNow)
        Write-SourceJournalOperatorReview $response $preview $token $State.Binding.DashboardOrigin
        return
    }

    $preview = Use-SourceJournalOperatorConfirmation $State.References $sid $form.token ([DateTimeOffset]::UtcNow)
    if ($null -eq $preview) {
        Write-SourceJournalOperatorPage $response 403 'Confirmation unavailable' '<p>Open a fresh confirmation from the local FluxKnowledge dashboard.</p>'
        return
    }
    if ($null -ne $State.Active) {
        Write-SourceJournalOperatorPage $response 409 'Journal change in progress' '<p>Wait for the current operation to finish and review the current allowlist again.</p>'
        return
    }
    if (!(Test-SourceJournalOperatorCurrentPreview $State $preview)) {
        Write-SourceJournalOperatorPage $response 409 'Preview could not be confirmed' '<p>The dashboard could not verify the current sources and volumes. Reopen the preview and review the change again.</p>'
        return
    }

    $operation = [guid]::NewGuid().ToString('D')
    $receipt = @{
        Version = 1; OperationId = $operation; OperatorSid = $sid; Status = 'accepted'; Code = 'accepted'
        Sha256 = $preview.Sha256; ImpactSha256 = $preview.ImpactSha256; UpdatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        ExpectedApprovedVolumes = $preview.ExpectedApprovedVolumes; ApprovedVolumes = $preview.ApprovedVolumes
    }
    Write-SourceJournalOperatorReceipt $State $receipt
    try {
        $markup = '<p>Save this operation ID or open the receipt before checking the dashboard again.</p>' +
            "<p><code>$operation</code></p><p><a href=`"/status/$operation`">Open journal allowlist receipt</a></p>"
        Write-SourceJournalOperatorPage $response 202 'Journal allowlist change accepted' $markup -ReceiptPath "/status/$operation"
    } catch {
        Complete-SourceJournalOperatorReceipt $State $receipt @{ Status = 'rejected'; Code = 'acceptance-not-delivered' }
        throw
    }
    Start-SourceJournalOperatorActivation $State $receipt $preview
}

function Start-SourceJournalAllowlistOperatorListener {
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Binding)

    $ErrorActionPreference = 'Stop'
    Assert-SourceJournalOperatorOrigin $Binding.OperatorOrigin 'localhost'
    Assert-SourceJournalOperatorOrigin $Binding.DashboardOrigin '127.0.0.1'
    if ($Binding.Version -notin 1,2 -or ([uri]$Binding.OperatorOrigin).Port -eq ([uri]$Binding.DashboardOrigin).Port -or
        ![IO.Path]::IsPathFullyQualified($Binding.ReceiptDirectory) -or ![IO.Directory]::Exists($Binding.ReceiptDirectory) -or
        $Binding.OperatorSids -isnot [array] -or $Binding.OperatorSids.Count -lt 1 -or $Binding.OperatorSids.Count -gt 64) {
        throw 'The protected journal operator listener binding is invalid.'
    }
    $sids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sid in $Binding.OperatorSids) {
        if ($sid -isnot [string] -or [Security.Principal.SecurityIdentifier]::new($sid).Value -cne $sid -or !$sids.Add($sid)) {
            throw 'The protected journal operator SID list is invalid.'
        }
    }
    $activationModule = Join-Path $PSScriptRoot 'source-journal-allowlist-activation.psm1'
    if (!(Test-Path -LiteralPath $activationModule -PathType Leaf)) { throw 'The installed journal activation module is unavailable.' }
    $handler = [Net.Http.SocketsHttpHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseProxy = $false
    $handler.Credentials = $null
    $handler.MaxResponseHeadersLength = 8
    $dashboard = [Net.Http.HttpClient]::new($handler, $true)
    $listener = [Net.HttpListener]::new()
    $state = @{ Binding = $Binding; OperatorSids = $sids; Authority = ([uri]$Binding.OperatorOrigin).Authority
        References = @{}; Active = $null; ActivationModule = $activationModule; Dashboard = $dashboard }
    try {
        $listener.Prefixes.Add($Binding.OperatorOrigin + '/')
        [FluxSourceJournalOperatorTransport]::RequireLocalWindowsAuthentication($listener, $state.Authority, $Binding.DashboardOrigin, $Binding.OperatorOrigin)
        $listener.UnsafeConnectionNtlmAuthentication = $false
        $listener.ExtendedProtectionPolicy = [Security.Authentication.ExtendedProtection.ExtendedProtectionPolicy]::new(
            [Security.Authentication.ExtendedProtection.PolicyEnforcement]::Always,
            [Security.Authentication.ExtendedProtection.ProtectionScenario]::TransportSelected,
            [string[]]@('HTTP/localhost', ('HTTP/' + [Environment]::MachineName)))
        $listener.TimeoutManager.EntityBody = [TimeSpan]::FromSeconds(5)
        $listener.TimeoutManager.HeaderWait = [TimeSpan]::FromSeconds(10)
        $listener.Start()
        $pending = $listener.GetContextAsync()
        while ($listener.IsListening) {
            Receive-SourceJournalOperatorActivation $state
            if (!$pending.Wait(250)) { continue }
            $context = $pending.GetAwaiter().GetResult()
            try { Invoke-SourceJournalOperatorRequest $state $context }
            catch {
                Write-Warning ('Journal operator HTTP request ended with ' + $_.Exception.GetType().Name + '.')
                try { Write-SourceJournalOperatorPage $context.Response 500 'Journal operator unavailable' '<p>The operator could not complete this request. Reopen the dashboard or check the operation receipt.</p>' }
                catch { $context.Response.Abort() }
            } finally {
                if ($null -ne $context.User -and $context.User.Identity -is [IDisposable]) { $context.User.Identity.Dispose() }
            }
            $pending = $listener.GetContextAsync()
        }
    } finally {
        $listener.Close()
        $dashboard.Dispose()
        if ($null -ne $state.Active) { $state.Active.Worker.Dispose() }
    }
}

Export-ModuleMember -Function Start-SourceJournalAllowlistOperatorListener
