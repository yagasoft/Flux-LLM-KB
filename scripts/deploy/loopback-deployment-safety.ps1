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

    Add-Type -AssemblyName System.Net.Http -ErrorAction Stop
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
