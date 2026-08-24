Add-Type -AssemblyName System.Net.Http -ErrorAction Stop

function Get-FixedLoopbackOrigin {
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$SiteUrl
    )

    if ([string]::IsNullOrWhiteSpace($SiteUrl) -or $SiteUrl -cne $SiteUrl.Trim()) {
        throw "A fixed HTTP loopback origin is required."
    }

    try {
        $siteUri = [Uri]$SiteUrl
    }
    catch {
        throw "A fixed HTTP loopback origin is required."
    }

    if (-not $siteUri.IsAbsoluteUri -or -not $siteUri.IsLoopback -or
        $siteUri.Scheme -notin @("http", "https") -or
        -not [string]::IsNullOrEmpty($siteUri.UserInfo) -or
        $siteUri.AbsolutePath -cne "/" -or
        -not [string]::IsNullOrEmpty($siteUri.Query) -or
        -not [string]::IsNullOrEmpty($siteUri.Fragment)) {
        throw "A fixed HTTP loopback origin is required."
    }

    $originHost = $siteUri.Host.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($originHost)) {
        throw "A fixed HTTP loopback origin is required."
    }
    $authorityHost = if ($originHost.Contains(":")) { "[$originHost]" } else { $originHost }
    $origin = "{0}://{1}:{2}" -f $siteUri.Scheme.ToLowerInvariant(), $authorityHost, $siteUri.Port

    return [pscustomobject]@{
        Scheme = $siteUri.Scheme.ToLowerInvariant()
        Host = $originHost
        Port = $siteUri.Port
        Origin = $origin
    }
}

function New-FixedLoopbackProbeClient {
    [OutputType([pscustomobject])]
    param(
        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds = 30
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)

    return [pscustomobject]@{
        Handler = $handler
        Client = $client
    }
}

function Invoke-FixedLoopbackProbe {
    [OutputType([System.Net.Http.HttpResponseMessage])]
    param(
        [Parameter(Mandatory)]
        [string]$Uri,
        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds = 30
    )

    try {
        $probeUri = [Uri]$Uri
    }
    catch {
        throw "A fixed HTTP loopback probe URI is required."
    }
    if (-not $probeUri.IsAbsoluteUri -or -not $probeUri.IsLoopback -or
        $probeUri.Scheme -notin @("http", "https") -or
        -not [string]::IsNullOrEmpty($probeUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($probeUri.Fragment)) {
        throw "A fixed HTTP loopback probe URI is required."
    }
    $probeOrigin = $probeUri.GetLeftPart([UriPartial]::Authority) + "/"
    [void](Get-FixedLoopbackOrigin -SiteUrl $probeOrigin)

    $probe = New-FixedLoopbackProbeClient -TimeoutSeconds $TimeoutSeconds

    try {
        $response = $probe.Client.GetAsync($probeUri).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 200) {
            $statusCode = [int]$response.StatusCode
            $response.Dispose()
            throw "The fixed-loopback endpoint returned HTTP $statusCode; exact HTTP 200 is required."
        }
        return $response
    }
    finally {
        $probe.Client.Dispose()
        $probe.Handler.Dispose()
    }
}

function Read-FixedLoopbackResponseLine {
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream]$Stream,
        [ValidateRange(1, 16384)]
        [int]$MaximumLength
    )

    $buffer = New-Object byte[] $MaximumLength
    $length = 0
    while ($true) {
        $nextByte = $Stream.ReadByte()
        if ($nextByte -eq -1) {
            throw "The explicit loopback probe returned an unterminated response line."
        }
        if ($nextByte -eq 13) {
            $lineEndingByte = $Stream.ReadByte()
            if ($lineEndingByte -eq -1) {
                throw "The explicit loopback probe returned an unterminated response line."
            }
            if ($lineEndingByte -ne 10) {
                throw "The explicit loopback probe returned an invalid response line ending."
            }
            break
        }
        if ($nextByte -eq 10) {
            throw "The explicit loopback probe returned an invalid response line ending."
        }
        if ($length -ge $MaximumLength) {
            throw "The explicit loopback probe response line exceeds the maximum line length."
        }
        $buffer[$length] = [byte]$nextByte
        $length++
    }
    return [System.Text.Encoding]::ASCII.GetString($buffer, 0, $length)
}

function Invoke-FixedLoopbackHeaderProbe {
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$Uri,
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Headers,
        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds = 30
    )

    try {
        $probeUri = [Uri]$Uri
    }
    catch {
        throw "A fixed HTTP loopback probe URI is required."
    }
    if (-not $probeUri.IsAbsoluteUri -or -not $probeUri.IsLoopback -or
        $probeUri.Scheme -notin @("http", "https") -or
        -not [string]::IsNullOrEmpty($probeUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($probeUri.Fragment) -or
        $probeUri.PathAndQuery -match '[\r\n]' -or $probeUri.PathAndQuery.Length -gt 4096) {
        throw "A fixed HTTP loopback probe URI is required."
    }
    $probeOrigin = $probeUri.GetLeftPart([UriPartial]::Authority) + "/"
    [void](Get-FixedLoopbackOrigin -SiteUrl $probeOrigin)

    if ($Headers.Count -lt 1 -or $Headers.Count -gt 32) {
        throw "The explicit loopback probe requires between one and 32 safe headers."
    }

    $headerLines = [System.Collections.Generic.List[string]]::new()
    $headerBytes = 0
    foreach ($entry in $Headers.GetEnumerator()) {
        $headerName = [string]$entry.Key
        $headerValue = [string]$entry.Value
        if ([string]::IsNullOrWhiteSpace($headerName) -or
            $headerName -notmatch '^[!#$%&''*+\-.^_`|~0-9A-Za-z]+$') {
            throw "The explicit loopback probe rejected an unsafe header name."
        }
        if ($headerValue -match '[\r\n]' -or $headerValue.Length -gt 4096) {
            throw "The explicit loopback probe rejected an unsafe header value."
        }
        if ($headerName -in @("Host", "Content-Length", "Transfer-Encoding")) {
            throw "The explicit loopback probe rejected a reserved header name."
        }
        $headerLine = "{0}: {1}" -f $headerName, $headerValue
        $headerBytes += [System.Text.Encoding]::ASCII.GetByteCount($headerLine) + 2
        if ($headerBytes -gt 16384) {
            throw "The explicit loopback probe headers exceed the bounded request limit."
        }
        $headerLines.Add($headerLine)
    }

    $timeoutMilliseconds = $TimeoutSeconds * 1000
    $tcpClient = [System.Net.Sockets.TcpClient]::new()
    $transport = $null
    $writer = $null
    try {
        $connectTask = $tcpClient.ConnectAsync($probeUri.Host, $probeUri.Port)
        if (-not $connectTask.Wait($timeoutMilliseconds)) {
            throw "The explicit loopback probe timed out while connecting."
        }
        $networkStream = $tcpClient.GetStream()
        $networkStream.ReadTimeout = $timeoutMilliseconds
        $networkStream.WriteTimeout = $timeoutMilliseconds
        if ($probeUri.Scheme -eq "https") {
            $transport = [System.Net.Security.SslStream]::new($networkStream, $false)
            $transport.ReadTimeout = $timeoutMilliseconds
            $transport.WriteTimeout = $timeoutMilliseconds
            $transport.AuthenticateAsClient($probeUri.Host)
        }
        else {
            $transport = $networkStream
        }

        $authorityHost = if ($probeUri.Host.Contains(":")) { "[$($probeUri.Host)]" } else { $probeUri.Host }
        $requestTarget = if ([string]::IsNullOrEmpty($probeUri.PathAndQuery)) { "/" } else { $probeUri.PathAndQuery }
        $writer = [System.IO.StreamWriter]::new($transport, [System.Text.Encoding]::ASCII, 1024, $true)
        $writer.NewLine = "`r`n"
        $writer.WriteLine("GET $requestTarget HTTP/1.1")
        $writer.WriteLine("Host: ${authorityHost}:$($probeUri.Port)")
        $writer.WriteLine("Connection: close")
        foreach ($headerLine in $headerLines) {
            $writer.WriteLine($headerLine)
        }
        $writer.WriteLine()
        $writer.Flush()

        $statusLine = Read-FixedLoopbackResponseLine -Stream $transport -MaximumLength 4096
        if ([string]::IsNullOrWhiteSpace($statusLine) -or
            $statusLine -notmatch '^HTTP/\d\.\d\s+(\d{3})(?:\s|$)') {
            throw "The explicit loopback probe returned an invalid HTTP status line."
        }
        $statusCode = [int]$Matches[1]
        $responseHeaderBytes = 0
        $responseHeaderCount = 0
        while ($true) {
            $responseHeader = Read-FixedLoopbackResponseLine -Stream $transport -MaximumLength 4096
            if ($responseHeader -eq "") {
                break
            }
            $responseHeaderCount++
            $responseHeaderBytes += $responseHeader.Length + 2
            if ($responseHeaderCount -gt 100 -or $responseHeaderBytes -gt 16384) {
                throw "The explicit loopback probe response headers exceed bounded limits."
            }
        }
        if ($statusCode -ne 403) {
            throw "The explicit loopback forwarded/proxy probe returned HTTP $statusCode; exact HTTP 403 is required."
        }
        return [pscustomobject]@{ StatusCode = $statusCode }
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $transport -and $transport -is [System.Net.Security.SslStream]) { $transport.Dispose() }
        $tcpClient.Dispose()
    }
}
