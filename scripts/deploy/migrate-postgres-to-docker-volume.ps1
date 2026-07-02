param(
    [string]$InstallRoot = "D:\FluxLLMKB",
    [int]$PostgresPort = 5432,
    [string]$PostgresVolumeName = "flux_llm_kb_postgres_data",
    [string]$BackupRoot = ""
)

$ErrorActionPreference = "Stop"

if (-not $BackupRoot) {
    $BackupRoot = Join-Path $InstallRoot "backups"
}

$legacyPostgresRoot = Join-Path $InstallRoot "data\postgres"
$legacyPgVersion = Join-Path $legacyPostgresRoot "PG_VERSION"
$tempContainerName = "flux-llm-kb-postgres-volume-migrate"

function Invoke-FluxDocker {
    param([string[]]$Arguments)
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Test-FluxDockerVolumeExists {
    param([string]$Name)
    docker volume inspect $Name *> $null
    return $LASTEXITCODE -eq 0
}

function Test-FluxPostgresVolumeInitialised {
    param([string]$Name)
    if (-not (Test-FluxDockerVolumeExists -Name $Name)) {
        return $false
    }
    docker run --rm --entrypoint sh -v "${Name}:/var/lib/postgresql/data" postgres:16 -c "test -f /var/lib/postgresql/data/PG_VERSION" *> $null
    return $LASTEXITCODE -eq 0
}

function Get-FluxContainerStatus {
    param([string]$ContainerName)
    $status = docker inspect --format "{{.State.Status}}" $ContainerName 2>$null
    if ($LASTEXITCODE -ne 0) { return "" }
    return ($status | Select-Object -First 1).Trim()
}

function Test-FluxDirectoryHasEntries {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        return $false
    }
    $entry = Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue | Select-Object -First 1
    return $null -ne $entry
}

function Test-FluxDockerVolumeHasEntries {
    param([string]$Name)
    if (-not (Test-FluxDockerVolumeExists -Name $Name)) {
        return $false
    }
    docker run --rm --entrypoint sh -v "${Name}:/to" postgres:16 -c "find /to -mindepth 1 -maxdepth 1 -print -quit | grep -q ." *> $null
    return $LASTEXITCODE -eq 0
}

function Copy-FluxDirectoryToDockerVolume {
    param(
        [string]$SourcePath,
        [string]$VolumeName,
        [string]$CopyCommand = "cp -a /from/. /to/"
    )
    if (-not (Test-FluxDirectoryHasEntries -Path $SourcePath)) {
        Write-Host "No existing container-owned data found at $SourcePath; $VolumeName does not need migration."
        return
    }
    docker volume create $VolumeName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "docker volume create $VolumeName failed with exit code $LASTEXITCODE"
    }
    if (Test-FluxDockerVolumeHasEntries -Name $VolumeName) {
        Write-Host "Docker volume $VolumeName already contains data; $SourcePath is left untouched for rollback."
        return
    }
    Write-Host "Copying $SourcePath into Docker volume $VolumeName"
    Invoke-FluxDocker -Arguments @(
        "run", "--rm",
        "--entrypoint", "sh",
        "-v", "${SourcePath}:/from:ro",
        "-v", "${VolumeName}:/to",
        "postgres:16",
        "-c", $CopyCommand
    )
}

function Invoke-FluxContainerStateVolumeMigration {
    Copy-FluxDirectoryToDockerVolume -SourcePath (Join-Path $InstallRoot "private\cache") -VolumeName "flux_llm_kb_cache"
    Copy-FluxDirectoryToDockerVolume -SourcePath (Join-Path $InstallRoot "runtime") -VolumeName "flux_llm_kb_runtime"
    Copy-FluxDirectoryToDockerVolume -SourcePath (Join-Path $InstallRoot "logs") -VolumeName "flux_llm_kb_logs" -CopyCommand "cd /from && tar --exclude='*.lock' -cf - . | tar -C /to -xf -"
    Copy-FluxDirectoryToDockerVolume -SourcePath (Join-Path $InstallRoot "models\ollama") -VolumeName "flux_llm_kb_ollama_models"
}

function Wait-FluxTemporaryPostgres {
    param([string]$ContainerName)
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        docker exec $ContainerName pg_isready -U flux -d flux_llm_kb *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    docker logs --tail 80 $ContainerName 2>&1 | Out-Host
    throw "Timed out waiting for temporary PostgreSQL restore container."
}

Invoke-FluxContainerStateVolumeMigration

if (-not (Test-Path $legacyPgVersion)) {
    Write-Host "No legacy PostgreSQL bind data found at $legacyPostgresRoot; Docker volume migration is not needed."
    exit 0
}

if (Test-FluxPostgresVolumeInitialised -Name $PostgresVolumeName) {
    Write-Host "PostgreSQL Docker volume $PostgresVolumeName is already initialised; legacy bind data is left untouched for rollback."
    exit 0
}

$status = Get-FluxContainerStatus -ContainerName "flux-llm-kb-postgres"
if ($status -ne "running") {
    throw "Legacy PostgreSQL data exists at $legacyPostgresRoot, but flux-llm-kb-postgres is not running. Start the current runtime and rerun deployment so a verified pg_dump can be restored into Docker volume $PostgresVolumeName."
}

New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupFileName = "postgres-bind-to-volume-$timestamp.dump"
$backupPath = Join-Path $BackupRoot $backupFileName
$containerDumpPath = "/tmp/$backupFileName"

Write-Host "Backing up legacy PostgreSQL bind data to $backupPath"
Invoke-FluxDocker -Arguments @("exec", "flux-llm-kb-postgres", "pg_dump", "-U", "flux", "-d", "flux_llm_kb", "-Fc", "-f", $containerDumpPath)
Invoke-FluxDocker -Arguments @("cp", "flux-llm-kb-postgres:$containerDumpPath", $backupPath)
docker exec flux-llm-kb-postgres rm -f $containerDumpPath *> $null

$backup = Get-Item -LiteralPath $backupPath -ErrorAction Stop
if ($backup.Length -le 0) {
    throw "PostgreSQL backup file was empty: $backupPath"
}

foreach ($containerName in @("flux-llm-kb-api", "flux-llm-kb-worker")) {
    docker stop $containerName 2>$null | Out-Null
}
Invoke-FluxDocker -Arguments @("stop", "flux-llm-kb-postgres")

docker volume create $PostgresVolumeName | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "docker volume create $PostgresVolumeName failed with exit code $LASTEXITCODE"
}

docker rm -f $tempContainerName 2>$null | Out-Null
try {
    Invoke-FluxDocker -Arguments @(
        "run", "-d",
        "--name", $tempContainerName,
        "-e", "POSTGRES_USER=flux",
        "-e", "POSTGRES_PASSWORD=flux",
        "-e", "POSTGRES_DB=flux_llm_kb",
        "-v", "${PostgresVolumeName}:/var/lib/postgresql/data",
        "postgres:16"
    )
    Wait-FluxTemporaryPostgres -ContainerName $tempContainerName
    Invoke-FluxDocker -Arguments @("cp", $backupPath, "${tempContainerName}:/tmp/$backupFileName")
    Invoke-FluxDocker -Arguments @("exec", $tempContainerName, "pg_restore", "-U", "flux", "-d", "flux_llm_kb", "--clean", "--if-exists", "/tmp/$backupFileName")
} finally {
    docker stop $tempContainerName 2>$null | Out-Null
}

Write-Host "PostgreSQL data restored into Docker volume $PostgresVolumeName. Legacy data remains at $legacyPostgresRoot for rollback."
