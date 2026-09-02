[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$guardScript = Join-Path $SourceRoot "scripts\dev\assert-legacy-gmail-unchanged.ps1"
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "FluxKnowledgeGmailGuard-$([Guid]::NewGuid().ToString('N'))"
$mainRoot = Join-Path $temporaryRoot "main"
$featureRoot = Join-Path $temporaryRoot "feature"
$approvedPaths = @(
    "dashboard/src/App.overview-performance.test.tsx",
    "dashboard/src/App.review-jobs.test.tsx",
    "dashboard/src/App.tsx",
    "dashboard/src/test/appHarness.ts",
    "docs/integrations.md",
    "src/flux_llm_kb/cli.py",
    "src/flux_llm_kb/dashboard_static/assets/index-BUYBjKEy.js",
    "src/flux_llm_kb/dashboard_static/assets/index-HCFctpq0.js",
    "src/flux_llm_kb/dashboard_static/index.html",
    "src/flux_llm_kb/database.py",
    "src/flux_llm_kb/rest_api.py",
    "src/flux_llm_kb/service.py",
    "tests/test_mail_cli_rest.py"
)

function Invoke-Guard {
    param([switch]$Confirm)

    $arguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $guardScript,
        "-RepositoryRoot", $featureRoot, "-BaselineRef", "main"
    )
    if ($Confirm) {
        $arguments += "-ConfirmApprovedLegacyLocalSurfaceChanges"
    }
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell @arguments 2>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

try {
    New-Item -ItemType Directory -Path $mainRoot | Out-Null
    & git init --initial-branch main $mainRoot | Out-Null
    & git -C $mainRoot config core.autocrlf false
    & git -C $mainRoot config user.email "gmail-guard@example.invalid"
    & git -C $mainRoot config user.name "Gmail Guard Test"
    foreach ($relativePath in $approvedPaths) {
        if ($relativePath -eq "src/flux_llm_kb/dashboard_static/assets/index-BUYBjKEy.js") {
            continue
        }
        $path = Join-Path $mainRoot $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        Set-Content -LiteralPath $path -Value "# baseline fixture"
    }
    $gmailPath = Join-Path $mainRoot "tests/test_mail_oauth.py"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $gmailPath) | Out-Null
    Set-Content -LiteralPath $gmailPath -Value "# preserved Gmail fixture"
    & git -C $mainRoot add .
    & git -C $mainRoot commit -m "baseline" | Out-Null
    & git -C $mainRoot worktree add -b codex/approved-local-surface $featureRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create the Gmail guard test worktree."
    }

    foreach ($relativePath in $approvedPaths) {
        $target = Join-Path $featureRoot $relativePath
        $source = Join-Path $SourceRoot $relativePath
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
        elseif (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force
        }
    }

    $defaultResult = Invoke-Guard
    if ($defaultResult.ExitCode -eq 0) {
        throw "The Gmail guard accepted approved local-surface changes without explicit confirmation."
    }
    $confirmedResult = Invoke-Guard -Confirm
    if ($confirmedResult.ExitCode -ne 0) {
        throw "The Gmail guard rejected the exact approved local-surface identities: $($confirmedResult.Output)"
    }

    Add-Content -LiteralPath (Join-Path $featureRoot "tests/test_mail_cli_rest.py") -Value "# prohibited mutation after approval"
    $identityResult = Invoke-Guard -Confirm
    if ($identityResult.ExitCode -eq 0) {
        throw "The Gmail guard accepted a mutation to an approved-path identity."
    }
    Copy-Item -LiteralPath (Join-Path $SourceRoot "tests/test_mail_cli_rest.py") `
        -Destination (Join-Path $featureRoot "tests/test_mail_cli_rest.py") -Force

    Set-Content -LiteralPath (Join-Path $featureRoot "tests/test_mail_oauth.py") -Value "# prohibited Gmail mutation"
    $gmailResult = Invoke-Guard -Confirm
    if ($gmailResult.ExitCode -eq 0) {
        throw "The Gmail guard accepted an actual Gmail-owned mutation under the Phase 5 confirmation."
    }

    Write-Output "Approved local-surface Gmail guard contract passed."
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
        $temporaryParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))
        if (-not $resolvedTemporaryRoot.StartsWith(
                "$temporaryParent$([System.IO.Path]::DirectorySeparatorChar)FluxKnowledgeGmailGuard-",
                [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemporaryRoot) -notmatch '^FluxKnowledgeGmailGuard-[0-9a-f]{32}$') {
            throw "The Gmail guard test cleanup target is outside its disposable boundary."
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
