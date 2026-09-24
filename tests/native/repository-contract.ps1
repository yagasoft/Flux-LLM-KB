[CmdletBinding()]
param([string]$SourceRoot = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$paths = @(& git -C $SourceRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate repository files.' }
$paths = @($paths | Sort-Object -Unique | Where-Object {
    Test-Path -LiteralPath (Join-Path $SourceRoot $_) -PathType Leaf
})
$allowedRoots = @('.config', '.github', 'docs', 'probes', 'scripts', 'src', 'tests',
    '.gitattributes', '.gitignore', 'AGENTS.md', 'Directory.Build.props',
    'Directory.Packages.props', 'FluxKnowledge.slnx', 'global.json', 'LICENSE', 'README.md')
foreach ($path in $paths) {
    if (($path -split '/')[0] -notin $allowedRoots) {
        throw "Unexpected top-level repository entry: $path"
    }
    if ($path.StartsWith('src/') -and $path -notmatch '^src/FluxKnowledge\.[^/]+/') {
        throw "Source is outside the native solution: $path"
    }
    if ($path.StartsWith('tests/') -and $path -notmatch '^tests/(FluxKnowledge\.[^/]+|native)/') {
        throw "Test is outside the native verification suite: $path"
    }
}

# Check maintained documentation as a graph, including relative links to code.
$documents = @($paths | Where-Object { $_ -match '\.md$' })
$obsoleteTerms = '(?i)\blegacy\b|flux[-_]llm[-_]kb|flux-kb|\b(Docker|Vespa|RabbitMQ|FastAPI|PostgreSQL|pgvector|Ollama|Snowflake|Qwen)\b|\bkb\.(search|brief|remember|finalize_turn)\b'
foreach ($path in $documents) {
    $file = Join-Path $SourceRoot $path
    $content = Get-Content -LiteralPath $file -Raw
    # The current, explicitly requested model evaluation names Qwen embedding
    # candidates; this is not a reference to the removed application stack.
    $documentObsoleteTerms = if ($path -eq 'docs/design/semantic-model-candidates.md') {
        $obsoleteTerms.Replace('|Qwen', '')
    } else { $obsoleteTerms }
    if ($content -match $documentObsoleteTerms) {
        throw "Documentation contains a superseded architecture reference: $path"
    }
    if ($content -match '(?i)(?<![A-Za-z0-9])(?:I|J):(?![\\/])\w|\(localdb\)MSSQLLocalDB') {
        throw "Documentation contains a drive-relative runtime path: $path"
    }
    foreach ($link in [regex]::Matches($content, '\]\((?<target>[^\s)]+)(?:\s+"[^"]*")?\)')) {
        $target = $link.Groups['target'].Value.Trim('<', '>')
        if ($target -match '^(?:[a-z][a-z0-9+.-]*:|#)') { continue }
        $target = [Uri]::UnescapeDataString(($target -split '#', 2)[0])
        if (-not $target) { continue }
        $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $file) $target))
        if (-not (Test-Path -LiteralPath $resolved)) {
            throw "Broken documentation link in ${path}: $target"
        }
    }
}
Write-Output "Native repository contract passed: $($paths.Count) files, $($documents.Count) Markdown documents."
