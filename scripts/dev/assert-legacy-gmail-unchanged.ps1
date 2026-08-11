[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Get-Location).Path,
    [string]$BaselineRef = "main"
)

$ErrorActionPreference = "Stop"
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
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

$mergeBase = (& git -C $RepositoryRoot merge-base $BaselineRef HEAD 2>$null | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($mergeBase)) {
    throw "Unable to determine the legacy Gmail preservation baseline."
}

$trackedChanges = @(& git -C $RepositoryRoot diff --name-only $mergeBase -- $protectedPaths)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect legacy Gmail-owned tracked paths."
}
$statusLines = @(& git -C $RepositoryRoot status --porcelain --untracked-files=all -- $protectedPaths)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect legacy Gmail-owned worktree paths."
}
$statusChanges = @($statusLines | ForEach-Object {
    if ($_.Length -gt 3) { $_.Substring(3).Trim('"') }
})
$changes = @($trackedChanges + $statusChanges | Where-Object { $_ } | Sort-Object -Unique)
if ($changes.Count -gt 0) {
    throw "Closeout stopped because legacy Gmail-owned paths changed: $($changes -join ', '). Separate approval is required."
}

Write-Output "Legacy Gmail preservation diff guard passed."
