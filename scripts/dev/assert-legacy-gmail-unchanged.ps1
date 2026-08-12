[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Get-Location).Path,
    [string]$BaselineRef = "main",
    [switch]$ConfirmApprovedLegacyLocalSurfaceChanges,
    [switch]$ResumeBoundary,
    [string]$ExpectedOriginUrl = "",
    [string]$ExpectedHead = "",
    [string]$ExpectedBranch = ""
)

$ErrorActionPreference = "Stop"
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$resumeBoundaryObject = $null
if ($ResumeBoundary) {
    if ($ExpectedOriginUrl -cne 'https://github.com/yagasoft/Flux-LLM-KB.git' -or $ExpectedHead -cnotmatch '^[0-9a-f]{40}$' -or [string]::IsNullOrWhiteSpace($ExpectedBranch)) {
        throw 'Resume Gmail guard requires the exact expected origin and canonical authenticated head/branch.'
    }
    $boundaryModule = Join-Path $PSScriptRoot 'ResumeGitBoundary.psm1'
    Import-Module $boundaryModule -Force
    $resumeBoundaryObject = New-ResumeGitBoundary -Worktree $RepositoryRoot -ExpectedOriginUrl $ExpectedOriginUrl -ExpectedHead $ExpectedHead -ExpectedBranch $ExpectedBranch
}
function Invoke-GmailGit {
    param([string[]]$Arguments)
    if ($null -ne $resumeBoundaryObject) {
        $result = Invoke-ResumeGit -Boundary $resumeBoundaryObject -Arguments $Arguments
        if ($result.ExitCode -ne 0) { throw "Authenticated Gmail guard Git $($Arguments[0]) operation failed." }
        return $result.StdOut
    }
    $nativeErrorPreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    $previousNativeErrorPreference = if ($null -ne $nativeErrorPreference) { [bool]$nativeErrorPreference.Value } else { $null }
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # A user PowerShell profile can opt native stderr into the error stream
        # even when Git succeeds (for example harmless autocrlf warnings).
        if ($null -ne $nativeErrorPreference) { $PSNativeCommandUseErrorActionPreference = $false }
        $ErrorActionPreference = 'Continue'
        $raw = & git -C $RepositoryRoot @Arguments 2>$null | Out-String
        if ($LASTEXITCODE -ne 0) { throw "Legacy Gmail guard Git $($Arguments[0]) operation failed." }
        return $raw
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($null -ne $nativeErrorPreference) { $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference }
    }
}
try {
$protectedPaths = @(
    "src/flux_llm_kb/mail_content_store.py",
    "src/flux_llm_kb/mail_ingestion.py",
    "src/flux_llm_kb/mail_oauth.py",
    "src/flux_llm_kb/mail_post_process.py",
    "src/flux_llm_kb/service.py",
    "src/flux_llm_kb/event_scheduler.py",
    "src/flux_llm_kb/event_worker.py",
    "src/flux_llm_kb/messaging.py",
    "src/flux_llm_kb/database.py",
    "src/flux_llm_kb/cli.py",
    "src/flux_llm_kb/rest_api.py",
    "src/flux_llm_kb/settings.py",
    "src/flux_llm_kb/settings_registry.py",
    "src/flux_llm_kb/sql/0004_runtime_settings_mail.sql",
    "src/flux_llm_kb/sql/0005_mail_oauth.sql",
    "src/flux_llm_kb/sql/0009_imap_scheduler_state_machine.sql",
    "src/flux_llm_kb/sql/0011_mail_post_process.sql",
    "dashboard",
    "src/flux_llm_kb/dashboard_static",
    "tests/test_mail_cli_rest.py",
    "tests/test_mail_ingestion.py",
    "tests/test_mail_oauth.py",
    "tests/test_mail_post_process.py",
    "tests/test_mail_scheduler.py",
    "tests/test_background_jobs.py",
    "tests/test_worker.py",
    "README.md",
    "docs/integrations.md",
    "docs/setup.md",
    "docs/user-guide"
)
$approvedLegacyLocalSurfaceIdentities = @{
    "dashboard/src/App.overview-performance.test.tsx" = "a3a5cef85217dcf6aa1964a6a25beb9aa78b94e8"
    "dashboard/src/App.review-jobs.test.tsx" = "85229089458b2f36698ba780f707c408e270bae9"
    "dashboard/src/App.tsx" = "444d515b3ae46cd6a41b5a151e408d01b39e04bb"
    "dashboard/src/test/appHarness.ts" = "e60c3a9d74dad663e8f30a4d907a92e8b2641a1c"
    "docs/integrations.md" = "b69f6b16b1c9201b133d2994fbdb96f1b2ed1bee"
    "src/flux_llm_kb/cli.py" = "dc98ccb7a7531119f1fdc6776247f090483581ea"
    "src/flux_llm_kb/dashboard_static/assets/index-BUYBjKEy.js" = "95267d82860e2bf870285ea3e40f8ca3666b6cd1"
    "src/flux_llm_kb/dashboard_static/assets/index-HCFctpq0.js" = "<absent>"
    "src/flux_llm_kb/dashboard_static/index.html" = "cf9b12e2b1cbd0f3857cb3a85d94b28469b0bb67"
    "src/flux_llm_kb/database.py" = "2e303ef98da5e06a81ef58e485126096dd051349"
    "src/flux_llm_kb/rest_api.py" = "64582fc2febb7cb89f2ca88ecaaa671f3a8a7e6a"
    "src/flux_llm_kb/service.py" = "20017dec242851f67edc220a2153863b6ad1d580"
    "tests/test_mail_cli_rest.py" = "f8b432483380de2109475866228803f873e27dfb"
}

function Get-WorktreeBlobIdentity {
    param([string]$RelativePath)

    $fullPath = Join-Path $RepositoryRoot ($RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        return "<absent>"
    }

    $identity = (Invoke-GmailGit -Arguments @('hash-object', ('--path=' + $RelativePath), '--', $RelativePath)).Trim()
    if ($identity -notmatch '^[0-9a-f]{40,64}$') {
        throw "Unable to calculate an approved local-surface blob identity."
    }
    return $identity
}

$mergeBase = (Invoke-GmailGit -Arguments @('merge-base', $BaselineRef, 'HEAD')).Trim()
if ([string]::IsNullOrWhiteSpace($mergeBase)) {
    throw "Unable to determine the legacy Gmail preservation baseline."
}

$trackedChanges = @((Invoke-GmailGit -Arguments (@('diff', '--name-only', $mergeBase, '--') + $protectedPaths)) -split "`r?`n" | Where-Object { $_ })
$statusLines = @((Invoke-GmailGit -Arguments (@('status', '--porcelain', '--untracked-files=all', '--') + $protectedPaths)) -split "`r?`n" | Where-Object { $_ })
$statusChanges = @($statusLines | ForEach-Object {
    if ($_.Length -gt 3) { $_.Substring(3).Trim('"') }
})
$changes = @($trackedChanges + $statusChanges |
    Where-Object { $_ } |
    ForEach-Object { ([string]$_).Replace('\', '/') } |
    Sort-Object -Unique)
if ($changes.Count -gt 0) {
    if (-not $ConfirmApprovedLegacyLocalSurfaceChanges) {
        throw "Closeout stopped because legacy Gmail-owned paths changed: $($changes -join ', '). Separate approval is required."
    }

    $unapprovedPaths = @($changes | Where-Object { -not $approvedLegacyLocalSurfaceIdentities.ContainsKey($_) })
    if ($unapprovedPaths.Count -gt 0) {
        throw "Closeout stopped because non-approved Gmail-owned paths changed: $($unapprovedPaths -join ', ')."
    }

    $identityMismatches = @($changes | Where-Object {
        (Get-WorktreeBlobIdentity -RelativePath $_) -cne [string]$approvedLegacyLocalSurfaceIdentities[$_]
    })
    if ($identityMismatches.Count -gt 0) {
        throw "Closeout stopped because approved local-surface file identities changed: $($identityMismatches -join ', ')."
    }

    Write-Output "Approved Phase 5 local-surface identities verified: $($changes.Count)."
}

Write-Output "Legacy Gmail preservation diff guard passed."
} finally {
    if ($null -ne $resumeBoundaryObject) { Remove-ResumeGitBoundary -Boundary $resumeBoundaryObject }
}
