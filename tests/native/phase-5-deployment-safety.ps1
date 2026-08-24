[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$planScript = Join-Path $SourceRoot "scripts\deploy\phase-5-deployment-plan.ps1"
$validatorScript = Join-Path $SourceRoot "scripts\deploy\validate-phase-5-deployment.ps1"
$deploymentScript = Join-Path $SourceRoot "scripts\deploy\update-native-windows.ps1"
$loopbackSafetyScript = Join-Path $SourceRoot "scripts\deploy\loopback-deployment-safety.ps1"
$workerValidatorScript = Join-Path $SourceRoot "scripts\deploy\validate-native-worker-supervision.ps1"
$outlookValidatorScript = Join-Path $SourceRoot "scripts\deploy\validate-native-outlook-ingress.ps1"
$deploymentDesign = Join-Path $SourceRoot "docs\superpowers\specs\2026-08-03-native-closeout-and-loopback-deployment.md"
$deploymentPlanDocument = Join-Path $SourceRoot "docs\superpowers\plans\2026-08-03-native-closeout-and-loopback-deployment.md"

foreach ($requiredScript in @($planScript, $validatorScript, $deploymentScript, $loopbackSafetyScript, $workerValidatorScript, $outlookValidatorScript, $deploymentDesign, $deploymentPlanDocument)) {
    if (-not (Test-Path -LiteralPath $requiredScript -PathType Leaf)) {
        throw "A required Phase 5 deployment-safety script is missing: $requiredScript"
    }
}

foreach ($unsafeOrigin in @(
    "http://127.0.0.1:5137/proxied",
    "http://127.0.0.1:5137/?via=query",
    "http://127.0.0.1:5137/#fragment",
    "http://operator@127.0.0.1:5137/"
)) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $unsafeOriginOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $deploymentScript `
            -SourceRoot $SourceRoot `
            -SiteUrl $unsafeOrigin `
            -PlanOnly 2>&1 | Out-String
        $unsafeOriginExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($unsafeOriginExitCode -eq 0 -or $unsafeOriginOutput -notmatch "fixed HTTP loopback origin") {
        throw "The native deployment plan accepted a poisoned URL instead of a fixed loopback origin: $unsafeOrigin"
    }
}

$planOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $planScript -SourceRoot $SourceRoot -PlanOnly 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The Phase 5 deployment plan failed: $planOutput"
}
$plan = $planOutput | ConvertFrom-Json
$expectedMigrations = @(
    "20260813103233_AddRetainedZipProcessorBranches",
    "20260813125157_AddRetainedProcessorBranchMemberChildForeignKeys",
    "20260814144818_AddSourceProcessorForceRequests",
    "20260814161559_AddOperatorActionCapabilityFoundation",
    "20260814162746_EnforceOperatorActionCapabilityInvariants",
    "20260814170852_EnforceOperatorActionRequestPolicies",
    "20260820062157_AddRetainedCsharpCodeFacts",
    "20260820070404_HardenRetainedCsharpLifecycle",
    "20260820101021_CloseRetainedCsharpMixedOutcomes"
)
if ($plan.mode -ne "plan-only" -or -not $plan.loopback_only -or -not $plan.read_only_validation -or
    $plan.outlook_host_activation -ne $false -or
    (@($plan.phase5_migration_ids) -join "|") -ne ($expectedMigrations -join "|") -or
    $plan.phase5_migration_target -ne $expectedMigrations[-1]) {
    throw "The Phase 5 deployment plan does not expose the approved loopback, migration, read-only and Outlook-disabled contract."
}
$expectedDirectEndpoints = @(
    "/operator-actions",
    "/api/operator-actions",
    "/search/csharp-code",
    "/api/local/retained-csharp-code?query={no-match-token}"
)
if ((@($plan.direct_get_endpoints) -join "|") -ne ($expectedDirectEndpoints -join "|")) {
    throw "The Phase 5 deployment plan does not expose exactly the required read-only GET probes."
}
if (-not $plan.retained_csharp_search_requires_no_match) {
    throw "The Phase 5 deployment plan does not require an empty retained C# search result."
}
$expectedTriggerParents = [ordered]@{
    "TR_OperatorActionCapabilityPolicies_Immutable" = "OperatorActionCapabilityPolicies"
    "TR_OperatorActionHardDenials_Immutable" = "OperatorActionHardDenials"
    "TR_SourceProcessorCodeCompletionReceipts_Closure" = "SourceProcessorCodeCompletionReceipts"
    "TR_SourceProcessorCodeCompletionReceipts_OutcomeFence" = "SourceProcessorCodeCompletionReceipts"
    "TR_SourceProcessorCodeDocuments_Immutable" = "SourceProcessorCodeDocuments"
    "TR_SourceProcessorCodeDocuments_InsertFence" = "SourceProcessorCodeDocuments"
    "TR_SourceProcessorCodeBlockedDiagnostics_InsertFence" = "SourceProcessorCodeBlockedDiagnostics"
}
$actualTriggerParents = [ordered]@{}
foreach ($binding in @($plan.required_schema_trigger_bindings)) {
    $actualTriggerParents[[string]$binding.name] = [string]$binding.parent_table
    if ([string]$binding.parent_schema -cne "dbo") {
        throw "The Phase 5 deployment plan does not bind trigger $($binding.name) to dbo."
    }
}
if ($actualTriggerParents.Count -ne $expectedTriggerParents.Count) {
    throw "The Phase 5 deployment plan does not expose every exact trigger parent binding."
}
foreach ($triggerName in $expectedTriggerParents.Keys) {
    if ($actualTriggerParents[$triggerName] -cne $expectedTriggerParents[$triggerName]) {
        throw "The Phase 5 deployment plan has no exact dbo parent-table binding for $triggerName."
    }
}
$expectedForwardedHeaders = @(
    "Forwarded", "Forwarded-For", "X-Forwarded-For", "X-Original-URL", "Proxy-Connection",
    "X-ProxyUser-IP", "X-Real-IP", "Via", "True-Client-IP", "CF-Connecting-IP"
)
if ((@($plan.forwarded_proxy_headers) -join "|") -ne ($expectedForwardedHeaders -join "|")) {
    throw "The Phase 5 deployment plan does not cover every forwarded/proxy header family enforced by the loopback gate."
}
$forbiddenOperations = @("POST", "PUT", "PATCH", "DELETE", "source-original-read", "outlook", "model", "runtime-activation")
foreach ($operation in $forbiddenOperations) {
    if ($operation -notin @($plan.prohibited_operations)) {
        throw "The Phase 5 deployment plan does not prohibit $operation."
    }
}

$validatorOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $validatorScript `
    -SourceRoot $SourceRoot `
    -PlanOnly 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The Phase 5 deployment validator plan failed: $validatorOutput"
}
$validatorPlan = $validatorOutput | ConvertFrom-Json
if ($validatorPlan.mode -ne "plan-only" -or -not $validatorPlan.loopback_only -or
    -not $validatorPlan.read_only_validation -or $validatorPlan.outlook_host_activation -ne $false -or
    (@($validatorPlan.phase5_migration_ids) -join "|") -ne ($expectedMigrations -join "|") -or
    (@($validatorPlan.direct_get_endpoints) -join "|") -ne ($expectedDirectEndpoints -join "|") -or
    (@($validatorPlan.forwarded_proxy_headers) -join "|") -ne ($expectedForwardedHeaders -join "|")) {
    throw "The Phase 5 validator does not derive the authoritative read-only deployment plan."
}

function Assert-ValidatorSchemaFencingContract {
    param([string]$ValidatorText)

    foreach ($requiredFragment in @(
        "INNER JOIN sys.schemas AS [table_schema]",
        "[table_schema].[name] = N'dbo'",
        "INNER JOIN sys.tables AS [parent_table]",
        "INNER JOIN sys.schemas AS [parent_schema]",
        "[parent_schema].[name] = N'dbo'",
        "[trigger].[is_disabled] = 0",
        "@parentTable",
        "@triggerName"
    )) {
        if (-not $ValidatorText.Contains($requiredFragment)) {
            throw "The Phase 5 validator does not enforce exact dbo table/trigger metadata: $requiredFragment"
        }
    }
}

$validatorText = Get-Content -LiteralPath $validatorScript -Raw
Assert-ValidatorSchemaFencingContract -ValidatorText $validatorText
if (-not $validatorText.Contains("Invoke-FixedLoopbackProbe")) {
    throw "The Phase 5 validator does not use the shared exact-200 fixed-loopback probe."
}
$disabledTriggerMutation = $validatorText.Replace("AND [trigger].[is_disabled] = 0", "")
$disabledTriggerMutationRejected = $false
try {
    Assert-ValidatorSchemaFencingContract -ValidatorText $disabledTriggerMutation
}
catch {
    $disabledTriggerMutationRejected = $true
}
if (-not $disabledTriggerMutationRejected) {
    throw "The Phase 5 validator contract did not detect a disabled-trigger safety regression."
}

if (-not (Test-Path -LiteralPath $loopbackSafetyScript -PathType Leaf)) {
    throw "The reusable fixed-loopback origin and no-redirect probe helper is missing."
}
. $loopbackSafetyScript
$probePolicy = New-FixedLoopbackProbeClient
try {
    if ($probePolicy.Handler.UseProxy -or $probePolicy.Handler.AllowAutoRedirect) {
        throw "The shared fixed-loopback probe client permits a proxy or redirect."
    }
}
finally {
    $probePolicy.Client.Dispose()
    $probePolicy.Handler.Dispose()
}

function Invoke-SyntheticFixedLoopbackProbe {
    param(
        [Parameter(Mandatory)]
        [int]$StatusCode,
        [Parameter(Mandatory)]
        [string]$ReasonPhrase
    )

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $powerShell = [PowerShell]::Create()
    $probeOutput = $null
    try {
        $probeScript = @'
param($HelperPath, $ProbeUri)
$ErrorActionPreference = "Stop"
. $HelperPath
$response = Invoke-FixedLoopbackProbe -Uri $ProbeUri -TimeoutSeconds 5
try {
    [int]$response.StatusCode
}
finally {
    $response.Dispose()
}
'@
        [void]$powerShell.AddScript($probeScript).AddArgument($loopbackSafetyScript).AddArgument("http://127.0.0.1:$port/probe")
        $probeAsync = $powerShell.BeginInvoke()
        $accepted = $listener.AcceptTcpClientAsync()
        if (-not $accepted.Wait(5000)) {
            throw "The shared fixed-loopback probe did not connect directly to the loopback listener."
        }
        $client = $accepted.GetAwaiter().GetResult()
        try {
            $stream = $client.GetStream()
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
            while ($reader.ReadLine() -ne "") { }
            $responseText = "HTTP/1.1 $StatusCode $ReasonPhrase`r`nContent-Length: 0`r`nConnection: close`r`n"
            if ($StatusCode -ge 300 -and $StatusCode -lt 400) {
                $responseText += "Location: http://127.0.0.1:$port/redirected`r`n"
            }
            $responseText += "`r`n"
            $responseBytes = [System.Text.Encoding]::ASCII.GetBytes($responseText)
            $stream.Write($responseBytes, 0, $responseBytes.Length)
            $stream.Flush()
        }
        finally {
            $client.Dispose()
        }

        if ($StatusCode -ge 300 -and $StatusCode -lt 400) {
            $redirectedRequest = $listener.AcceptTcpClientAsync()
            if ($redirectedRequest.Wait(1000)) {
                $redirectedClient = $redirectedRequest.GetAwaiter().GetResult()
                $redirectedClient.Dispose()
                throw "The shared fixed-loopback probe followed a redirect."
            }
        }
        $endInvokeError = $null
        try {
            $probeOutput = @($powerShell.EndInvoke($probeAsync))
        }
        catch {
            $endInvokeError = $_.Exception.Message
        }
        return [pscustomobject]@{
            Output = $probeOutput
            HadErrors = $powerShell.HadErrors -or -not [string]::IsNullOrWhiteSpace($endInvokeError)
            Errors = @(@($powerShell.Streams.Error | ForEach-Object { $_.ToString() }) + @($endInvokeError))
        }
    }
    finally {
        $powerShell.Dispose()
        $listener.Stop()
    }
}

function Invoke-FreshWindowsPowerShellFixedLoopbackProbe {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $childScriptPath = Join-Path ([System.IO.Path]::GetTempPath()) ("loopback-probe-{0}.ps1" -f [Guid]::NewGuid().ToString("N"))
    $childJob = $null
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    try {
        @'
param(
    [Parameter(Mandatory)]
    [string]$HelperPath,
    [Parameter(Mandatory)]
    [string]$ProbeUri
)

$ErrorActionPreference = "Stop"
. $HelperPath
$response = Invoke-FixedLoopbackProbe -Uri $ProbeUri -TimeoutSeconds 5
try {
    [int]$response.StatusCode
}
finally {
    $response.Dispose()
}
'@ | Set-Content -LiteralPath $childScriptPath -Encoding UTF8

        $childJob = Start-Job -ScriptBlock {
            param($WindowsPowerShell, $ScriptPath, $HelperPath, $ProbeUri)

            $output = & $WindowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath `
                -HelperPath $HelperPath -ProbeUri $ProbeUri 2>&1 | Out-String
            [pscustomobject]@{
                ExitCode = $LASTEXITCODE
                Output = $output
            }
        } -ArgumentList (Get-Command powershell -CommandType Application).Source, $childScriptPath, `
            $loopbackSafetyScript, "http://127.0.0.1:$port/probe"

        $accepted = $listener.AcceptTcpClientAsync()
        if (-not $accepted.Wait(5000)) {
            $childResult = Receive-Job -Job $childJob -Wait
            throw "A fresh Windows PowerShell loopback probe did not connect to the synthetic listener: $($childResult.Output)"
        }
        $client = $accepted.GetAwaiter().GetResult()
        try {
            $stream = $client.GetStream()
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
            while ($reader.ReadLine() -ne "") { }
            $responseBytes = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
            $stream.Write($responseBytes, 0, $responseBytes.Length)
            $stream.Flush()
        }
        finally {
            $client.Dispose()
        }

        $childResult = Receive-Job -Job $childJob -Wait
        if ($childResult.ExitCode -ne 0) {
            throw "A fresh Windows PowerShell loopback probe failed: $($childResult.Output)"
        }
        if ($childResult.Output.Trim() -ne "200") {
            throw "A fresh Windows PowerShell loopback probe did not preserve the synthetic HTTP 200 response: $($childResult.Output)"
        }
    }
    finally {
        if ($null -ne $childJob) {
            Remove-Job -Job $childJob -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $childScriptPath) {
            Remove-Item -LiteralPath $childScriptPath -Force
        }
        $listener.Stop()
    }
}

$freshWindowsPowerShellProbe = Invoke-FreshWindowsPowerShellFixedLoopbackProbe
$okProbe = Invoke-SyntheticFixedLoopbackProbe -StatusCode 200 -ReasonPhrase "OK"
if ($okProbe.HadErrors -or (@($okProbe.Output) -join "").Trim() -ne "200") {
    throw "The shared fixed-loopback probe did not preserve an exact loopback 200 response."
}
$redirectProbe = Invoke-SyntheticFixedLoopbackProbe -StatusCode 302 -ReasonPhrase "Found"
if (-not $redirectProbe.HadErrors -or (@($redirectProbe.Errors) -join " ") -notmatch "HTTP 302") {
    throw "The shared fixed-loopback probe did not reject a redirect as a non-200 response."
}

$redirectListener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$redirectListener.Start()
$redirectPort = ([System.Net.IPEndPoint]$redirectListener.LocalEndpoint).Port
$probe = $null
try {
    $probe = New-FixedLoopbackProbeClient
    $firstRequest = $redirectListener.AcceptTcpClientAsync()
    $responseTask = $probe.Client.GetAsync("http://127.0.0.1:$redirectPort/initial")
    $firstClient = $firstRequest.GetAwaiter().GetResult()
    try {
        $firstStream = $firstClient.GetStream()
        $firstReader = [System.IO.StreamReader]::new($firstStream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
        while ($firstReader.ReadLine() -ne "") { }
        $redirect = "HTTP/1.1 302 Found`r`nLocation: http://127.0.0.1:$redirectPort/redirected`r`nContent-Length: 0`r`nConnection: close`r`n`r`n"
        $redirectBytes = [System.Text.Encoding]::ASCII.GetBytes($redirect)
        $firstStream.Write($redirectBytes, 0, $redirectBytes.Length)
        $firstStream.Flush()
    }
    finally {
        $firstClient.Dispose()
    }

    $redirectedRequest = $redirectListener.AcceptTcpClientAsync()
    if ($redirectedRequest.Wait(1000)) {
        $redirectedClient = $redirectedRequest.GetAwaiter().GetResult()
        try {
            $redirectedStream = $redirectedClient.GetStream()
            $redirectedResponse = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
            $redirectedStream.Write($redirectedResponse, 0, $redirectedResponse.Length)
            $redirectedStream.Flush()
        }
        finally {
            $redirectedClient.Dispose()
        }
        [void]$responseTask.GetAwaiter().GetResult()
        throw "The Phase 5 validation HTTP client followed a redirect."
    }

    $redirectResponse = $responseTask.GetAwaiter().GetResult()
    try {
        if ([int]$redirectResponse.StatusCode -ne 302) {
            throw "The Phase 5 validation HTTP client did not expose the redirect status."
        }
    }
    finally {
        $redirectResponse.Dispose()
    }
}
finally {
    if ($null -ne $probe) {
        $probe.Client.Dispose()
        $probe.Handler.Dispose()
    }
    $redirectListener.Stop()
}

$deploymentDesignText = Get-Content -LiteralPath $deploymentDesign -Raw
$deploymentPlanText = Get-Content -LiteralPath $deploymentPlanDocument -Raw
foreach ($documentText in @($deploymentDesignText, $deploymentPlanText)) {
    if ($documentText -match "Active CI and closeout runs contain no Python, pytest") {
        throw "The native deployment documentation still forbids its deliberate focused Gmail pytest exception."
    }
    if ($documentText -notmatch "focused legacy Gmail pytest regression" -or
        $documentText -notmatch "no other Python or pytest") {
        throw "The native deployment documentation does not bound the focused Gmail pytest exception."
    }
}

$deploymentText = Get-Content -LiteralPath $deploymentScript -Raw
if ($deploymentText -notmatch '(?m)^\s*\[void\]\(DisableAndDrain-OutlookHostTask\)\s*$') {
    throw "The deployment executable can leak the Outlook drain helper result into its JSON output."
}
$outlookDrainIndex = $deploymentText.LastIndexOf('DisableAndDrain-OutlookHostTask', [System.StringComparison]::Ordinal)
$poolStopIndex = $deploymentText.IndexOf('Stop-WebAppPool -Name $SiteName', [System.StringComparison]::Ordinal)
$backupIndex = $deploymentText.IndexOf('BACKUP DATABASE [FluxKnowledge]', [System.StringComparison]::Ordinal)
$verifyIndex = $deploymentText.IndexOf('RESTORE VERIFYONLY', [System.StringComparison]::Ordinal)
if ($outlookDrainIndex -lt 0 -or $poolStopIndex -lt 0 -or $backupIndex -lt 0 -or $verifyIndex -lt 0 -or
    $outlookDrainIndex -gt $backupIndex -or $poolStopIndex -gt $backupIndex -or $verifyIndex -lt $backupIndex) {
    throw "The deployment executable does not stop/drain IIS and application writers before the verified COPY_ONLY backup."
}
if ($deploymentText -match '(?i)RESTORE\s+DATABASE') {
    throw "The deployment executable must never restore the production database automatically."
}
$drainHelper = [regex]::Match(
    $deploymentText,
    '(?s)function\s+DisableAndDrain-OutlookHostTask\b.*?(?=\r?\nfunction\s+|\z)').Value
if ($drainHelper -match 'RestoreEnabledOnFailure' -or
    $deploymentText -match '\bEnable-ScheduledTask\b') {
    throw "The Outlook-disabled deployment path can re-enable a pre-existing task."
}
foreach ($activationCommand in @('Register-ScheduledTask', 'Start-ScheduledTask', 'Install-OutlookHostTask')) {
    if ($deploymentText -match ("\b{0}\b" -f [regex]::Escape($activationCommand))) {
        throw "The Outlook-disabled deployment path retains an activation command: $activationCommand"
    }
}

Write-Output "Phase 5 deployment safety contract passed."
