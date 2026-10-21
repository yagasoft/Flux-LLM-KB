param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$OperatorRoot,
    [Parameter(Mandatory)][string]$IndexRoot,[Parameter(Mandatory)][string]$Configuration,
    [Parameter(Mandatory)][string]$Hold,[Parameter(Mandatory)][string]$Bindings,
    [Parameter(Mandatory)][string]$MutexName,[Parameter(Mandatory)][string]$ExpectedDatabase)
$ErrorActionPreference='Stop';$VerbosePreference='Continue'
foreach($path in @($IndexRoot,$Configuration,$Hold,$Bindings)) {
    if([IO.Path]::GetFullPath($path).StartsWith('I:\FluxKnowledge\',[StringComparison]::OrdinalIgnoreCase)){throw 'A bridge fixture cannot use production.'}
}
$configurationJson=Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
$normalised=$configurationJson.ConnectionStrings.FluxKnowledge -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$connection=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
if($connection.InitialCatalog -cne $ExpectedDatabase -or $ExpectedDatabase -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$'){throw 'Not an owned disposable database.'}
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-projection-operator.psm1') -Force
$script:observations=0
$quiescent=[Action]{$script:observations++}
$result=& (Get-Module 'incremental-projection-operator') {
    param($root,$index,$config,$hold,$binding,$check,$mutex)
    Invoke-ProjectionOperatorBridge -OperatorRoot $root -BeginMethod 'BeginForIsolatedTests' `
        -BeginArguments @($index,$config,$hold,$binding,$check,$mutex) -TimeoutSeconds 30
} $OperatorRoot $IndexRoot $Configuration $Hold (Get-Content -LiteralPath $Bindings -Raw) $quiescent $MutexName
if($script:observations -lt 3 -or $result.RecordedGeneration.FormatVersion -ne 2){throw 'Actual PowerShell bridge did not complete conversion with owner-thread checks.'}
'PASS: actual PowerShell process owns the native converter and lifecycle callbacks.'
