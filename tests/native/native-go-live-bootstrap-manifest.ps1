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
$manifestPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveSqlBootstrapManifest.g.cs'

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
    'The canonical bootstrap must make every failed SQL batch terminate SQLCMD.'
Assert-True ($ddl -notmatch '(?i)(PRIVATE\s+KEY|PASSWORD\s*=|PWD\s*=|USER\s+ID\s*=|DATA\s+SOURCE\s*=)') `
    'The reviewed bootstrap DDL must not contain secret or connection material.'
Assert-True ($ddl -notmatch '(?i)(CERTIFICATE|ADD\s+SIGNATURE|DROP\s+SIGNATURE|CRYPT_PROPERTIES)') `
    'The direct-admin bootstrap must contain no certificate or procedure-signing machinery.'
Assert-True ($ddl -notmatch 'IIS AppPool\\FluxKnowledge|ALTER\s+AUTHORIZATION\s+ON\s+DATABASE') `
    'The bootstrap must not create, transfer to, or observe an app-pool SQL identity.'
Assert-True ($ddl -match "N'FluxKnowledge'") 'The canonical catalogue binding is missing.'
$firstProcedure = $ddl.IndexOf('-- BEGIN HASHED PROCEDURE:', [StringComparison]::Ordinal)
$existingProcedureGuard = [regex]::Match(
    $ddl,
    "(?s)IF EXISTS \(.*?sys\.procedures.*?FluxKnowledgeNativeGoLiveCreate.*?FluxKnowledgeNativeGoLiveDrop.*?\)\s*THROW 51000, 'native-go-live-bootstrap-procedure-already-exists', 1;")
Assert-True ($existingProcedureGuard.Success -and $existingProcedureGuard.Index -lt $firstProcedure) `
    'The clean-slate bootstrap must reject every existing canonical procedure before the creation-only definition batches.'
$creationOnlyProcedures = [regex]::Matches(
    $ddl,
    '(?m)^CREATE PROCEDURE dbo\.(FluxKnowledgeNativeGoLiveCreate|FluxKnowledgeNativeGoLiveDrop)$')
Assert-True ($creationOnlyProcedures.Count -eq 2 -and $ddl -notmatch '(?im)^CREATE\s+OR\s+ALTER\s+PROCEDURE') `
    'Every canonical procedure must use creation-only DDL so a late pre-existing object cannot be overwritten.'
Assert-True (-not ($ddl -match '(?i)(CREATE\s+USER|DROP\s+USER|sp_addrolemember|db_datareader|db_datawriter)')) `
    'The direct-admin bootstrap must not retain named database-user or role management.'

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
        allow_existing_procedure_replacement = $ddl.Replace(
            'CREATE PROCEDURE dbo.FluxKnowledgeNativeGoLiveCreate',
            'CREATE OR ALTER PROCEDURE dbo.FluxKnowledgeNativeGoLiveCreate')
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
    Assert-True ($manifest -notmatch '(?i)(Certificate|Signature|Thumbprint)') `
        'The generated direct-admin manifest must contain no signing evidence.'
    Assert-True ($manifest.Contains('ProcedureManifestSha256', [StringComparison]::Ordinal)) `
        'The generated manifest must retain the fixed procedure hash contract.'
}
finally {
    Remove-Item -LiteralPath $first, $second -Force -ErrorAction SilentlyContinue
}

Write-Output 'Native go-live bootstrap manifest passed.'
