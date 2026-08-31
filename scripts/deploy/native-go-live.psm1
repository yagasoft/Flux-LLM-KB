Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:CanonicalRoot = 'I:\FluxKnowledge'
$script:CanonicalApplicationRoot = 'I:\FluxKnowledge\App'
$script:CanonicalConfigRoot = 'I:\FluxKnowledge\Config'
$script:CanonicalSqlDataFile = 'I:\FluxKnowledge\Data\Sql\Data\FluxKnowledge.mdf'
$script:CanonicalSqlLogFile = 'I:\FluxKnowledge\Data\Sql\Log\FluxKnowledge_log.ldf'
$script:CanonicalCodexRoot = 'I:\FluxKnowledge\CodexPlugin'
$script:CanonicalSiteName = 'FluxKnowledge'
$script:CanonicalLoopbackPort = 5137
$script:CanonicalMarketplaceName = 'fluxknowledge'
$script:CanonicalVssFraction = [decimal]::Parse('0.10', [Globalization.CultureInfo]::InvariantCulture)
$script:RequiredMcpTools = @(
    'knowledge.search',
    'knowledge.write',
    'knowledge.graph',
    'code.query',
    'code.write',
    'corpus.query',
    'corpus.write',
    'operations.status',
    'operations.audit'
)

function Get-NativeGoLivePlanHash {
    param([Parameter(Mandatory)][string]$CommittedSha)

    $canonical = @(
        'native-go-live-v1',
        $script:CanonicalRoot,
        'FluxKnowledge',
        $script:CanonicalSqlDataFile,
        $script:CanonicalSqlLogFile,
        'I:',
        '0.10',
        $script:CanonicalCodexRoot,
        $script:CanonicalMarketplaceName,
        $script:CanonicalMarketplaceName,
        $script:CanonicalSiteName,
        '5137',
        '18000000000',
        $CommittedSha
    ) -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($canonical)
    try {
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Get-NativeGoLivePlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9a-f]{40}$')]
        [string]$CommittedSha
    )

    [pscustomobject]@{
        PSTypeName = 'FluxKnowledge.NativeGoLive.Plan.v1'
        root = $script:CanonicalRoot
        applicationRoot = $script:CanonicalApplicationRoot
        configRoot = $script:CanonicalConfigRoot
        sql = [pscustomobject]@{
            catalogName = 'FluxKnowledge'
            dataFilePath = $script:CanonicalSqlDataFile
            logFilePath = $script:CanonicalSqlLogFile
        }
        vss = [pscustomobject]@{
            volume = 'I:'
            maximumStorageFraction = $script:CanonicalVssFraction
        }
        codex = [pscustomobject]@{
            marketplaceRoot = $script:CanonicalCodexRoot
            marketplaceName = $script:CanonicalMarketplaceName
            pluginName = $script:CanonicalMarketplaceName
        }
        siteName = $script:CanonicalSiteName
        appPoolName = $script:CanonicalSiteName
        loopbackPort = $script:CanonicalLoopbackPort
        committedSha = $CommittedSha
        planHash = Get-NativeGoLivePlanHash -CommittedSha $CommittedSha
        executionAvailable = $false
    }
}

function Clear-NativeGoLiveBootstrapEnvironment {
    [Environment]::SetEnvironmentVariable(
        'FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP',
        $null,
        [EnvironmentVariableTarget]::Process)
    Remove-Item Env:\FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP -ErrorAction SilentlyContinue
}

function Invoke-NativeGoLive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$CapabilityIssuer,
        [Parameter(Mandatory)][object]$Capability,
        [Parameter(Mandatory)][object]$Request,
        [Parameter(Mandatory)][object]$NativeGoLiveHost
    )

    try {
        try {
            $assembly = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
                $_.GetName().Name -ceq 'FluxKnowledge.Integrations'
            })
            if ($assembly.Count -ne 1) { throw 'go-live-closeout-assembly-unavailable' }
            $bridgeType = $assembly[0].GetType(
                'FluxKnowledge.Integrations.Windows.NativeGoLive.NativeGoLiveCloseoutBridge',
                $true,
                $false)
            $flags = [Reflection.BindingFlags]'Static,NonPublic'
            $method = $bridgeType.GetMethod('ExecuteAsync', $flags)
            if ($null -eq $method) { throw 'go-live-closeout-bridge-unavailable' }
        }
        catch {
            throw 'native-go-live-bridge-discovery-failed'
        }

        try {
            $task = $method.Invoke($null, @(
                $CapabilityIssuer,
                $Capability,
                $Request,
                $NativeGoLiveHost,
                [Threading.CancellationToken]::None))
        }
        catch {
            if ($null -ne $_.Exception.InnerException -and
                $_.Exception.InnerException.Message -ceq 'go-live-closeout-capability-unrecognised') {
                throw $_.Exception.InnerException
            }
            throw 'native-go-live-bridge-call-failed'
        }

        try {
            return $task.GetAwaiter().GetResult()
        }
        catch {
            throw 'native-go-live-bridge-result-failed'
        }
    }
    finally {
        Clear-NativeGoLiveBootstrapEnvironment
    }
}

Export-ModuleMember -Function Get-NativeGoLivePlan
