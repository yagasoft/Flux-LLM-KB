[CmdletBinding()]
param(
    [string]$SourceRoot = "D:\FluxLLMKB",
    [string]$DestinationRoot = "J:\FluxLLMKB",
    [AllowEmptyString()]
    [string]$SourceCodeRoot = "__FLUX_SOURCE_CODE_ROOT_REQUIRED__",
    [string]$DockerDataRoot = "I:\Docker\data\wsl\DockerDesktopWSL",
    [string]$DockerSettingsPath = (Join-Path $env:APPDATA "Docker\settings-store.json"),
    [switch]$ResumePartialDestination,
    [switch]$Apply,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:FluxTaskNames = @(
    "FluxKB Host Agent",
    "FluxKB Outlook Host"
)

$script:FluxComposeServices = @(
    "postgres",
    "rabbitmq",
    "vespa",
    "paddle-runner",
    "model-runner",
    "ollama",
    "asr",
    "api",
    "worker",
    "search-index-worker",
    "mail-worker",
    "automation-worker",
    "governance-worker",
    "runtime-control-worker",
    "gpu-eviction-worker",
    "event-scheduler",
    "callback-worker",
    "event-audit-worker",
    "event-dashboard-worker",
    "event-diagnostics-worker",
    "outbox-relay"
)

function Assert-FluxAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "-Apply requires an elevated Administrator PowerShell session."
    }
}

function Invoke-FluxNativeCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory)]
        [string]$StepName
    )

    $exitCode = 1
    $nativeOutput = @()
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            # Docker BuildKit writes normal progress to stderr.  Capture native
            # stderr under Continue and replay it as output so an enclosing
            # `*>&1 | Tee-Object` pipeline cannot convert progress into a
            # terminating NativeCommandError.
            $ErrorActionPreference = "Continue"
            $nativeOutput = @(& $FilePath @Arguments 2>&1)
            $exitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    } finally {
        Pop-Location
    }

    foreach ($line in $nativeOutput) {
        Write-Output ([string]$line)
    }

    if ($exitCode -ne 0) {
        throw "$StepName failed with exit code $exitCode."
    }
}

function Assert-FluxDockerDataRoot {
    param(
        [Parameter(Mandatory)]
        [string]$DockerDataRoot,
        [Parameter(Mandatory)]
        [string]$DockerSettingsPath
    )

    if (-not (Test-Path -LiteralPath $DockerDataRoot -PathType Container)) {
        throw "Docker data root does not exist: $DockerDataRoot"
    }
    $dockerDataRootFull = (Resolve-Path -LiteralPath $DockerDataRoot).Path.TrimEnd("\")
    $supportedVhdxPath = $null
    foreach ($relativePath in @("disk\docker_data.vhdx", "disk\docker-desktop-data.vhdx", "ext4.vhdx")) {
        $candidate = Join-Path $dockerDataRootFull $relativePath
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $item = Get-Item -LiteralPath $candidate
            if ($item.Length -gt 0) {
                $supportedVhdxPath = $item.FullName
                break
            }
        }
    }
    if ($null -eq $supportedVhdxPath) {
        throw "Docker data root does not contain a supported VHDX: $dockerDataRootFull"
    }
    if (-not (Test-Path -LiteralPath $DockerSettingsPath -PathType Leaf)) {
        throw "Docker Desktop settings file does not exist: $DockerSettingsPath"
    }

    try {
        $settings = Get-Content -LiteralPath $DockerSettingsPath -Raw | ConvertFrom-Json
    } catch {
        throw "Docker Desktop settings file is not valid JSON: $DockerSettingsPath"
    }
    $activeRootProperty = $settings.PSObject.Properties["CustomWslDistroDir"]
    if ($null -eq $activeRootProperty -or [string]::IsNullOrWhiteSpace([string]$activeRootProperty.Value)) {
        throw "Docker Desktop settings file does not declare CustomWslDistroDir: $DockerSettingsPath"
    }
    $activeRoot = [string]$activeRootProperty.Value
    if (-not [System.IO.Path]::IsPathRooted($activeRoot)) {
        throw "Docker Desktop CustomWslDistroDir is not an absolute path: $activeRoot"
    }
    $activeRootFull = [System.IO.Path]::GetFullPath($activeRoot).TrimEnd("\")
    if (-not $activeRootFull.Equals($dockerDataRootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Docker Desktop CustomWslDistroDir does not match Docker data root. Active=$activeRootFull Expected=$dockerDataRootFull"
    }

    return [pscustomobject]@{
        DataRoot = $dockerDataRootFull
        VhdxPath = $supportedVhdxPath
        SettingsPath = (Resolve-Path -LiteralPath $DockerSettingsPath).Path
        SettingsDataRoot = $activeRootFull
    }
}

function Get-FluxComposeConfigurationServices {
    param(
        [Parameter(Mandatory)]
        [string]$ComposePath,
        [Parameter(Mandatory)]
        [string]$EnvPath,
        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw "Docker Compose is unavailable for source configuration validation."
    }
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $composeArguments = @(
            "compose",
            "--env-file", $EnvPath,
            "-f", $ComposePath,
            "config", "--services"
        )
        $output = & docker @composeArguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($exitCode -ne 0) {
        throw "Could not resolve source Compose services."
    }
    return @(
        $output |
            ForEach-Object { [string]$_ } |
            Where-Object { $_ -and $script:FluxComposeServices -contains $_ }
    )
}

function Get-FluxComposeState {
    param(
        [Parameter(Mandatory)]
        [string]$ComposePath,
        [Parameter(Mandatory)]
        [string]$EnvPath,
        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $state = [ordered]@{
        query_status = "unavailable"
        running_services = @()
        expected_services = $script:FluxComposeServices
        error = $null
    }
    if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
        $state.error = "docker command is unavailable"
        return [pscustomobject]$state
    }

    $exitCode = $null
    try {
        Push-Location -LiteralPath $WorkingDirectory
        try {
            $output = & docker compose --env-file $EnvPath -f $ComposePath ps --services --status running 2>&1
            $exitCode = $LASTEXITCODE
        } finally {
            Pop-Location
        }
    } catch {
        $state.error = "docker compose ps could not be queried"
        return [pscustomobject]$state
    }

    if ($exitCode -ne 0) {
        $state.error = "docker compose ps failed with exit code $exitCode"
        return [pscustomobject]$state
    }

    $state.running_services = @(
        $output |
            ForEach-Object { [string]$_ } |
            Where-Object { $_ -and $script:FluxComposeServices -contains $_ }
    )
    $state.query_status = if ($state.running_services.Count -eq 0) { "stopped" } else { "running" }
    return [pscustomobject]$state
}

function Assert-FluxMigrationPreflight {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot,
        [Parameter(Mandatory)]
        [string]$SourceCodeRoot,
        [Parameter(Mandatory)]
        [string]$DockerDataRoot,
        [Parameter(Mandatory)]
        [string]$DockerSettingsPath,
        [bool]$ResumePartialDestination
    )

    if ([string]::IsNullOrWhiteSpace($SourceCodeRoot) -or $SourceCodeRoot -eq "__FLUX_SOURCE_CODE_ROOT_REQUIRED__") {
        throw "Specify -SourceCodeRoot as a clean checkout at the deployed runtime revision."
    }
    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        throw "Source install root does not exist: $SourceRoot"
    }
    if (-not (Test-Path -LiteralPath $SourceCodeRoot -PathType Container)) {
        throw "Source code root does not exist: $SourceCodeRoot"
    }

    $sourceRootFull = (Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd("\")
    $sourceCodeRootFull = (Resolve-Path -LiteralPath $SourceCodeRoot).Path.TrimEnd("\")
    $destinationRootFull = [System.IO.Path]::GetFullPath($DestinationRoot).TrimEnd("\")
    $destinationParent = Split-Path -Parent $destinationRootFull

    if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
        throw "Destination parent does not exist: $destinationParent"
    }
    if ($sourceRootFull.Equals($destinationRootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SourceRoot and DestinationRoot must be different."
    }
    if ($destinationRootFull.StartsWith($sourceRootFull + "\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "DestinationRoot cannot be nested below SourceRoot."
    }
    $destinationHasEntries = $false
    if (Test-Path -LiteralPath $DestinationRoot) {
        $destinationHasEntries = @(Get-ChildItem -LiteralPath $DestinationRoot -Force).Count -gt 0
    }

    $sourceAppRoot = Join-Path $sourceRootFull "app"
    $sourceVersionPath = Join-Path $SourceRoot "app\VERSION"
    $sourceComposePath = Join-Path $sourceAppRoot "docker-compose.yml"
    $sourceEnvPath = Join-Path $sourceAppRoot ".env"
    foreach ($requiredPath in @($sourceAppRoot, $sourceVersionPath, $sourceComposePath, $sourceEnvPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "Required Flux runtime path is missing: $requiredPath"
        }
    }
    $dockerData = Assert-FluxDockerDataRoot -DockerDataRoot $DockerDataRoot -DockerSettingsPath $DockerSettingsPath
    $sourceComposeServices = Get-FluxComposeConfigurationServices -ComposePath $sourceComposePath -EnvPath $sourceEnvPath -WorkingDirectory $sourceAppRoot
    $missingComposeServices = @($script:FluxComposeServices | Where-Object { $sourceComposeServices -notcontains $_ })
    if ($missingComposeServices.Count -gt 0) {
        throw "Source Compose configuration is missing required services: $($missingComposeServices -join ', ')"
    }

    $runtimeRevision = (Get-Content -LiteralPath $sourceVersionPath -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($runtimeRevision)) {
        throw "Source runtime revision is empty: $sourceVersionPath"
    }
    if ($destinationHasEntries) {
        if (-not $ResumePartialDestination) {
            throw "DestinationRoot must be new or empty. Destination root must be empty unless -ResumePartialDestination is explicitly supplied: $DestinationRoot"
        }
        $partialVersionPath = Join-Path $destinationRootFull "app\VERSION"
        $partialComposePath = Join-Path $destinationRootFull "app\docker-compose.yml"
        if (-not (Test-Path -LiteralPath $partialVersionPath -PathType Leaf) -or -not (Test-Path -LiteralPath $partialComposePath -PathType Leaf)) {
            throw "Partial destination root cannot be resumed because it is not a recognised Flux runtime: $DestinationRoot"
        }
        $partialRevision = (Get-Content -LiteralPath $partialVersionPath -Raw).Trim()
        if ($partialRevision -ne $runtimeRevision) {
            throw "Partial destination root cannot be resumed because its revision does not match the source runtime. Destination=$partialRevision Source=$runtimeRevision"
        }
    }
    $workingTreeStatus = & git -C $SourceCodeRoot status --porcelain
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect source code checkout status: $SourceCodeRoot"
    }
    if (@($workingTreeStatus | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "Source code checkout must be clean: $SourceCodeRoot"
    }
    $codeRevisionOutput = & git -C $SourceCodeRoot rev-parse --short HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve the source code revision from: $SourceCodeRoot"
    }
    $codeRevision = ($codeRevisionOutput | Select-Object -First 1).Trim()
    if ($runtimeRevision -ne $codeRevision) {
        throw "Source runtime revision does not match source code revision. Runtime=$runtimeRevision Code=$codeRevision"
    }

    $taskStates = @(
        foreach ($taskName in $script:FluxTaskNames) {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            [pscustomobject]@{
                name = $taskName
                state = if ($null -eq $task) { "Absent" } else { [string]$task.State }
            }
        }
    )
    $composeState = Get-FluxComposeState -ComposePath $sourceComposePath -EnvPath $sourceEnvPath -WorkingDirectory $sourceAppRoot
    [pscustomobject]@{
        SourceRoot = $sourceRootFull
        DestinationRoot = $destinationRootFull
        SourceCodeRoot = $sourceCodeRootFull
        SourceAppRoot = $sourceAppRoot
        SourceComposePath = $sourceComposePath
        SourceEnvPath = $sourceEnvPath
        RuntimeRevision = $runtimeRevision
        CodeRevision = $codeRevision
        DockerDataRoot = $dockerData.DataRoot
        DockerVhdxPath = $dockerData.VhdxPath
        DockerSettingsPath = $dockerData.SettingsPath
        DockerSettingsDataRoot = $dockerData.SettingsDataRoot
        SourceComposeServices = $sourceComposeServices
        TaskStates = $taskStates
        ComposeState = $composeState
        ResumePartialDestination = ($destinationHasEntries -and $ResumePartialDestination)
    }
}

function New-FluxMigrationReport {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Preflight,
        [Parameter(Mandatory)]
        [bool]$ApplyRequested
    )

    $runningTasks = @($Preflight.TaskStates | Where-Object { $_.state -eq "Running" } | ForEach-Object { $_.name })
    $runtimeState = if ($Preflight.ComposeState.query_status -eq "unavailable") {
        "unknown"
    } elseif ($runningTasks.Count -gt 0 -or $Preflight.ComposeState.query_status -eq "running") {
        "running"
    } else {
        "stopped"
    }
    [ordered]@{
        mode = if ($ApplyRequested) { "apply" } else { "preflight" }
        status = "validated"
        source_root = $Preflight.SourceRoot
        destination_root = $Preflight.DestinationRoot
        source_code_root = $Preflight.SourceCodeRoot
        runtime_revision = $Preflight.RuntimeRevision
        code_revision = $Preflight.CodeRevision
        docker_data_root = $Preflight.DockerDataRoot
        docker_vhdx_path = $Preflight.DockerVhdxPath
        docker_settings_path = $Preflight.DockerSettingsPath
        docker_settings_data_root = $Preflight.DockerSettingsDataRoot
        source_compose_services = $Preflight.SourceComposeServices
        scheduled_tasks = $Preflight.TaskStates
        compose_state = $Preflight.ComposeState
        runtime_state = $runtimeState
        runtime_stopped = ($runtimeState -eq "stopped")
        source_retained = $true
        resume_partial_destination = $Preflight.ResumePartialDestination
        mutations_performed = $false
        planned_steps = @(
            "stop Flux scheduled tasks",
            "stop residual source-root Flux processes",
            "stop the source Compose project",
            "copy the source root with robocopy",
            "repair root-bearing text configuration and launchers",
            "materialize required cached Windows wheels into the copied local wheelhouse",
            "rebuild the target Python virtual environment from the local wheelhouse",
            "build the local Docker pip wheelhouse image from the copied local cache",
            "start and verify all target Compose services without building",
            "replace Flux-only PATH entries for User and Machine",
            "replace Codex MCP runtime references",
            "re-register Flux scheduled tasks with J-drive actions",
            "retain the source root and recovery snapshot"
        )
        warnings = @(
            if ($runningTasks.Count -gt 0) {
                "Running Flux tasks will be stopped only after -Apply and the administrator check."
            }
            if ($Preflight.ComposeState.query_status -eq "unavailable") {
                "Docker Compose state could not be queried during preflight."
            }
        )
    }
}

function Write-FluxMigrationReport {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [switch]$Json
    )

    if ($Json) {
        $Report | ConvertTo-Json -Depth 8
        return
    }

    $Report | Format-List | Out-String | Write-Output
}

function New-FluxMigrationSnapshot {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Preflight
    )

    $recoveryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("flux-install-root-recovery-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null
    $taskSnapshots = @(
        foreach ($taskName in $script:FluxTaskNames) {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            $xmlPath = $null
            if ($null -ne $task) {
                $xmlPath = Join-Path $recoveryRoot (($taskName -replace "[^A-Za-z0-9_.-]", "_") + ".xml")
                $taskXml = Export-ScheduledTask -TaskName $taskName | Out-String
                [System.IO.File]::WriteAllText($xmlPath, $taskXml, [System.Text.UTF8Encoding]::new($false))
            }
            [pscustomobject]@{
                Name = $taskName
                Exists = ($null -ne $task)
                WasRunning = ($null -ne $task -and $task.State -eq "Running")
                TriggerCount = if ($null -eq $task) { 0 } else { @($task.Triggers).Count }
                XmlPath = $xmlPath
            }
        }
    )

    $codexConfigPath = Join-Path $env:USERPROFILE ".codex\config.toml"
    $codexConfigExists = Test-Path -LiteralPath $codexConfigPath -PathType Leaf

    [pscustomobject]@{
        RecoveryRoot = $recoveryRoot
        TaskSnapshots = $taskSnapshots
        PathValues = [ordered]@{
            User = [Environment]::GetEnvironmentVariable("Path", "User")
            Machine = [Environment]::GetEnvironmentVariable("Path", "Machine")
        }
        CodexConfigSnapshot = [pscustomobject]@{
            Path = $codexConfigPath
            Exists = $codexConfigExists
            Bytes = if ($codexConfigExists) { [System.IO.File]::ReadAllBytes($codexConfigPath) } else { $null }
        }
        SourceRunningServices = @($Preflight.ComposeState.running_services)
    }
}

function Stop-FluxScheduledTasks {
    foreach ($taskName in $script:FluxTaskNames) {
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($null -eq $task) {
            continue
        }

        if ($task.State -eq "Running") {
            Stop-ScheduledTask -TaskName $taskName -ErrorAction Stop
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            do {
                Start-Sleep -Milliseconds 500
                $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            } while ($null -ne $task -and $task.State -eq "Running" -and [DateTime]::UtcNow -lt $deadline)

            if ($null -ne $task -and $task.State -eq "Running") {
                throw "Scheduled task did not stop before migration: $taskName"
            }
        }

        Disable-ScheduledTask -TaskName $taskName -ErrorAction Stop | Out-Null
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
        if ($task.State -ne "Disabled") {
            throw "Scheduled task did not disable before migration: $taskName"
        }
    }
}

function Stop-FluxSourceRootProcesses {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot
    )

    $sourceRootFull = [System.IO.Path]::GetFullPath($SourceRoot).TrimEnd("\")
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $processes = @(
            Get-CimInstance -ClassName Win32_Process -ErrorAction Stop |
                Where-Object {
                    $_.ProcessId -ne $PID -and
                    -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
                    $_.CommandLine.IndexOf($sourceRootFull, [StringComparison]::OrdinalIgnoreCase) -ge 0
                }
        )
        if ($processes.Count -eq 0) {
            return
        }
        foreach ($process in @($processes | Sort-Object -Property ProcessId -Descending)) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    $remainingProcesses = @(
        Get-CimInstance -ClassName Win32_Process -ErrorAction Stop |
            Where-Object {
                $_.ProcessId -ne $PID -and
                -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
                $_.CommandLine.IndexOf($sourceRootFull, [StringComparison]::OrdinalIgnoreCase) -ge 0
            }
    )
    if ($remainingProcesses.Count -gt 0) {
        throw "Flux processes still reference source root after scheduled-task shutdown: $SourceRoot"
    }
}

function Start-FluxComposeServices {
    param(
        [Parameter(Mandatory)]
        [string]$ComposePath,
        [Parameter(Mandatory)]
        [string]$EnvPath,
        [Parameter(Mandatory)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory)]
        [string[]]$Services,
        [Parameter(Mandatory)]
        [string]$StepName
    )

    if ($Services.Count -eq 0) {
        return
    }
    $composeArguments = @(
        "compose",
        "--env-file", $EnvPath,
        "-f", $ComposePath,
        "up", "-d", "--no-build"
    ) + $Services
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments $composeArguments -WorkingDirectory $WorkingDirectory -StepName $StepName
}

function Restore-FluxMigrationSnapshot {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Snapshot,
        [Parameter(Mandatory)]
        [pscustomobject]$Preflight,
        [bool]$TargetComposeStarted
    )

    $rollback = [ordered]@{
        attempted = $true
        status = "succeeded"
        target_compose_stopped = $false
        source_compose_restored = $false
        paths_restored = $false
        codex_config_restored = $false
        tasks_restored = $false
        errors = @()
    }
    if ($TargetComposeStarted) {
        try {
            $targetAppRoot = Join-Path $Preflight.DestinationRoot "app"
            Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
                "compose",
                "--env-file", (Join-Path $targetAppRoot ".env"),
                "-f", (Join-Path $targetAppRoot "docker-compose.yml"),
                "down"
            ) -WorkingDirectory $targetAppRoot -StepName "stop incomplete target Compose project"
            $rollback.target_compose_stopped = $true
        } catch {
            $rollback.errors += "target Compose rollback: $($_.Exception.Message)"
        }
    }
    try {
        [Environment]::SetEnvironmentVariable("Path", $Snapshot.PathValues.User, "User")
        [Environment]::SetEnvironmentVariable("Path", $Snapshot.PathValues.Machine, "Machine")
        $rollback.paths_restored = $true
    } catch {
        $rollback.errors += "PATH rollback: $($_.Exception.Message)"
    }
    try {
        if ($Snapshot.CodexConfigSnapshot.Exists) {
            [System.IO.File]::WriteAllBytes($Snapshot.CodexConfigSnapshot.Path, $Snapshot.CodexConfigSnapshot.Bytes)
        }
        $rollback.codex_config_restored = $true
    } catch {
        $rollback.errors += "Codex MCP config rollback: $($_.Exception.Message)"
    }
    try {
        if ($Snapshot.SourceRunningServices.Count -gt 0) {
            Start-FluxComposeServices -ComposePath $Preflight.SourceComposePath -EnvPath $Preflight.SourceEnvPath -WorkingDirectory $Preflight.SourceAppRoot -Services $Snapshot.SourceRunningServices -StepName "restore source Compose project"
        }
        $rollback.source_compose_restored = $true
    } catch {
        $rollback.errors += "source Compose rollback: $($_.Exception.Message)"
    }
    try {
        foreach ($taskSnapshot in $Snapshot.TaskSnapshots) {
            $existingTask = Get-ScheduledTask -TaskName $taskSnapshot.Name -ErrorAction SilentlyContinue
            if ($null -ne $existingTask) {
                Unregister-ScheduledTask -TaskName $taskSnapshot.Name -Confirm:$false
            }
            if ($taskSnapshot.Exists) {
                $taskXml = [System.IO.File]::ReadAllText($taskSnapshot.XmlPath)
                Register-ScheduledTask -TaskName $taskSnapshot.Name -Xml $taskXml -Force | Out-Null
                if ($taskSnapshot.WasRunning) {
                    Start-ScheduledTask -TaskName $taskSnapshot.Name
                }
            }
        }
        $rollback.tasks_restored = $true
    } catch {
        $rollback.errors += "scheduled-task rollback: $($_.Exception.Message)"
    }
    if ($rollback.errors.Count -gt 0) {
        $rollback.status = "partial"
    }
    return [pscustomobject]$rollback
}

function Copy-FluxInstallRoot {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    if (-not (Test-Path -LiteralPath $DestinationRoot)) {
        New-Item -ItemType Directory -Path $DestinationRoot | Out-Null
    }

    $robocopyOutput = @(& robocopy $SourceRoot $DestinationRoot /E /COPY:DAT /DCOPY:DAT /ZB /R:2 /W:2 /XJ /NFL /NDL /NP 2>&1)
    $robocopyExitCode = $LASTEXITCODE
    if ($robocopyExitCode -gt 7) {
        $diagnostics = @(
            $robocopyOutput |
                ForEach-Object { [string]$_ } |
                Where-Object { $_ -match '(?i)error|failed|denied|retry' } |
                Select-Object -Last 12
        )
        $diagnosticText = if ($diagnostics.Count -gt 0) { $diagnostics -join ' | ' } else { 'no file-level diagnostics were emitted' }
        throw "robocopy failed with exit code $robocopyExitCode; robocopy diagnostics: $diagnosticText"
    }
}

function Replace-FluxRootText {
    param(
        [Parameter(Mandatory)]
        [string]$Text,
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $plainRoot = [regex]::new([regex]::Escape($SourceRoot), [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $escapedRoot = [regex]::new([regex]::Escape($SourceRoot.Replace("\", "\\")), [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $updatedText = $plainRoot.Replace($Text, $DestinationRoot)
    return $escapedRoot.Replace($updatedText, $DestinationRoot.Replace("\", "\\"))
}

function Update-FluxRootReferenceFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $reader = [System.IO.StreamReader]::new($Path, $true)
    try {
        $content = $reader.ReadToEnd()
        $encoding = $reader.CurrentEncoding
    } finally {
        $reader.Dispose()
    }
    $updatedContent = Replace-FluxRootText -Text $content -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot
    if ($updatedContent -eq $content) {
        return
    }
    $writer = [System.IO.StreamWriter]::new($Path, $false, $encoding)
    try {
        $writer.Write($updatedContent)
    } finally {
        $writer.Dispose()
    }
}

function Update-FluxCodexMcpConfiguration {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot,
        [string]$ConfigPath = (Join-Path $env:USERPROFILE ".codex\config.toml")
    )

    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        return $false
    }

    $originalContent = [System.IO.File]::ReadAllText($ConfigPath)
    $escapedSourceRoot = $SourceRoot.Replace("\", "\\")
    $escapedDestinationRoot = $DestinationRoot.Replace("\", "\\")
    if (-not ($originalContent.Contains($SourceRoot) -or $originalContent.Contains($escapedSourceRoot))) {
        return $false
    }

    Update-FluxRootReferenceFile -Path $ConfigPath -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot
    $updatedContent = [System.IO.File]::ReadAllText($ConfigPath)
    if (
        $updatedContent.Contains($SourceRoot) -or
        $updatedContent.Contains($escapedSourceRoot) -or
        -not ($updatedContent.Contains($DestinationRoot) -or $updatedContent.Contains($escapedDestinationRoot))
    ) {
        throw "Codex MCP configuration retains an invalid Flux runtime path: $ConfigPath"
    }
    return $true
}

function Repair-FluxRootReferences {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $activeRelativePaths = @(
        "app\.env",
        "app\docker-compose.yml",
        "app\run-host-agent.pyw",
        "app\run-outlook-host.pyw",
        "app\.venv\pyvenv.cfg",
        "private\flux.env",
        "private\runtime.env"
    )
    foreach ($relativePath in $activeRelativePaths) {
        $targetPath = Join-Path $DestinationRoot $relativePath
        if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
            Update-FluxRootReferenceFile -Path $targetPath -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot
        }
    }
}

function Regenerate-FluxTargetLaunchers {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    foreach ($relativePath in @("app\run-host-agent.pyw", "app\run-outlook-host.pyw")) {
        $sourcePath = Join-Path $SourceRoot $relativePath
        $targetPath = Join-Path $DestinationRoot $relativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Source launcher template is missing: $sourcePath"
        }
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
    }
    Repair-FluxRootReferences -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot
}

function Clear-FluxTargetBytecode {
    param(
        [Parameter(Mandatory)]
        [string]$TargetPython,
        [Parameter(Mandatory)]
        [string]$TargetAppRoot,
        [Parameter(Mandatory)]
        [string]$TargetVenv
    )

    $appPathLiteral = $TargetAppRoot.Replace("'", "\'")
    $venvPathLiteral = $TargetVenv.Replace("'", "\'")
    $cleanupScript = @"
from pathlib import Path
for root in (Path(r'$appPathLiteral'), Path(r'$venvPathLiteral')):
    for pyc in root.rglob('*.pyc'):
        pyc.unlink()
"@
    Invoke-FluxNativeCommand -FilePath $TargetPython -Arguments @("-c", $cleanupScript) -WorkingDirectory $TargetAppRoot -StepName "clear target-only stale bytecode"
    Invoke-FluxNativeCommand -FilePath $TargetPython -Arguments @("-m", "compileall", "-f", "-q", $TargetAppRoot) -WorkingDirectory $TargetAppRoot -StepName "recompile target application bytecode"
}

function Test-FluxBytePattern {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [byte[]]$Bytes,
        [Parameter(Mandatory)]
        [byte[]]$Pattern
    )

    if ($Pattern.Length -eq 0 -or $Bytes.Length -lt $Pattern.Length) {
        return $false
    }
    for ($offset = 0; $offset -le ($Bytes.Length - $Pattern.Length); $offset++) {
        $matches = $true
        for ($index = 0; $index -lt $Pattern.Length; $index++) {
            if ($Bytes[$offset + $index] -ne $Pattern[$index]) {
                $matches = $false
                break
            }
        }
        if ($matches) {
            return $true
        }
    }
    return $false
}

function Assert-FluxTargetReferences {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $targetAppRoot = Join-Path $DestinationRoot "app"
    $targetVenv = Join-Path $targetAppRoot ".venv"
    $activeRelativePaths = @("app\.env", "app\docker-compose.yml", "app\run-host-agent.pyw", "app\run-outlook-host.pyw", "app\.venv\pyvenv.cfg")
    $referenceCandidates = @(
        foreach ($relativePath in $activeRelativePaths) {
            $path = Join-Path $DestinationRoot $relativePath
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $path
            }
        }
        Get-ChildItem -LiteralPath $targetVenv -File -Recurse | Where-Object {
            $_.Extension.ToLowerInvariant() -in @(".exe", ".cfg", ".pth", ".pyc")
        } | ForEach-Object { $_.FullName }
    )
    $sourcePatterns = @(
        [Text.Encoding]::UTF8.GetBytes($SourceRoot),
        [Text.Encoding]::Unicode.GetBytes($SourceRoot),
        [Text.Encoding]::BigEndianUnicode.GetBytes($SourceRoot)
    )
    foreach ($path in $referenceCandidates) {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        foreach ($pattern in $sourcePatterns) {
            if (Test-FluxBytePattern -Bytes $bytes -Pattern $pattern) {
                throw "Target active configuration, launcher, or bytecode retains a source-root reference: $path"
            }
        }
    }
    foreach ($requiredPath in @(
        (Join-Path $targetAppRoot "run-host-agent.pyw"),
        (Join-Path $targetAppRoot "run-outlook-host.pyw"),
        (Join-Path $targetVenv "pyvenv.cfg")
    )) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required target launcher or environment file is missing: $requiredPath"
        }
        if (-not ((Get-Content -LiteralPath $requiredPath -Raw).Contains($DestinationRoot))) {
            throw "Target launcher or environment file does not reference the destination root: $requiredPath"
        }
    }
}

function Rebuild-FluxPythonRuntime {
    param(
        [Parameter(Mandatory)]
        [string]$DestinationRoot,
        [Parameter(Mandatory)]
        [string]$SourceCodeRoot,
        [Parameter(Mandatory)]
        [string]$SourceRoot
    )

    $targetPython = Join-Path $DestinationRoot "python\python.exe"
    $targetAppRoot = Join-Path $DestinationRoot "app"
    $targetVenv = Join-Path $targetAppRoot ".venv"
    $targetVenvPython = Join-Path $targetVenv "Scripts\python.exe"
    $wheelhouse = Join-Path $DestinationRoot "package-cache\wheelhouse"

    foreach ($requiredPath in @($targetPython, $targetAppRoot, $wheelhouse)) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "Target Python rebuild prerequisite is missing: $requiredPath"
        }
    }
    if (@(Get-ChildItem -LiteralPath $wheelhouse -File -Recurse).Count -eq 0) {
        throw "Target local wheelhouse is empty: $wheelhouse"
    }

    $venvArguments = @("-m", "venv", "--clear", $targetVenv)
    Invoke-FluxNativeCommand -FilePath $targetPython -Arguments $venvArguments -WorkingDirectory $targetAppRoot -StepName "rebuild target Python virtual environment"

    if (-not (Test-Path -LiteralPath $targetVenvPython)) {
        throw "Target virtual environment Python was not created: $targetVenvPython"
    }

    $pipCommonArguments = @("--no-index", "--find-links", $wheelhouse)
    $productionExtras = "$SourceCodeRoot[api,corpus,mail,mcp,processors]"
    Invoke-FluxNativeCommand -FilePath $targetVenvPython -Arguments (@("-m", "pip", "install") + $pipCommonArguments + @("--upgrade", "pip")) -WorkingDirectory $SourceCodeRoot -StepName "upgrade target pip from local wheelhouse"
    Invoke-FluxNativeCommand -FilePath $targetVenvPython -Arguments (@("-m", "pip", "install") + $pipCommonArguments + @($productionExtras)) -WorkingDirectory $SourceCodeRoot -StepName "install target production extras from verified source revision"
    Invoke-FluxNativeCommand -FilePath $targetVenvPython -Arguments (@("-m", "pip", "install") + $pipCommonArguments + @("--force-reinstall", "--no-deps", "--no-build-isolation", $SourceCodeRoot)) -WorkingDirectory $SourceCodeRoot -StepName "install target production package from verified source revision"
    Regenerate-FluxTargetLaunchers -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot
    Clear-FluxTargetBytecode -TargetPython $targetVenvPython -TargetAppRoot $targetAppRoot -TargetVenv $targetVenv
    Repair-FluxRootReferences -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot
    Assert-FluxTargetReferences -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot
}

function Materialize-FluxLocalPipCacheWheels {
    param(
        [Parameter(Mandatory)]
        [string]$DestinationRoot,
        [Parameter(Mandatory)]
        [string]$SourceRoot
    )

    $sourcePython = Join-Path $SourceRoot "python\python.exe"
    $wheelhouse = Join-Path $DestinationRoot "package-cache\wheelhouse"
    $materializer = Join-Path $PSScriptRoot "materialize-local-pip-wheelhouse.py"
    foreach ($requiredPath in @($sourcePython, $wheelhouse, $materializer)) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "Local pip-cache materialization prerequisite is missing: $requiredPath"
        }
    }

    $pipCacheDirectoryOutput = & $sourcePython -m pip cache dir 2>&1
    $pipCacheDirectoryExitCode = $LASTEXITCODE
    if ($pipCacheDirectoryExitCode -ne 0) {
        throw "Could not resolve the source local pip cache directory."
    }
    $pipCacheDirectory = (@($pipCacheDirectoryOutput | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }) | Select-Object -First 1).ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($pipCacheDirectory) -or -not (Test-Path -LiteralPath $pipCacheDirectory -PathType Container)) {
        throw "Source local pip cache directory does not exist: $pipCacheDirectory"
    }

    # The durable cache is primarily populated for Linux Docker builds.  These
    # host-only wheels are recovered from pip's existing local HTTP cache so
    # the Windows virtual environment can be rebuilt without network access.
    $requiredDistributions = @(
        "pywin32",
        "watchdog",
        "cryptography",
        "pillow",
        "duckdb",
        "pyarrow",
        "numpy",
        "psutil",
        "pyyaml",
        "torch",
        "safetensors",
        "ctranslate2",
        "tokenizers",
        "onnxruntime",
        "av",
        "scikit-learn",
        "scipy",
        "regex",
        "yarl",
        "cffi",
        "tzdata",
        "psycopg-binary",
        "pydantic-core",
        "lxml",
        "colorama",
        "rpds-py",
        "protobuf",
        "win32-setctime",
        "charset-normalizer",
        "multidict",
        "propcache",
        "markupsafe"
    )
    $materializerArguments = @(
        $materializer,
        "--cache-dir", $pipCacheDirectory,
        "--wheelhouse", $wheelhouse
    )
    foreach ($distribution in $requiredDistributions) {
        $materializerArguments += @("--require", $distribution)
    }

    Invoke-FluxNativeCommand -FilePath $sourcePython -Arguments $materializerArguments -WorkingDirectory $SourceRoot -StepName "materialize required cached Windows wheels into target local wheelhouse"
}

function Build-FluxLocalWheelhouseImage {
    param(
        [Parameter(Mandatory)]
        [string]$DestinationRoot,
        [Parameter(Mandatory)]
        [string]$SourceCodeRoot
    )

    $wheelhouse = Join-Path $DestinationRoot "package-cache\wheelhouse"
    $wheelhouseDockerfile = Join-Path $SourceCodeRoot "docker\wheelhouse.Dockerfile"
    if (-not (Test-Path -LiteralPath $wheelhouse -PathType Container)) {
        throw "Target local wheelhouse is missing: $wheelhouse"
    }
    if (@(Get-ChildItem -LiteralPath $wheelhouse -File -Recurse).Count -eq 0) {
        throw "Target local wheelhouse is empty: $wheelhouse"
    }
    if (-not (Test-Path -LiteralPath $wheelhouseDockerfile -PathType Leaf)) {
        throw "Wheelhouse image Dockerfile is missing: $wheelhouseDockerfile"
    }

    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
        "build", "--progress=plain", "--pull=false",
        "-f", $wheelhouseDockerfile,
        "-t", "flux-llm-kb-wheelhouse:local",
        $wheelhouse
    ) -WorkingDirectory $SourceCodeRoot -StepName "build target local pip wheelhouse image"
}

function Test-FluxPathSegment {
    param(
        [Parameter(Mandatory)]
        [string]$Segment,
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $normalisedSegment = $Segment.Trim().Trim('"')
    foreach ($root in @($SourceRoot.TrimEnd("\"), $DestinationRoot.TrimEnd("\"))) {
        if ($normalisedSegment.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $normalisedSegment.StartsWith($root + "\", [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Set-FluxPathEntries {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $pathByScope = [ordered]@{
        User = [Environment]::GetEnvironmentVariable("Path", "User")
        Machine = [Environment]::GetEnvironmentVariable("Path", "Machine")
    }
    $targetSegments = @(
        (Join-Path $DestinationRoot "python"),
        (Join-Path $DestinationRoot "app\.venv\Scripts")
    )

    foreach ($scope in $pathByScope.Keys) {
        $currentPath = [string]$pathByScope[$scope]
        $segments = @($currentPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $dockerEntries = @($segments | Where-Object { $_ -match "(?i)\\docker(?:\\|$)" })
        $retainedSegments = @($segments | Where-Object { -not (Test-FluxPathSegment -Segment $_ -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot) })

        foreach ($dockerEntry in $dockerEntries) {
            if ($retainedSegments -notcontains $dockerEntry) {
                throw "Docker PATH entries must remain untouched."
            }
        }
        foreach ($targetSegment in $targetSegments) {
            if ($retainedSegments -notcontains $targetSegment) {
                $retainedSegments += $targetSegment
            }
        }

        $updatedPath = $retainedSegments -join ";"
        if ($updatedPath -eq $currentPath) {
            continue
        }
        if ($scope -eq "User") {
            [Environment]::SetEnvironmentVariable("Path", $updatedPath, "User")
        } else {
            [Environment]::SetEnvironmentVariable("Path", $updatedPath, "Machine")
        }
    }
}

function Register-FluxMigrationTasks {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Snapshot,
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    foreach ($taskSnapshot in $Snapshot.TaskSnapshots) {
        if (-not $taskSnapshot.Exists) {
            continue
        }
        $sourceTaskXml = [System.IO.File]::ReadAllText($taskSnapshot.XmlPath)
        $targetTaskXml = Replace-FluxRootText -Text $sourceTaskXml -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot
        if ($targetTaskXml -eq $sourceTaskXml) {
            throw "Scheduled task action does not reference the source install root: $($taskSnapshot.Name)"
        }
        $existingTask = Get-ScheduledTask -TaskName $taskSnapshot.Name -ErrorAction SilentlyContinue
        if ($null -ne $existingTask) {
            Unregister-ScheduledTask -TaskName $taskSnapshot.Name -Confirm:$false
        }
        $taskName = $taskSnapshot.Name
        Register-ScheduledTask -TaskName $taskName -Xml $targetTaskXml -Force | Out-Null

        $targetTask = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
        if (@($targetTask.Triggers).Count -ne $taskSnapshot.TriggerCount) {
            throw "Target task trigger count does not match the retained task XML: $taskName"
        }
        $targetActionText = @(
            $targetTask.Actions | ForEach-Object {
                "{0}|{1}|{2}" -f $_.Execute, $_.Arguments, $_.WorkingDirectory
            }
        ) -join [Environment]::NewLine
        if ($targetActionText.Contains($SourceRoot) -or -not $targetActionText.Contains($DestinationRoot)) {
            throw "Target scheduled task action does not point to the destination root: $taskName"
        }
        if ($taskSnapshot.WasRunning) {
            Start-ScheduledTask -TaskName $taskName
        }
    }
}

function Start-FluxTargetCompose {
    param(
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $targetAppRoot = Join-Path $DestinationRoot "app"
    $targetComposePath = Join-Path $targetAppRoot "docker-compose.yml"
    $targetEnvPath = Join-Path $targetAppRoot ".env"
    Start-FluxComposeServices -ComposePath $targetComposePath -EnvPath $targetEnvPath -WorkingDirectory $targetAppRoot -Services $script:FluxComposeServices -StepName "start complete target Compose project"
}

function Assert-FluxTargetComposeHealthy {
    param(
        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $targetAppRoot = Join-Path $DestinationRoot "app"
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    do {
        $state = Get-FluxComposeState -ComposePath (Join-Path $targetAppRoot "docker-compose.yml") -EnvPath (Join-Path $targetAppRoot ".env") -WorkingDirectory $targetAppRoot
        if ($state.query_status -eq "unavailable") {
            throw "Target Compose health could not be verified."
        }
        $missingServices = @($script:FluxComposeServices | Where-Object { $state.running_services -notcontains $_ })
        if ($missingServices.Count -eq 0) {
            return $state
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($missingServices.Count -gt 0) {
        throw "Target Compose project is not healthy; missing running services: $($missingServices -join ', ')"
    }
}

$preflight = Assert-FluxMigrationPreflight -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot -SourceCodeRoot $SourceCodeRoot -DockerDataRoot $DockerDataRoot -DockerSettingsPath $DockerSettingsPath -ResumePartialDestination ([bool]$ResumePartialDestination)
$report = New-FluxMigrationReport -Preflight $preflight -ApplyRequested ([bool]$Apply)

if (-not $Apply) {
    Write-FluxMigrationReport -Report $report -Json:$Json
    return
}

Assert-FluxAdministrator

$snapshot = $null
$targetComposeStarted = $false
$cutoverCompleted = $false
try {
    if ($preflight.ComposeState.query_status -eq "unavailable") {
        throw "Apply requires a readable source Compose state so recovery can restore the original runtime."
    }
    $snapshot = New-FluxMigrationSnapshot -Preflight $preflight
    Stop-FluxScheduledTasks
    Stop-FluxSourceRootProcesses -SourceRoot $preflight.SourceRoot
    if ($preflight.ComposeState.query_status -eq "running") {
        Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
            "compose",
            "--env-file", $preflight.SourceEnvPath,
            "-f", $preflight.SourceComposePath,
            "down"
        ) -WorkingDirectory $preflight.SourceAppRoot -StepName "stop source Compose project"
    }

    Stop-FluxSourceRootProcesses -SourceRoot $preflight.SourceRoot
    Copy-FluxInstallRoot -SourceRoot $preflight.SourceRoot -DestinationRoot $preflight.DestinationRoot
    Repair-FluxRootReferences -SourceRoot $preflight.SourceRoot -DestinationRoot $preflight.DestinationRoot
    Materialize-FluxLocalPipCacheWheels -DestinationRoot $preflight.DestinationRoot -SourceRoot $preflight.SourceRoot
    Rebuild-FluxPythonRuntime -DestinationRoot $preflight.DestinationRoot -SourceCodeRoot $preflight.SourceCodeRoot -SourceRoot $preflight.SourceRoot
    Build-FluxLocalWheelhouseImage -DestinationRoot $preflight.DestinationRoot -SourceCodeRoot $preflight.SourceCodeRoot
    $targetComposeStarted = $true
    Start-FluxTargetCompose -DestinationRoot $preflight.DestinationRoot
    $report.compose_state = Assert-FluxTargetComposeHealthy -DestinationRoot $preflight.DestinationRoot

    Set-FluxPathEntries -SourceRoot $preflight.SourceRoot -DestinationRoot $preflight.DestinationRoot
    Update-FluxCodexMcpConfiguration -SourceRoot $preflight.SourceRoot -DestinationRoot $preflight.DestinationRoot | Out-Null
    Register-FluxMigrationTasks -Snapshot $snapshot -SourceRoot $preflight.SourceRoot -DestinationRoot $preflight.DestinationRoot

    $report.status = "applied"
    $report.mutations_performed = $true
    $report.cutover_completed = $true
    $cutoverCompleted = $true
} catch {
    $report.status = "failed"
    $report.error = $_.Exception.Message
    $report.cutover_completed = $cutoverCompleted
    if ($null -ne $snapshot -and -not $cutoverCompleted) {
        $report.rollback = Restore-FluxMigrationSnapshot -Snapshot $snapshot -Preflight $preflight -TargetComposeStarted $targetComposeStarted
    }
    Write-FluxMigrationReport -Report $report -Json:$Json
    throw
}

Write-FluxMigrationReport -Report $report -Json:$Json
