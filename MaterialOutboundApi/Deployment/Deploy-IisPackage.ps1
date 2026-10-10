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

function Wait-AppPoolState([string]$Expected, [int]$Seconds = 45) {
    for ($attempt = 0; $attempt -lt $Seconds; $attempt++) {
        if ((Get-WebAppPoolState -Name $AppPoolName).Value -eq $Expected) {
            return
        }
        Start-Sleep -Seconds 1
    }
    throw "IIS application pool did not reach state: $Expected"
}

function Ensure-AppPoolStarted {
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            $state = (Get-WebAppPoolState -Name $AppPoolName).Value
            if ($state -eq "Started") { return }
            if ($state -eq "Stopping") {
                Start-Sleep -Seconds 2
                continue
            }
            Start-WebAppPool -Name $AppPoolName -ErrorAction Stop
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }
    throw "IIS application pool could not be started."
}

function Ensure-SiteStarted {
    if ((Get-WebsiteState -Name $SiteName).Value -ne "Started") {
        Start-Website -Name $SiteName -ErrorAction Stop
    }
}

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

try {
    if ((Get-WebsiteState -Name $SiteName).Value -eq "Started") {
        Stop-Website -Name $SiteName
    }
    if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne "Stopped") {
        Stop-WebAppPool -Name $AppPoolName
        Wait-AppPoolState "Stopped"
    }

    Get-ChildItem -LiteralPath $StagingPath -Force |
        Where-Object { $_.Name -ne "App_Data" } |
        Copy-Item -Destination $TargetPath -Recurse -Force

    Ensure-AppPoolStarted
    Ensure-SiteStarted

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
        -Value ([DateTime]::Now.ToString("O")) -Encoding UTF8
}
catch {
    try {
        if ((Get-WebsiteState -Name $SiteName).Value -eq "Started") {
            Stop-Website -Name $SiteName -ErrorAction SilentlyContinue
        }
        if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne "Stopped") {
            Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
            Wait-AppPoolState "Stopped"
        }
        Get-ChildItem -LiteralPath $backupPath -Force |
            Copy-Item -Destination $TargetPath -Recurse -Force
        Ensure-AppPoolStarted
        Ensure-SiteStarted
    }
    catch { }
    throw
}
finally {
    Stop-Transcript | Out-Null
}
