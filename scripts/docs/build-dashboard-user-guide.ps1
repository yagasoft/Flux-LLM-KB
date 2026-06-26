[CmdletBinding()]
param(
    [switch]$SkipScreens,
    [switch]$Render,
    [string]$Python = "",
    [string]$Renderer = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docxPath = Join-Path $repoRoot "docs\user-guide\Flux-LLM-KB-Dashboard-User-Manual.docx"
$renderDir = Join-Path $repoRoot "docs\user-guide\rendered-pages"
$bundledPython = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
if ([string]::IsNullOrWhiteSpace($Python)) {
    if (Test-Path -LiteralPath $bundledPython) {
        $Python = $bundledPython
    } elseif (-not [string]::IsNullOrWhiteSpace($env:PYTHON)) {
        $Python = $env:PYTHON
    } else {
        $Python = "python"
    }
}

Push-Location $repoRoot
try {
    if ($SkipScreens) {
        & $Python "scripts\docs\build_dashboard_user_guide.py" --build-docx
    } else {
        & (Join-Path $PSScriptRoot "capture-dashboard-user-guide-screens.ps1")
        if ($LASTEXITCODE -ne 0) {
            throw "Screenshot generation failed with exit code $LASTEXITCODE"
        }
        & $Python "scripts\docs\build_dashboard_user_guide.py" --build-docx
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Manual build failed with exit code $LASTEXITCODE"
    }

    if ($Render) {
        if ([string]::IsNullOrWhiteSpace($Renderer)) {
            $Renderer = Join-Path $env:USERPROFILE ".codex\plugins\cache\openai-primary-runtime\documents\26.623.12021\skills\documents\render_docx.py"
        }
        if (-not (Test-Path -LiteralPath $Renderer)) {
            throw "DOCX renderer not found: $Renderer"
        }
        New-Item -ItemType Directory -Force -Path $renderDir | Out-Null
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $primaryOutput = & $Python $Renderer $docxPath --output_dir $renderDir --width 1200 --height 1600 2>&1
            $primaryExit = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($primaryExit -ne 0) {
            Write-Host "Primary DOCX renderer unavailable or failed with exit code $primaryExit; trying Word/Poppler fallback."
            & "python" "scripts\docs\render_docx_with_word.py" $docxPath --output_dir $renderDir
            if ($LASTEXITCODE -ne 0) {
                throw "Manual render failed with exit code $LASTEXITCODE"
            }
        } else {
            $primaryOutput
        }
    }
} finally {
    Pop-Location
}
