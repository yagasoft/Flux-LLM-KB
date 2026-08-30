[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path

$ddlPath = Join-Path $SourceRoot 'scripts\deploy\native-go-live-bootstrap.sql'
$generatorPath = Join-Path $SourceRoot 'scripts\dev\generate-native-go-live-bootstrap-manifest.ps1'
$manifestPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveSqlBootstrapAuthorityManifest.g.cs'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

foreach ($path in @($ddlPath, $generatorPath, $manifestPath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required bootstrap trust source is missing: $path"
}

$ddl = Get-Content -LiteralPath $ddlPath -Raw
$normalizedDdl = $ddl.Replace("`r`n", "`n").Replace("`r", "`n")
Assert-True ($normalizedDdl.StartsWith(":On Error exit`n", [StringComparison]::Ordinal)) `
    'The canonical bootstrap must make every failed SQL batch terminate SQLCMD before any later signing batch can run.'
Assert-True ($ddl -notmatch '(?i)(PRIVATE\s+KEY|PASSWORD\s*=|PWD\s*=|USER\s+ID\s*=|DATA\s+SOURCE\s*=)') `
    'The reviewed bootstrap DDL must not contain secret or connection material.'
Assert-True ($ddl -match 'FluxKnowledgeNativeGoLiveCertificate') 'The canonical signing certificate is missing.'
Assert-True ($ddl -match 'FluxKnowledgeNativeGoLiveObserveAppPool') 'The canonical app-pool observer is missing.'
Assert-True ($ddl -match "N'FluxKnowledge'") 'The canonical catalogue binding is missing.'
Assert-True ($ddl -match 'sys\.login_token' -and $ddl -match 'sys\.user_token') `
    'The app-pool observer must report actual effective server and database authority.'
$firstProcedure = $ddl.IndexOf('-- BEGIN HASHED PROCEDURE:', [StringComparison]::Ordinal)
$existingProcedureGuard = [regex]::Match(
    $ddl,
    "(?s)IF EXISTS \(.*?sys\.procedures.*?FluxKnowledgeNativeGoLiveCreate.*?FluxKnowledgeNativeGoLiveDrop.*?FluxKnowledgeNativeGoLiveManageAppPool.*?FluxKnowledgeNativeGoLiveObserveAppPool.*?\)\s*THROW 51000, 'native-go-live-bootstrap-procedure-already-exists', 1;")
Assert-True ($existingProcedureGuard.Success -and $existingProcedureGuard.Index -lt $firstProcedure) `
    'The clean-slate bootstrap must reject every existing canonical procedure before the creation-only definition batches.'
$creationOnlyProcedures = [regex]::Matches(
    $ddl,
    '(?m)^CREATE PROCEDURE dbo\.(FluxKnowledgeNativeGoLiveCreate|FluxKnowledgeNativeGoLiveDrop|FluxKnowledgeNativeGoLiveManageAppPool|FluxKnowledgeNativeGoLiveObserveAppPool)$')
Assert-True ($creationOnlyProcedures.Count -eq 4 -and $ddl -notmatch '(?im)^CREATE\s+OR\s+ALTER\s+PROCEDURE') `
    'Every canonical procedure must use creation-only DDL so a late pre-existing object or stale grant cannot be overwritten and signed.'
Assert-True ($ddl -match 'native-go-live-bootstrap-security-artifact-exists') `
    'The canonical bootstrap must refuse every pre-existing signing security artifact.'
Assert-True ($ddl -match 'ALTER\s+AUTHORIZATION\s+ON\s+DATABASE::\[FluxKnowledge\]') `
    'The canonical bootstrap must transfer target database ownership away from its caller.'
Assert-True ($ddl -match 'DROP\s+USER') `
    'The canonical bootstrap must remove any retained target-database bootstrap user.'
Assert-True ($ddl -notmatch 'IF\s+CERT_ID\([^\r\n]+\)\s+IS\s+NULL\s*BEGIN\s*CREATE\s+CERTIFICATE') `
    'The canonical bootstrap must never reuse a pre-existing signing certificate.'
Assert-True ($ddl -notmatch 'DROP\s+SIGNATURE') `
    'The canonical bootstrap must refuse, not replace, a pre-existing procedure signature.'
Assert-True ($ddl -match "principal\.name=N''public''" -and $ddl -match "N''FluxKnowledge''") `
    'The effective-token observer must include public authority in the target database.'
$certificateCreation = $ddl.IndexOf('CREATE CERTIFICATE FluxKnowledgeNativeGoLiveCertificate', [StringComparison]::Ordinal)
$signatureAdmission = $ddl.IndexOf("EXEC(N'ADD SIGNATURE TO OBJECT::dbo.'", [StringComparison]::Ordinal)
Assert-True ($certificateCreation -ge 0 -and $signatureAdmission -gt $certificateCreation) `
    'The clean-slate certificate must be freshly created before procedure signatures are admitted and journal-pinnable.'
$lastProcedureEnd = $ddl.IndexOf('-- END HASHED PROCEDURE: FluxKnowledgeNativeGoLiveObserveAppPool', [StringComparison]::Ordinal)
$procedurePermissionGuard = $ddl.IndexOf(
    "THROW 51000, 'native-go-live-bootstrap-procedure-permission-exists', 1;",
    [StringComparison]::Ordinal)
Assert-True ($procedurePermissionGuard -gt $lastProcedureEnd -and $procedurePermissionGuard -lt $signatureAdmission) `
    'Every canonical procedure must be rechecked for any object permission or grantee immediately before signing.'

$first = Join-Path ([System.IO.Path]::GetTempPath()) ("native-go-live-manifest-{0}.cs" -f [Guid]::NewGuid().ToString('N'))
$second = Join-Path ([System.IO.Path]::GetTempPath()) ("native-go-live-manifest-{0}.cs" -f [Guid]::NewGuid().ToString('N'))
try {
    & pwsh -NoProfile -File $generatorPath -SourcePath $ddlPath -OutputPath $first
    Assert-True ($LASTEXITCODE -eq 0) 'The bootstrap manifest generator failed.'
    & pwsh -NoProfile -File $generatorPath -SourcePath $ddlPath -OutputPath $second
    Assert-True ($LASTEXITCODE -eq 0) 'The second bootstrap manifest generation failed.'

    $firstBytes = [System.IO.File]::ReadAllBytes($first)
    $secondBytes = [System.IO.File]::ReadAllBytes($second)
    $committedBytes = [System.IO.File]::ReadAllBytes($manifestPath)
    Assert-True ([System.Linq.Enumerable]::SequenceEqual[byte]($firstBytes, $secondBytes)) `
        'The bootstrap manifest generator is not byte reproducible.'
    Assert-True ([System.Linq.Enumerable]::SequenceEqual[byte]($firstBytes, $committedBytes)) `
        'The committed bootstrap manifest does not match the reviewed DDL source.'

    $mutants = [ordered]@{
        continue_after_failed_create = $ddl.Replace(':On Error exit', ':On Error ignore')
        missing_existing_procedure_guard = $ddl.Remove(
            $existingProcedureGuard.Index,
            $existingProcedureGuard.Length)
        reusable_certificate = $ddl.Replace(
            "IF CERT_ID(N'FluxKnowledgeNativeGoLiveCertificate') IS NOT NULL",
            "IF CERT_ID(N'FluxKnowledgeNativeGoLiveCertificate') IS NULL")
        missing_security_refusal = $ddl.Replace(
            "THROW 51000, 'native-go-live-bootstrap-security-artifact-exists', 1;",
            "THROW 51000, 'native-go-live-bootstrap-security-artifact-ignored', 1;")
        accept_mismatched_certificate_login_sid = $ddl.Replace(
            '@SigningCertificateLoginSid <> @SigningCertificateThumbprint',
            '@SigningCertificateLoginSid = @SigningCertificateThumbprint')
        allow_existing_procedure_replacement = $ddl.Replace(
            'CREATE PROCEDURE dbo.FluxKnowledgeNativeGoLiveCreate',
            'CREATE OR ALTER PROCEDURE dbo.FluxKnowledgeNativeGoLiveCreate')
        ignore_raced_procedure_permission = $ddl.Replace(
            "THROW 51000, 'native-go-live-bootstrap-procedure-permission-exists', 1;",
            "THROW 51000, 'native-go-live-bootstrap-procedure-permission-ignored', 1;")
        replace_existing_signature = $ddl.Replace(
            "EXEC(N'ADD SIGNATURE TO OBJECT::dbo.'+QUOTENAME(@Procedure)+N' BY CERTIFICATE '+QUOTENAME(@Certificate)+N';');",
            "EXEC(N'DROP SIGNATURE FROM OBJECT::dbo.'+QUOTENAME(@Procedure)+N' BY CERTIFICATE '+QUOTENAME(@Certificate)+N';');")
    }
    foreach ($mutant in $mutants.GetEnumerator()) {
        Assert-True ($mutant.Value -ne $ddl) "The $($mutant.Key) generator mutant did not alter the canonical DDL."
        $mutantSource = Join-Path ([System.IO.Path]::GetTempPath()) ("native-go-live-bootstrap-mutant-{0}.sql" -f [Guid]::NewGuid().ToString('N'))
        $mutantOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("native-go-live-bootstrap-mutant-{0}.cs" -f [Guid]::NewGuid().ToString('N'))
        try {
            [System.IO.File]::WriteAllText($mutantSource, $mutant.Value, [System.Text.UTF8Encoding]::new($false))
            & pwsh -NoProfile -File $generatorPath -SourcePath $mutantSource -OutputPath $mutantOutput 2>$null
            Assert-True ($LASTEXITCODE -ne 0) "The generator accepted the $($mutant.Key) security-contract mutant."
        }
        finally {
            Remove-Item -LiteralPath $mutantSource, $mutantOutput -Force -ErrorAction SilentlyContinue
        }
    }

    $manifest = [System.Text.Encoding]::UTF8.GetString($committedBytes)
    $expected = [ordered]@{
        BootstrapSourceSha256 = 'b991bb0d1308a1f3af601c5873279ddddfa25cf92eeaba943480600bf792bfe4'
        SecurityBootstrapSha256 = '4caf0596fde411c6bbffeb4666ff941787fbda3a74dcfcf8043bda3a293f26a3'
        ProcedureManifestSha256 = 'aa558b3269f879311cbfaaafc22acc6320023b02307eea90221a5f43a5364527'
        FluxKnowledgeNativeGoLiveCreate = '1a4d9a8711cbc83e330f59814bfcc5eeea77260ac5e8ad74593481070bb16213'
        FluxKnowledgeNativeGoLiveDrop = '1d66fd3beafb537f12bc2e91655de2b68390500775056eb931018f80e20b7f66'
        FluxKnowledgeNativeGoLiveManageAppPool = '54647da826b0b5e3590f1abb28e620c461a1121975f85d46690325b2f455dcde'
        FluxKnowledgeNativeGoLiveObserveAppPool = '970fbd3d0f332311cdaeb2d1dd60a009315b61e4feffd363b9013b2a82bd6629'
    }
    foreach ($entry in $expected.GetEnumerator()) {
        $needle = if ($entry.Key.EndsWith('Sha256', [StringComparison]::Ordinal)) {
            "const string $($entry.Key) = `"$($entry.Value)`";"
        }
        else {
            "`"$($entry.Key)`" => `"$($entry.Value)`""
        }
        Assert-True ($manifest.Contains($needle, [StringComparison]::Ordinal)) `
            "The fixed reviewed hash for $($entry.Key) changed unexpectedly."
    }
    Assert-True ($manifest.Contains(
            'const string SigningCertificateLoginName = "FluxKnowledgeNativeGoLiveCertificateLogin";',
            [StringComparison]::Ordinal)) `
        'The generated trust manifest does not bind the fixed certificate-mapped login name.'
}
finally {
    Remove-Item -LiteralPath $first, $second -Force -ErrorAction SilentlyContinue
}

Write-Output 'Native go-live bootstrap manifest passed.'
