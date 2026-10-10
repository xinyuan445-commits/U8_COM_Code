param(
    [string]$StagingPath = "D:\MupiMaterialOutboundV2Deploy\package",
    [string]$TargetPath = "D:\MaterialOutboundApi",
    [string]$BackupRoot = "D:\MaterialOutboundApi-backups",
    [string]$SiteName = "MaterialOutboundApi",
    [string]$AppPoolName = "MaterialOutboundApiPool",
    [int]$Port = 9879
)

$ErrorActionPreference = "Stop"
$transcriptPath = "D:\MupiMaterialOutboundV2Deploy\iis-deploy.log"
Start-Transcript -Path $transcriptPath -Force | Out-Null
Import-Module WebAdministration

if (-not (Test-Path -LiteralPath $StagingPath)) {
    throw "IIS staging package does not exist: $StagingPath"
}
if (-not (Test-Path -LiteralPath $TargetPath)) {
    throw "IIS target does not exist: $TargetPath"
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$backupPath = Join-Path $BackupRoot "MaterialOutboundApi-pre-v2-$timestamp"
New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
Get-ChildItem -LiteralPath $TargetPath -Force |
    Copy-Item -Destination $backupPath -Recurse -Force

$siteStopped = $false
$poolStopped = $false
try {
    Stop-Website -Name $SiteName -ErrorAction SilentlyContinue
    $siteStopped = $true
    Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    $poolStopped = $true

    Get-ChildItem -LiteralPath $StagingPath -Force |
        Where-Object { $_.Name -ne "App_Data" } |
        Copy-Item -Destination $TargetPath -Recurse -Force

    Start-WebAppPool -Name $AppPoolName
    $poolStopped = $false
    Start-Website -Name $SiteName
    $siteStopped = $false

    $healthUrl = "http://localhost:$Port/api/v1/health"
    $healthy = $false
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
            if ($response.success -and $response.data.status -eq "running") {
                $healthy = $true
                break
            }
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }
    if (-not $healthy) {
        throw "IIS health check failed after deployment."
    }
    Set-Content -LiteralPath "D:\MupiMaterialOutboundV2Deploy\completed.flag" `
        -Value ([DateTime]::Now.ToString("O")) `
        -Encoding UTF8
}
catch {
    Stop-Website -Name $SiteName -ErrorAction SilentlyContinue
    Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $backupPath -Force |
        Copy-Item -Destination $TargetPath -Recurse -Force
    Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    Start-Website -Name $SiteName -ErrorAction SilentlyContinue
    throw
}
finally {
    if ($poolStopped) {
        Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    }
    if ($siteStopped) {
        Start-Website -Name $SiteName -ErrorAction SilentlyContinue
    }
}

Stop-Transcript | Out-Null
