[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$SourcePath = (Resolve-Path -LiteralPath $SourcePath).Path
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$source = [System.IO.File]::ReadAllText($SourcePath).Replace("`r`n", "`n").Replace("`r", "`n")
if (-not $source.StartsWith(":On Error exit`n", [StringComparison]::Ordinal)) {
    throw 'The bootstrap source must terminate SQLCMD on the first failed batch.'
}
$certificateName = 'FluxKnowledgeNativeGoLiveCertificate'
$certificateLoginName = 'FluxKnowledgeNativeGoLiveCertificateLogin'
$procedureNames = @(
    'FluxKnowledgeNativeGoLiveCreate'
    'FluxKnowledgeNativeGoLiveDrop'
    'FluxKnowledgeNativeGoLiveManageAppPool'
    'FluxKnowledgeNativeGoLiveObserveAppPool'
)
$securityStart = "-- BEGIN HASHED SECURITY BOOTSTRAP`n"
$securityEnd = "-- END HASHED SECURITY BOOTSTRAP"
$firstProcedure = $source.IndexOf('-- BEGIN HASHED PROCEDURE:', [StringComparison]::Ordinal)
$existingProcedureGuard = [regex]::Match(
    $source,
    "(?s)IF EXISTS \(.*?sys\.procedures.*?FluxKnowledgeNativeGoLiveCreate.*?FluxKnowledgeNativeGoLiveDrop.*?FluxKnowledgeNativeGoLiveManageAppPool.*?FluxKnowledgeNativeGoLiveObserveAppPool.*?\)\s*THROW 51000, 'native-go-live-bootstrap-procedure-already-exists', 1;")
if (-not $existingProcedureGuard.Success -or $existingProcedureGuard.Index -ge $firstProcedure) {
    throw 'The bootstrap source does not refuse existing canonical procedure objects before definition.'
}

function Get-Sha256 {
    param([byte[]]$Bytes)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($sha256.ComputeHash($Bytes)).ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

$definitionHashes = [ordered]@{}
$sourceHash = Get-Sha256 ([System.Text.Encoding]::UTF8.GetBytes($source))
$securityStartIndex = $source.IndexOf($securityStart, [StringComparison]::Ordinal)
if ($securityStartIndex -lt 0) { throw 'Missing security bootstrap start marker.' }
$securityDefinitionStart = $securityStartIndex + $securityStart.Length
$securityEndIndex = $source.IndexOf($securityEnd, $securityDefinitionStart, [StringComparison]::Ordinal)
if ($securityEndIndex -lt 0) { throw 'Missing security bootstrap end marker.' }
$securityDefinition = $source.Substring($securityDefinitionStart, $securityEndIndex - $securityDefinitionStart)
$requiredSecurityFragments = @(
    "IF CERT_ID(N'FluxKnowledgeNativeGoLiveCertificate') IS NOT NULL OR"
    "SUSER_ID(N'FluxKnowledgeNativeGoLiveCertificateLogin') IS NOT NULL OR"
    'FROM sys.crypt_properties property'
    "THROW 51000, 'native-go-live-bootstrap-security-artifact-exists', 1;"
    'CREATE CERTIFICATE FluxKnowledgeNativeGoLiveCertificate'
    'CREATE LOGIN FluxKnowledgeNativeGoLiveCertificateLogin'
    'FROM CERTIFICATE FluxKnowledgeNativeGoLiveCertificate;'
    "THROW 51000, 'native-go-live-bootstrap-security-artifact-creation-not-proved', 1;"
    "SUSER_SID(N'FluxKnowledgeNativeGoLiveCertificateLogin')"
    '@SigningCertificateLoginSid <> @SigningCertificateThumbprint'
    "THROW 51000, 'native-go-live-bootstrap-certificate-login-mismatch', 1;"
)
foreach ($fragment in $requiredSecurityFragments) {
    if (-not $securityDefinition.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "The clean-slate security bootstrap is missing: $fragment"
    }
}
if ($source -match '(?i)DROP\s+SIGNATURE') {
    throw 'The clean-slate security bootstrap must refuse rather than replace existing signatures.'
}
$guardIndex = $securityDefinition.IndexOf("IF CERT_ID(N'FluxKnowledgeNativeGoLiveCertificate') IS NOT NULL OR", [StringComparison]::Ordinal)
$certificateIndex = $securityDefinition.IndexOf('CREATE CERTIFICATE FluxKnowledgeNativeGoLiveCertificate', [StringComparison]::Ordinal)
$loginIndex = $securityDefinition.IndexOf('CREATE LOGIN FluxKnowledgeNativeGoLiveCertificateLogin', [StringComparison]::Ordinal)
$proofIndex = $securityDefinition.IndexOf("THROW 51000, 'native-go-live-bootstrap-security-artifact-creation-not-proved', 1;", [StringComparison]::Ordinal)
if (-not ($guardIndex -lt $certificateIndex -and $certificateIndex -lt $loginIndex -and $loginIndex -lt $proofIndex)) {
    throw 'The fresh certificate/login creation order is not canonical.'
}
$signatureIndex = $source.IndexOf("EXEC(N'ADD SIGNATURE TO OBJECT::dbo.'", $securityEndIndex, [StringComparison]::Ordinal)
if ($signatureIndex -lt $securityEndIndex) {
    throw 'The clean-slate certificate/login must be created and proved before procedure signatures are admitted.'
}
$securityHash = Get-Sha256 ([System.Text.Encoding]::UTF8.GetBytes($securityDefinition))
foreach ($name in $procedureNames) {
    $start = "-- BEGIN HASHED PROCEDURE: $name`n"
    $end = "GO`n-- END HASHED PROCEDURE: $name"
    $startIndex = $source.IndexOf($start, [StringComparison]::Ordinal)
    if ($startIndex -lt 0) { throw "Missing bootstrap procedure start marker: $name" }
    $definitionStart = $startIndex + $start.Length
    $endIndex = $source.IndexOf($end, $definitionStart, [StringComparison]::Ordinal)
    if ($endIndex -lt 0) { throw "Missing bootstrap procedure end marker: $name" }
    $definition = $source.Substring($definitionStart, $endIndex - $definitionStart)
    if (-not $definition.StartsWith("CREATE PROCEDURE dbo.$name`n", [StringComparison]::Ordinal)) {
        throw "The hashed definition is not the expected creation-only canonical procedure: $name"
    }
    if ($definition -match '(?m)^GO\s*$') { throw "A nested batch separator exists in $name" }
    $definitionHashes[$name] = Get-Sha256 ([System.Text.Encoding]::Unicode.GetBytes($definition))
}

$lastProcedureEndIndex = $source.IndexOf(
    '-- END HASHED PROCEDURE: FluxKnowledgeNativeGoLiveObserveAppPool',
    [StringComparison]::Ordinal)
$procedurePermissionGuardIndex = $source.IndexOf(
    "IF EXISTS (`n    SELECT 1`n    FROM sys.database_permissions procedure_permission",
    [StringComparison]::Ordinal)
$procedurePermissionRefusalIndex = $source.IndexOf(
    "THROW 51000, 'native-go-live-bootstrap-procedure-permission-exists', 1;",
    [StringComparison]::Ordinal)
if ($procedurePermissionGuardIndex -le $lastProcedureEndIndex -or
    $procedurePermissionRefusalIndex -le $procedurePermissionGuardIndex -or
    $signatureIndex -le $procedurePermissionRefusalIndex) {
    throw 'Every canonical procedure must have exact permission absence rechecked immediately before signing.'
}
$procedurePermissionGuard = $source.Substring(
    $procedurePermissionGuardIndex,
    $procedurePermissionRefusalIndex - $procedurePermissionGuardIndex)
foreach ($name in $procedureNames) {
    if (-not $procedurePermissionGuard.Contains("N'$name'", [StringComparison]::Ordinal)) {
        throw "The pre-sign permission guard does not cover the canonical procedure: $name"
    }
}
foreach ($fragment in @(
    'JOIN sys.procedures procedure_object ON procedure_object.object_id=procedure_permission.major_id'
    "procedure_permission.class_desc=N'OBJECT_OR_COLUMN'"
    "procedure_schema.name=N'dbo'")) {
    if (-not $procedurePermissionGuard.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "The pre-sign permission guard is missing: $fragment"
    }
}

$manifestInput = "native-go-live-bootstrap-manifest-v2`nsource:$sourceHash`nsecurity:$securityHash`ncertificate:$certificateName`ncertificate-login:$certificateLoginName`n"
foreach ($name in $procedureNames) {
    $manifestInput += "${name}:$($definitionHashes[$name])`n"
}
$manifestHash = Get-Sha256 ([System.Text.Encoding]::UTF8.GetBytes($manifestInput))

$generated = @"
// <auto-generated />
namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

internal static partial class NativeGoLiveSqlBootstrapAuthorityContract
{
    internal const string BootstrapSourceSha256 = "$sourceHash";
    internal const string SecurityBootstrapSha256 = "$securityHash";
    internal const string SigningCertificateName = "$certificateName";
    internal const string SigningCertificateLoginName = "$certificateLoginName";
    internal const string ProcedureManifestSha256 = "$manifestHash";

    internal static string DefinitionSha256(string procedureName) => procedureName switch
    {
        "FluxKnowledgeNativeGoLiveCreate" => "$($definitionHashes['FluxKnowledgeNativeGoLiveCreate'])",
        "FluxKnowledgeNativeGoLiveDrop" => "$($definitionHashes['FluxKnowledgeNativeGoLiveDrop'])",
        "FluxKnowledgeNativeGoLiveManageAppPool" => "$($definitionHashes['FluxKnowledgeNativeGoLiveManageAppPool'])",
        "FluxKnowledgeNativeGoLiveObserveAppPool" => "$($definitionHashes['FluxKnowledgeNativeGoLiveObserveAppPool'])",
        _ => throw new ArgumentOutOfRangeException(nameof(procedureName))
    };
}
"@.Replace("`r`n", "`n").Replace("`r", "`n")

$outputDirectory = [System.IO.Path]::GetDirectoryName($OutputPath)
if (-not [string]::IsNullOrEmpty($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
[System.IO.File]::WriteAllText($OutputPath, $generated, [System.Text.UTF8Encoding]::new($false))
