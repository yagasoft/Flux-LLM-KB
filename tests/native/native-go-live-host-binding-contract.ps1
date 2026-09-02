[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Message)

    try {
        & $Action
    } catch {
        if ([string]$_.Exception.Message -notmatch $Pattern) {
            throw "$Message Unexpected failure: $($_.Exception.Message)"
        }
        return
    }

    throw $Message
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$closeoutScript = Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1'
$modulePath = Join-Path $SourceRoot 'scripts\deploy\native-go-live.psm1'

foreach ($path in @($closeoutScript, $modulePath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required native go-live hand-off file is missing: $path"
}

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $closeoutScript,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) 'The closeout script does not parse.'
$requiredFunctionNames = @(
    'Invoke-NativeGoLiveComposition',
    'Invoke-NativeGoLiveModuleBridge',
    'Invoke-NativeGoLive')
$requiredFunctions = @{}
foreach ($name in $requiredFunctionNames) {
    $matches = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true))
    Assert-True ($matches.Count -eq 1) "The required closeout function is missing or ambiguous: $name"
    $requiredFunctions[$name] = $matches[0]
}

# Execute the production hand-off body with only reflection seams replaced. The
# imported production module must receive the constructed host, then stop before
# the CLR bridge has an ExecuteAsync method to invoke.
function Get-NativeGoLiveWindowsSqlClientAssemblyPath { param([string]$MergedMainRoot) return $script:FixtureAssembly }
function Get-NativeGoLiveWindowsSqlClientDependencyAssemblyPath { param([string]$MergedMainRoot) return $script:FixtureAssembly }
function Get-NativeGoLiveWindowsSqlClientNativeSniAsset { param([string]$MergedMainRoot) return [pscustomobject]@{ RuntimeIdentifier = 'win-x64'; Path = $script:FixtureAssembly } }
function Import-NativeGoLiveWindowsSqlClientAssembly { param([string]$SqlClientAssemblyPath) return $null }
function Import-NativeGoLiveWindowsSqlClientDependencyAssembly { param([string]$DependencyAssemblyPath) return $null }
function Load-NativeGoLiveWindowsSqlClientNativeSniAsset { param([string]$SqlClientNativeSniAssetPath) return $null }
function Assert-NativeGoLiveBootstrapConnection { param([string]$ConnectionString) return $null }
function Invoke-NativeGoLiveBootstrap { }
function Clear-NativeGoLiveBootstrapEnvironment { }
function Get-RequiredReflectionType { param([object]$Assembly, [string]$Name) return $Name }
function Get-RequiredReflectionMethod { param([object]$Type, [string]$Name) return "$Type::$Name" }
function Invoke-RequiredReflectionMethod {
    param([object]$Method, [object]$Instance, [object[]]$Arguments)

    switch -Regex ([string]$Method) {
        'NativeGoLivePlan::CreateProduction$' { return [pscustomobject]@{ kind = 'plan' } }
        'NativeGoLivePayloadHasher::Compute$' { return [pscustomobject]@{ Sha256 = 'fixture-manifest' } }
        'NativeGoLiveCloseoutCapabilityIssuer::Issue$' { return [pscustomobject]@{ kind = 'capability' } }
        'NativeGoLiveWindowsHostPorts::CreateProduction$' { return [pscustomobject]@{ kind = 'ports' } }
        default { throw "Unexpected reflected method: $Method" }
    }
}
function New-RequiredReflectionInstance {
    param([object]$Type, [object[]]$Arguments)

    switch ([string]$Type) {
        'FluxKnowledge.Integrations.Windows.NativeGoLive.NativeGoLiveCloseoutCapabilityIssuer' { return [pscustomobject]@{ kind = 'issuer' } }
        'FluxKnowledge.Integrations.Windows.NativeGoLive.GuardedNativeGoLiveHost' {
            $script:ConstructedNativeGoLiveHost = [pscustomobject]@{ kind = 'guarded-host'; token = [Guid]::NewGuid() }
            return $script:ConstructedNativeGoLiveHost
        }
        'FluxKnowledge.Integrations.Windows.NativeGoLive.NativeGoLiveRequest' {
            $script:ConstructedNativeGoLiveRequestArguments = $Arguments
            return [pscustomobject]@{ kind = 'request' }
        }
        default { throw "Unexpected reflected type: $Type" }
    }
}

foreach ($name in $requiredFunctionNames) {
    . ([scriptblock]::Create($requiredFunctions[$name].Extent.Text))
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeNativeGoLiveHostBinding-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $script:FixtureAssembly = [psobject].Assembly.Location
    foreach ($name in @('FluxKnowledge.Application.dll', 'FluxKnowledge.Integrations.dll')) {
        Copy-Item -LiteralPath $script:FixtureAssembly -Destination (Join-Path $temporaryRoot $name)
    }

    $distinctAcknowledgements = @{
        ConfirmCleanSlate = $false
        ConfirmConfigureVss = $true
        ConfirmDestroySql = $false
        ConfirmRegisterCodex = $true
        ConfirmRemoveLegacyPlugin = $false
    }
    $null = Invoke-NativeGoLiveComposition -MergedMainRoot $temporaryRoot -CommittedSha ('a' * 40) `
        -Acknowledgements $distinctAcknowledgements -BootstrapScript (Join-Path $temporaryRoot 'fixture.sql')
    $withoutLegacyRemovalAcknowledgement = @($script:ConstructedNativeGoLiveRequestArguments)

    $distinctAcknowledgements.ConfirmRemoveLegacyPlugin = $true
    $null = Invoke-NativeGoLiveComposition -MergedMainRoot $temporaryRoot -CommittedSha ('a' * 40) `
        -Acknowledgements $distinctAcknowledgements -BootstrapScript (Join-Path $temporaryRoot 'fixture.sql')
    $withLegacyRemovalAcknowledgement = @($script:ConstructedNativeGoLiveRequestArguments)

    Assert-True ($withoutLegacyRemovalAcknowledgement.Count -eq 10 -and
        -not [bool]$withoutLegacyRemovalAcknowledgement[2] -and
        [bool]$withoutLegacyRemovalAcknowledgement[3] -and
        -not [bool]$withoutLegacyRemovalAcknowledgement[4] -and
        [bool]$withoutLegacyRemovalAcknowledgement[5] -and
        -not [bool]$withoutLegacyRemovalAcknowledgement[6] -and
        [bool]$withLegacyRemovalAcknowledgement[6]) `
        'The reflected fifth acknowledgement does not derive specifically from ConfirmRemoveLegacyPlugin.'

    Assert-Throws -Action {
        Invoke-NativeGoLive -MergedMainRoot $temporaryRoot -CommittedSha ('a' * 40) `
            -Acknowledgements @{
                ConfirmCleanSlate = $true
                ConfirmConfigureVss = $true
                ConfirmDestroySql = $true
                ConfirmRegisterCodex = $true
                ConfirmRemoveLegacyPlugin = $true
            } -ModulePath $modulePath -BootstrapScript (Join-Path $temporaryRoot 'fixture.sql')
    } -Pattern 'native-go-live-bridge-discovery-failed' -Message `
        'The native host did not cross every PowerShell hand-off before ExecuteAsync.'

    Assert-True ($null -ne $script:ConstructedNativeGoLiveHost) 'The closeout did not construct the guarded native host.'
    Write-Output 'Native go-live host-binding contract passed.'
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
