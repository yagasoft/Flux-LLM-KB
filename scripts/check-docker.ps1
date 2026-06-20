param()

$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker) {
    Write-Error "Docker is required for the default PostgreSQL/pgvector runtime profile but was not found on PATH."
    exit 1
}

docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker Compose is required but is not available through 'docker compose'."
    exit 1
}

Write-Output "Docker Compose is available."

