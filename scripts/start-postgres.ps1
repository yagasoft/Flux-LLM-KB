param()

& "$PSScriptRoot\check-docker.ps1"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

docker compose up -d postgres

