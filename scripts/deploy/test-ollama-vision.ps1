param(
    [int]$OllamaHostPort = 11435,
    [string]$Model = "qwen3-vl:8b",
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Get-FluxErrorBody {
    param([object]$ErrorRecord)
    $detail = $ErrorRecord.ErrorDetails.Message
    if ($detail) { return [string]$detail }
    $response = $ErrorRecord.Exception.Response
    if (-not $response) { return "" }
    try {
        $stream = $response.GetResponseStream()
        if (-not $stream) { return "" }
        $reader = [System.IO.StreamReader]::new($stream)
        return $reader.ReadToEnd()
    } catch {
        return ""
    }
}

Write-Host "Checking ffmpeg/ffprobe inside flux-ollama..."
& docker exec flux-ollama sh -lc "command -v ffmpeg >/dev/null && command -v ffprobe >/dev/null"
if ($LASTEXITCODE -ne 0) {
    throw "Ollama media runtime is incomplete: ffmpeg and ffprobe must both resolve inside the flux-ollama container."
}

$endpoint = "http://127.0.0.1:${OllamaHostPort}/api/generate"
$tinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMB/axjfkUAAAAASUVORK5CYII="
$request = @{
    model = $Model
    stream = $false
    prompt = "Reply OK if you can decode this image."
    images = @($tinyPng)
    options = @{ num_predict = 8 }
}
$body = $request | ConvertTo-Json -Depth 5 -Compress

Write-Host "Submitting tiny PNG vision decode smoke test to $endpoint with model $Model..."
try {
    $response = Invoke-RestMethod -Method Post -Uri $endpoint -ContentType "application/json" -Body $body -TimeoutSec $TimeoutSeconds
} catch {
    $detail = (Get-FluxErrorBody -ErrorRecord $_).Trim()
    if ($detail.Length -gt 1000) {
        $detail = $detail.Substring(0, 1000)
    }
    if ($detail -match "ffprobe|ffmpeg|decode|media") {
        throw "Ollama vision decode smoke test failed with media-runtime error: $detail"
    }
    if ($detail) {
        throw "Ollama vision decode smoke test failed: $detail"
    }
    throw "Ollama vision decode smoke test failed: $($_.Exception.Message)"
}

$text = [string]($response.response ?? $response.message.content ?? "")
if (-not $text.Trim()) {
    throw "Ollama vision decode smoke test returned no response text; the request reached Ollama but image decoding or generation may be broken."
}

Write-Host "Ollama vision decode smoke test passed for $Model."
