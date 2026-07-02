param(
    [Parameter(Mandatory = $true)]
    [string]$Image,
    [string]$Container = "",
    [string]$ExpectedRevision = ""
)

$ErrorActionPreference = "Stop"

$requiredLabels = @(
    "org.opencontainers.image.revision",
    "org.opencontainers.image.source",
    "org.opencontainers.image.created",
    "org.opencontainers.image.version"
)

function ConvertTo-FluxLabelMap {
    param([string]$Json)
    $labels = @{}
    if (-not $Json -or $Json.Trim() -eq "null") {
        return $labels
    }
    $parsed = $Json | ConvertFrom-Json
    foreach ($property in $parsed.PSObject.Properties) {
        if ($property.Value -is [datetime]) {
            $labels[$property.Name] = $property.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        } else {
            $labels[$property.Name] = [string]$property.Value
        }
    }
    return $labels
}

function Assert-FluxRequiredLabels {
    param([hashtable]$Labels, [string]$Subject)
    $missing = @()
    foreach ($label in $requiredLabels) {
        if (-not $Labels.ContainsKey($label) -or -not $Labels[$label]) {
            $missing += $label
        }
    }
    if ($missing.Count -gt 0) {
        Write-Error "$Subject is missing required OCI labels: $($missing -join ', ')"
        exit 1
    }
}

function Assert-FluxExpectedRevision {
    param([hashtable]$Labels, [string]$Subject, [string]$ExpectedRevision)
    if (-not $ExpectedRevision) { return }
    $actualRevision = $Labels["org.opencontainers.image.revision"]
    if ($actualRevision -ne $ExpectedRevision) {
        Write-Error "$Subject revision label '$actualRevision' does not match expected revision '$ExpectedRevision'."
        exit 1
    }
}

function Write-FluxLabels {
    param([hashtable]$Labels, [string]$Subject)
    Write-Host $Subject
    foreach ($label in $requiredLabels) {
        Write-Host "  ${label}: $($Labels[$label])"
    }
}

$imageLabelsJson = docker image inspect $Image --format "{{json .Config.Labels}}"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker image not found or not inspectable: $Image"
    exit 1
}
$imageLabels = ConvertTo-FluxLabelMap -Json (($imageLabelsJson | Select-Object -First 1) -as [string])
Assert-FluxRequiredLabels -Labels $imageLabels -Subject "Image $Image"
Assert-FluxExpectedRevision -Labels $imageLabels -Subject "Image $Image" -ExpectedRevision $ExpectedRevision
Write-FluxLabels -Labels $imageLabels -Subject "Image $Image labels"

if ($Container) {
    $containerLabelsJson = docker inspect $Container --format "{{json .Config.Labels}}"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker container not found or not inspectable: $Container"
        exit 1
    }
    $containerLabels = ConvertTo-FluxLabelMap -Json (($containerLabelsJson | Select-Object -First 1) -as [string])
    Assert-FluxRequiredLabels -Labels $containerLabels -Subject "Container $Container"
    Assert-FluxExpectedRevision -Labels $containerLabels -Subject "Container $Container" -ExpectedRevision $ExpectedRevision
    Write-FluxLabels -Labels $containerLabels -Subject "Container $Container labels"
}
