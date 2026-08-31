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
$handoffFunction = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Invoke-NativeGoLive' }, $true))
Assert-True ($handoffFunction.Count -eq 1) 'The closeout hand-off function is missing or ambiguous.'

# Execute the production hand-off body with only reflection seams replaced. The
# imported production module must receive the constructed host, then stop before
# the CLR bridge has an ExecuteAsync method to invoke.
function Get-NativeGoLiveWindowsSqlClientAssemblyPath { param([string]$MergedMainRoot) return $script:FixtureAssembly }
function Import-NativeGoLiveWindowsSqlClientAssembly { param([string]$SqlClientAssemblyPath) return $null }
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
        'FluxKnowledge.Integrations.Windows.NativeGoLive.NativeGoLiveRequest' { return [pscustomobject]@{ kind = 'request' } }
        default { throw "Unexpected reflected type: $Type" }
    }
}

. ([scriptblock]::Create($handoffFunction[0].Extent.Text))

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeNativeGoLiveHostBinding-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $script:FixtureAssembly = [psobject].Assembly.Location
    foreach ($name in @('FluxKnowledge.Application.dll', 'FluxKnowledge.Integrations.dll')) {
        Copy-Item -LiteralPath $script:FixtureAssembly -Destination (Join-Path $temporaryRoot $name)
    }

    Assert-Throws -Action {
        Invoke-NativeGoLive -MergedMainRoot $temporaryRoot -CommittedSha ('a' * 40) `
            -Acknowledgements @{
                ConfirmCleanSlate = $true
                ConfirmConfigureVss = $true
                ConfirmDestroySql = $true
                ConfirmRegisterCodex = $true
            } -ModulePath $modulePath -BootstrapScript (Join-Path $temporaryRoot 'fixture.sql')
    } -Pattern 'go-live-closeout-assembly-unavailable' -Message `
        'The native host did not cross every PowerShell hand-off before ExecuteAsync.'

    Assert-True ($null -ne $script:ConstructedNativeGoLiveHost) 'The closeout did not construct the guarded native host.'
    Write-Output 'Native go-live host-binding contract passed.'
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
