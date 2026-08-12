Set-StrictMode -Version Latest

function ConvertTo-ResumeGitArgumentString {
    param([Parameter(Mandatory)] [string[]]$Arguments)

    $encoded = New-Object System.Collections.Generic.List[string]
    foreach ($argument in $Arguments) {
        if ($null -eq $argument) {
            throw "Resume Git command arguments must not be null."
        }
        if ($argument.Length -gt 0 -and $argument -notmatch '[\s"]') {
            [void]$encoded.Add($argument)
            continue
        }

        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append('"')
        $backslashCount = 0
        foreach ($character in $argument.ToCharArray()) {
            if ($character -eq '\') {
                $backslashCount++
                continue
            }
            if ($character -eq '"') {
                [void]$builder.Append(('\' * (($backslashCount * 2) + 1)))
                [void]$builder.Append('"')
                $backslashCount = 0
                continue
            }
            if ($backslashCount -gt 0) {
                [void]$builder.Append(('\' * $backslashCount))
                $backslashCount = 0
            }
            [void]$builder.Append($character)
        }
        if ($backslashCount -gt 0) {
            [void]$builder.Append(('\' * ($backslashCount * 2)))
        }
        [void]$builder.Append('"')
        [void]$encoded.Add($builder.ToString())
    }

    return [string]::Join(' ', $encoded)
}

function Get-ResumeGitExecutablePath {
    $command = Get-Command -Name 'git.exe' -CommandType Application -ErrorAction Stop | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($command.Path) -or -not (Test-Path -LiteralPath $command.Path -PathType Leaf)) {
        throw "Unable to resolve a full git.exe application path."
    }
    return (Resolve-Path -LiteralPath $command.Path -ErrorAction Stop).Path
}

function New-ResumeGitPrivateConfiguration {
    $directory = Join-Path ([System.IO.Path]::GetTempPath()) ("FluxKnowledge-ResumeGit-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
    $systemPath = Join-Path $directory 'system.gitconfig'
    $globalPath = Join-Path $directory 'global.gitconfig'
    $hooksPath = Join-Path $directory 'hooks'
    New-Item -ItemType Directory -Path $hooksPath -ErrorAction Stop | Out-Null
    [System.IO.File]::WriteAllText($systemPath, '', (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($globalPath, '', (New-Object System.Text.UTF8Encoding($false)))
    return [pscustomobject]@{ Directory = $directory; SystemPath = $systemPath; GlobalPath = $globalPath; HooksPath = $hooksPath }
}

function Remove-ResumeGitPrivateConfiguration {
    param($Configuration)

    if ($null -ne $Configuration -and (Test-Path -LiteralPath $Configuration.Directory)) {
        Remove-Item -LiteralPath $Configuration.Directory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function New-ResumeGitEnvironment {
    param(
        [Parameter(Mandatory)] $Configuration,
        [hashtable]$PinnedPaths = @{}
    )

    $environment = @{}
    foreach ($entry in Get-ChildItem Env:) {
        if ($entry.Name -notmatch '^GIT_') {
            $environment[$entry.Name] = [string]$entry.Value
        }
    }
    $environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $environment['GIT_CONFIG_SYSTEM'] = $Configuration.SystemPath
    $environment['GIT_CONFIG_GLOBAL'] = $Configuration.GlobalPath
    $environment['GIT_CONFIG_COUNT'] = '5'
    $environment['GIT_CONFIG_KEY_0'] = 'core.fsmonitor'
    $environment['GIT_CONFIG_VALUE_0'] = 'false'
    $environment['GIT_CONFIG_KEY_1'] = 'core.useReplaceRefs'
    $environment['GIT_CONFIG_VALUE_1'] = 'false'
    $environment['GIT_CONFIG_KEY_2'] = 'fetch.ifMissing'
    $environment['GIT_CONFIG_VALUE_2'] = 'false'
    $environment['GIT_CONFIG_KEY_3'] = 'maintenance.auto'
    $environment['GIT_CONFIG_VALUE_3'] = 'false'
    $environment['GIT_CONFIG_KEY_4'] = 'core.hooksPath'
    $environment['GIT_CONFIG_VALUE_4'] = $Configuration.HooksPath
    $environment['GIT_NO_REPLACE_OBJECTS'] = '1'
    $environment['GIT_NO_LAZY_FETCH'] = '1'
    $environment['GIT_OPTIONAL_LOCKS'] = '0'
    foreach ($key in $PinnedPaths.Keys) {
        $environment[$key] = [string]$PinnedPaths[$key]
    }
    return $environment
}

function Get-ResumeGitBranchConfigurationPart {
    param(
        [Parameter(Mandatory)] [string]$Key,
        [Parameter(Mandatory)] [string[]]$KnownLocalBranches
    )

    foreach ($branchName in @($KnownLocalBranches | Sort-Object { $_.Length } -Descending)) {
        $prefix = 'branch.' + $branchName + '.'
        if ($Key.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Branch = $branchName; Property = $Key.Substring($prefix.Length).ToLowerInvariant() }
        }
    }
    return $null
}

function Test-ResumeGitAllowedConfigurationKey {
    param(
        [Parameter(Mandatory)] [string]$Key,
        [Parameter(Mandatory)] [string]$ExpectedBranch,
        [Parameter(Mandatory)] [string[]]$KnownLocalBranches,
        [Parameter(Mandatory)] [string[]]$AllowedCoreKeys
    )

    if ($Key -in $AllowedCoreKeys -or $Key -in @('remote.origin.url', 'remote.origin.fetch')) {
        return $true
    }
    $branchPart = Get-ResumeGitBranchConfigurationPart -Key $Key -KnownLocalBranches $KnownLocalBranches
    if ($null -eq $branchPart) {
        return $false
    }
    if ($branchPart.Property -ceq 'vscode-merge-base') {
        return $true
    }
    return $branchPart.Property -in @('remote', 'merge')
}

function Invoke-ResumeGitProcess {
    param(
        [Parameter(Mandatory)] [string]$GitExecutable,
        [Parameter(Mandatory)] [string]$WorkingDirectory,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [hashtable]$Environment,
        [string]$StandardInput = '',
        $OperationBoundary = $null,
        $OperationPeerBoundary = $null
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $GitExecutable
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.Arguments = ConvertTo-ResumeGitArgumentString -Arguments $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $startInfo.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
    foreach ($name in @($startInfo.EnvironmentVariables.Keys)) {
        if ($name -match '^GIT_') {
            [void]$startInfo.EnvironmentVariables.Remove($name)
        }
    }
    foreach ($name in $Environment.Keys) {
        $startInfo.EnvironmentVariables[$name] = [string]$Environment[$name]
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if ($null -ne $OperationBoundary) {
        Assert-ResumeGitNoInProgressOperation $OperationBoundary
        if ($null -ne $OperationPeerBoundary) {
            Assert-ResumeGitNoInProgressOperation $OperationPeerBoundary
        }
    }
    if (-not $process.Start()) {
        throw "Unable to start authenticated git.exe."
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        if ($StandardInput) {
            $process.StandardInput.Write($StandardInput)
        }
    }
    finally {
        $process.StandardInput.Close()
    }
    $process.WaitForExit()
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $exitCode = $process.ExitCode
    $process.Dispose()
    return [pscustomobject]@{ ExitCode = $exitCode; StdOut = $stdout; StdErr = $stderr }
}

function Get-ResumeGitCanonicalPath {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$BasePath,
        [switch]$RequireFile,
        [switch]$AllowMissing
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Git returned an empty repository identity path."
    }
    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $BasePath $Path }
    if (-not $AllowMissing -and -not (Test-Path -LiteralPath $candidate -PathType $(if ($RequireFile) { 'Leaf' } else { 'Any' }))) {
        throw "Git returned a missing repository identity path."
    }
    if ($AllowMissing) {
        return [System.IO.Path]::GetFullPath($candidate)
    }
    return (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path
}

function Get-ResumeGitBootstrapValue {
    param(
        [Parameter(Mandatory)] [string]$GitExecutable,
        [Parameter(Mandatory)] [string]$Worktree,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [hashtable]$Environment
    )

    $result = Invoke-ResumeGitProcess -GitExecutable $GitExecutable -WorkingDirectory $Worktree -Arguments $Arguments -Environment $Environment
    if ($result.ExitCode -ne 0) {
        throw "Authenticated Git bootstrap failed while running $($Arguments[0])."
    }
    return $result.StdOut.Trim()
}

function Assert-ResumeGitConfiguration {
    param(
        [Parameter(Mandatory)] [string]$GitExecutable,
        [Parameter(Mandatory)] [string]$Worktree,
        [Parameter(Mandatory)] [hashtable]$Environment,
        [Parameter(Mandatory)] [string]$CommonDirectory,
        [Parameter(Mandatory)] [string]$GitDirectory,
        [Parameter(Mandatory)] [string]$ExpectedOriginUrl,
        [Parameter(Mandatory)] [string]$ExpectedBranch,
        [Parameter(Mandatory)] [string[]]$KnownLocalBranches
    )

    $configurationPath = Join-Path $CommonDirectory 'config'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw "The authenticated Git common configuration file is missing."
    }
    if (Test-Path -LiteralPath (Join-Path $GitDirectory 'config.worktree') -PathType Leaf) {
        throw "Per-worktree Git configuration is not permitted for staged-squash resume."
    }

    $lock = New-Object System.IO.FileStream($configurationPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $allowedCoreKeys = @(
            'core.repositoryformatversion', 'core.filemode', 'core.bare', 'core.logallrefupdates',
            'core.symlinks', 'core.ignorecase', 'core.precomposeunicode', 'core.protectntfs')
        $physicalSection = ''
        foreach ($line in [System.IO.File]::ReadAllLines($configurationPath, [System.Text.Encoding]::UTF8)) {
            $trimmedLine = $line.Trim()
            if (-not $trimmedLine -or $trimmedLine.StartsWith('#') -or $trimmedLine.StartsWith(';')) { continue }
            if ($trimmedLine -match '^\[([A-Za-z0-9.-]+)(?:\s+"([^"]+)")?\]$') {
                $physicalSection = $matches[1].ToLowerInvariant()
                if ($matches[2]) { $physicalSection += '.' + $matches[2].ToLowerInvariant() }
                continue
            }
            if (-not $physicalSection -or $trimmedLine -notmatch '^([A-Za-z][A-Za-z0-9-]*)\s*(?:=|$)') {
                throw "The authenticated Git configuration contains an unparseable entry."
            }
            $physicalKey = ($physicalSection + '.' + $matches[1]).ToLowerInvariant()
            if (Test-ResumeGitAllowedConfigurationKey -Key $physicalKey -ExpectedBranch $ExpectedBranch -KnownLocalBranches $KnownLocalBranches -AllowedCoreKeys $allowedCoreKeys) { continue }
            throw "The authenticated Git configuration contains a non-allowlisted key: $physicalKey"
        }
        $result = Invoke-ResumeGitProcess -GitExecutable $GitExecutable -WorkingDirectory $Worktree -Arguments @('config', '--local', '--null', '--includes', '--list') -Environment $Environment
        if ($result.ExitCode -ne 0) {
            throw "Unable to read the locked authenticated Git configuration."
        }
        $originUrlSeen = $false
        foreach ($record in @($result.StdOut -split "`0")) {
            if (-not $record) { continue }
            $pair = $record -split "`n", 2
            if ($pair.Count -ne 2) {
                throw "The authenticated Git configuration has an invalid record."
            }
            $key = $pair[0].ToLowerInvariant()
            $value = $pair[1]
            if ($key -in $allowedCoreKeys) { continue }
            if ($key -ceq 'remote.origin.url' -and $value -ceq $ExpectedOriginUrl) {
                $originUrlSeen = $true
                continue
            }
            if ($key -ceq 'remote.origin.fetch' -and $value -ceq '+refs/heads/*:refs/remotes/origin/*') { continue }
            $branchPart = Get-ResumeGitBranchConfigurationPart -Key $key -KnownLocalBranches $KnownLocalBranches
            if ($null -ne $branchPart -and (Test-ResumeGitAllowedConfigurationKey -Key $key -ExpectedBranch $ExpectedBranch -KnownLocalBranches $KnownLocalBranches -AllowedCoreKeys $allowedCoreKeys)) {
                if ($branchPart.Property -ceq 'vscode-merge-base' -and $value -ceq 'origin/main') { continue }
                if ($branchPart.Property -ceq 'remote' -and $value -ceq 'origin') { continue }
                if ($branchPart.Property -ceq 'merge' -and $value -ceq ('refs/heads/' + $branchPart.Branch)) { continue }
            }
            throw "The authenticated Git configuration contains a non-allowlisted key: $key"
        }
        if (-not $originUrlSeen) {
            throw "The authenticated Git origin URL does not match the expected origin URL."
        }
    }
    catch {
        $lock.Dispose()
        throw
    }
    return $lock
}

function Assert-ResumeGitArgumentSafety {
    param([Parameter(Mandatory)] [string[]]$Arguments)

    foreach ($argument in $Arguments) {
        if ($argument -match '^(?:-C|--git-dir|--work-tree|--namespace|--shallow-file|--config-env|-c|--no-replace-objects)(?:=|$)') {
            throw "Resume Git command arguments must not override authenticated repository state."
        }
    }
}

function New-ResumeGitBoundary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Worktree,
        [Parameter(Mandatory)] [string]$ExpectedOriginUrl,
        [Parameter(Mandatory)] [string]$ExpectedHead,
        [Parameter(Mandatory)] [string]$ExpectedBranch
    )

    if ($ExpectedHead -cnotmatch '^[0-9a-f]{40}$') {
        throw "Expected Git head must be a canonical full SHA-1 value."
    }
    $resolvedWorktree = (Resolve-Path -LiteralPath $Worktree -ErrorAction Stop).Path
    $configuration = New-ResumeGitPrivateConfiguration
    try {
        $gitExecutable = Get-ResumeGitExecutablePath
        $bootstrapEnvironment = New-ResumeGitEnvironment -Configuration $configuration
        $topLevel = Get-ResumeGitCanonicalPath -Path (Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $resolvedWorktree -Arguments @('rev-parse', '--show-toplevel') -Environment $bootstrapEnvironment) -BasePath $resolvedWorktree
        if (-not $topLevel.Equals($resolvedWorktree, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Git top-level identity does not match the requested worktree."
        }
        $gitDirectory = Get-ResumeGitCanonicalPath -Path (Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $resolvedWorktree -Arguments @('rev-parse', '--absolute-git-dir') -Environment $bootstrapEnvironment) -BasePath $topLevel
        $commonDirectory = Get-ResumeGitCanonicalPath -Path (Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $resolvedWorktree -Arguments @('rev-parse', '--git-common-dir') -Environment $bootstrapEnvironment) -BasePath $topLevel
        # `rev-parse --git-path index` may be relative to the authenticated
        # worktree (for example `.git/index`), not the git directory.
        $indexPath = Get-ResumeGitCanonicalPath -Path (Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $resolvedWorktree -Arguments @('rev-parse', '--git-path', 'index') -Environment $bootstrapEnvironment) -BasePath $topLevel -AllowMissing
        $objectDirectory = Get-ResumeGitCanonicalPath -Path (Join-Path $commonDirectory 'objects') -BasePath $commonDirectory
        foreach ($alternateMetadataName in @('info\\alternates', 'info\\http-alternates')) {
            if (Test-Path -LiteralPath (Join-Path $objectDirectory $alternateMetadataName) -PathType Leaf) {
                throw "Persistent Git alternate object metadata is not permitted for staged-squash resume."
            }
        }
        if ((Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $resolvedWorktree -Arguments @('rev-parse', '--is-bare-repository') -Environment $bootstrapEnvironment) -cne 'false' -or
            (Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $resolvedWorktree -Arguments @('rev-parse', '--is-shallow-repository') -Environment $bootstrapEnvironment) -cne 'false' -or
            (Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $resolvedWorktree -Arguments @('rev-parse', '--show-object-format=storage') -Environment $bootstrapEnvironment) -cne 'sha1') {
            throw "The authenticated repository must be non-bare, non-shallow and SHA-1."
        }
        $pinnedEnvironment = New-ResumeGitEnvironment -Configuration $configuration -PinnedPaths @{
            GIT_DIR = $gitDirectory; GIT_WORK_TREE = $topLevel; GIT_INDEX_FILE = $indexPath; GIT_COMMON_DIR = $commonDirectory; GIT_OBJECT_DIRECTORY = $objectDirectory
        }
        $knownWorktreeBranches = @((Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $topLevel -Arguments @('worktree', 'list', '--porcelain') -Environment $pinnedEnvironment) -split "`r?`n" |
            Where-Object { $_.StartsWith('branch refs/heads/', [System.StringComparison]::Ordinal) } |
            ForEach-Object { $_.Substring('branch refs/heads/'.Length) })
        $configurationLock = Assert-ResumeGitConfiguration -GitExecutable $gitExecutable -Worktree $topLevel -Environment $pinnedEnvironment -CommonDirectory $commonDirectory -GitDirectory $gitDirectory -ExpectedOriginUrl $ExpectedOriginUrl -ExpectedBranch $ExpectedBranch -KnownLocalBranches $knownWorktreeBranches
        try {
            if ((Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $topLevel -Arguments @('rev-parse', '--verify', 'HEAD^{commit}') -Environment $pinnedEnvironment) -cne $ExpectedHead -or
                (Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $topLevel -Arguments @('branch', '--show-current') -Environment $pinnedEnvironment) -cne $ExpectedBranch -or
                (Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $topLevel -Arguments @('rev-parse', '--verify', ('refs/heads/' + $ExpectedBranch)) -Environment $pinnedEnvironment) -cne $ExpectedHead) {
                throw "The authenticated repository head or branch does not match the expected identity."
            }
            foreach ($metadataDirectory in @($gitDirectory, $commonDirectory) | Select-Object -Unique) {
                if (Test-Path -LiteralPath (Join-Path $metadataDirectory 'info\grafts') -PathType Leaf) {
                    throw "Git graft metadata is not permitted for staged-squash resume."
                }
            }
            if (Get-ResumeGitBootstrapValue -GitExecutable $gitExecutable -Worktree $topLevel -Arguments @('for-each-ref', '--format=%(refname)', 'refs/replace') -Environment $pinnedEnvironment) {
                throw "Git replacement refs are not permitted for staged-squash resume."
            }
            return [pscustomobject]@{
                GitExecutable = $gitExecutable; Worktree = $topLevel; GitDirectory = $gitDirectory; CommonDirectory = $commonDirectory; IndexPath = $indexPath; ObjectDirectory = $objectDirectory
                Environment = $pinnedEnvironment; Configuration = $configuration; ConfigurationLock = $configurationLock
            }
        }
        catch {
            $configurationLock.Dispose()
            throw
        }
    }
    catch {
        Remove-ResumeGitPrivateConfiguration -Configuration $configuration
        throw
    }
}

function Invoke-ResumeGit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Boundary,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [string]$StandardInput = '',
        [hashtable]$Identity = @{},
        [switch]$RequireNoInProgressOperation,
        $PeerBoundary = $null
    )

    Assert-ResumeGitArgumentSafety -Arguments $Arguments
    $environment = @{}
    foreach ($key in $Boundary.Environment.Keys) { $environment[$key] = $Boundary.Environment[$key] }
    if ($Identity.Count -gt 0) {
        $requiredKeys = @('GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_AUTHOR_DATE', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL', 'GIT_COMMITTER_DATE')
        if (@($Identity.Keys | Where-Object { $_ -notin $requiredKeys }).Count -gt 0 -or @($requiredKeys | Where-Object { -not $Identity.ContainsKey($_) -or [string]::IsNullOrWhiteSpace([string]$Identity[$_]) }).Count -gt 0) {
            throw 'Resume Git commit identity must contain only complete explicit author and committer headers.'
        }
        foreach ($key in $requiredKeys) {
            if ([string]$Identity[$key] -match "[\r\n\0]") { throw 'Resume Git commit identity contains an unsafe control character.' }
            $environment[$key] = [string]$Identity[$key]
        }
    }
    $operationBoundary = if ($RequireNoInProgressOperation) { $Boundary } else { $null }
    $operationPeerBoundary = if ($RequireNoInProgressOperation) { $PeerBoundary } else { $null }
    return Invoke-ResumeGitProcess -GitExecutable $Boundary.GitExecutable -WorkingDirectory $Boundary.Worktree -Arguments $Arguments -Environment $environment -StandardInput $StandardInput -OperationBoundary $operationBoundary -OperationPeerBoundary $operationPeerBoundary
}

function Assert-ResumeGitNoInProgressOperation {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Boundary)

    foreach ($name in @('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-apply', 'rebase-merge', 'sequencer')) {
        if (Test-Path -LiteralPath (Join-Path $Boundary.GitDirectory $name)) {
            throw ('A Git operation marker is present in the authenticated worktree: ' + $name)
        }
    }
}

function Assert-ResumeGitPairNoInProgressOperation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $MainBoundary,
        [Parameter(Mandatory)] $FeatureBoundary
    )

    Assert-ResumeGitNoInProgressOperation $MainBoundary
    Assert-ResumeGitNoInProgressOperation $FeatureBoundary
}

function Remove-ResumeGitBoundary {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Boundary)

    if ($null -ne $Boundary.ConfigurationLock) {
        $Boundary.ConfigurationLock.Dispose()
    }
    Remove-ResumeGitPrivateConfiguration -Configuration $Boundary.Configuration
}

Export-ModuleMember -Function New-ResumeGitBoundary, Invoke-ResumeGit, Assert-ResumeGitNoInProgressOperation, Assert-ResumeGitPairNoInProgressOperation, Remove-ResumeGitBoundary
