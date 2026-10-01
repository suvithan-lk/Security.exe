<#
.SYNOPSIS
    Downloads the ONNX face models required by SECURITY.EXE.

.DESCRIPTION
    Fetches two models from the official opencv_zoo repository (Apache-2.0,
    commercially usable) into ./models:

      face_detection_yunet_2023mar.onnx      ~227 KB   face detector / 5-point landmarks
      face_recognition_sface_2021dec.onnx    ~36.9 MB  128-D face embedding

    The application also downloads these automatically on first run when the
    files are missing and the network is reachable, so this script is only
    needed for an offline install or a clean machine.

    Nothing is uploaded anywhere: the files are only ever fetched, never sent.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\download-models.ps1

.NOTES
    Licence  : Apache-2.0 (opencv_zoo)
    Source   : https://github.com/opencv/opencv_zoo
    Phase 1  : models are excluded from version control (see .gitignore).
#>
[CmdletBinding()]
param(
    # Where the models should land. Defaults to ./models next to this repo root.
    [string]$Destination,

    # Skip files that are already present and non-empty.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes large downloads dramatically faster

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repoRoot 'models'
}

$models = @(
    @{
        Name     = 'face_detection_yunet_2023mar.onnx'
        Url      = 'https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx'
        MinBytes = 100KB
    },
    @{
        Name     = 'face_recognition_sface_2021dec.onnx'
        Url      = 'https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx'
        MinBytes = 30MB
    }
)

function Write-Step($message) { Write-Host "[models] $message" -ForegroundColor Cyan }
function Write-Ok($message)   { Write-Host "[models] $message" -ForegroundColor Green }
function Write-Warn($message) { Write-Host "[models] $message" -ForegroundColor Yellow }

Write-Step "Destination: $Destination"
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

# TLS 1.2 is required by raw.githubusercontent.com / media.githubusercontent.com.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$allPresent = $true

foreach ($model in $models) {
    $target = Join-Path $Destination $model.Name

    if (-not $Force -and (Test-Path $target) -and ((Get-Item $target).Length -ge $model.MinBytes)) {
        $size = [Math]::Round((Get-Item $target).Length / 1MB, 2)
        Write-Ok "$($model.Name) already present ($size MB) - skipping"
        continue
    }

    $allPresent = $false
    $tmp = "$target.download"

    try {
        Write-Step "Downloading $($model.Name) ..."
        Invoke-WebRequest -Uri $model.Url -OutFile $tmp -UseBasicParsing -TimeoutSec 600

        $length = (Get-Item $tmp).Length
        if ($length -lt $model.MinBytes) {
            throw "Downloaded file is only $length bytes - expected at least $($model.MinBytes). The download was truncated."
        }

        Move-Item -Path $tmp -Destination $target -Force
        Write-Ok "$($model.Name) saved ($([Math]::Round($length / 1MB, 2)) MB)"
    }
    catch {
        if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        Write-Warn "Could not download $($model.Name): $($_.Exception.Message)"
        Write-Warn "SECURITY.EXE will still start; the dashboard will show 'Recognition engine not ready'."
        Write-Warn "Re-run this script when you are online."
    }
}

if ($allPresent) {
    Write-Ok 'All models are already present.'
    exit 0
}

$remaining = @($models | Where-Object {
    -not ((Test-Path (Join-Path $Destination $_.Name)) -and ((Get-Item (Join-Path $Destination $_.Name)).Length -ge $_.MinBytes))
})

if ($remaining.Count -eq 0) {
    Write-Ok 'All models downloaded successfully.'
    exit 0
}

Write-Warn "$($remaining.Count) model(s) still missing."
exit 1
