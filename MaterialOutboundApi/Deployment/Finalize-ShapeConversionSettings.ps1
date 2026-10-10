param(
    [string]$TargetPath = "D:\MaterialOutboundApi",
    [string]$AppPoolName = "MaterialOutboundApiPool",
    [int]$Port = 9879,
    [string]$DepartmentCode = "04125",
    [string]$InboundRdCode = "112",
    [string]$OutboundRdCode = "216",
    [int]$VoucherTemplateId = 91
)

$ErrorActionPreference = "Stop"
$deployRoot = "D:\MupiShapeConversionDeploy"
$resultPath = Join-Path $deployRoot "deployment-result.json"
$errorPath = Join-Path $deployRoot "deployment-finalize-error.txt"
$settingsPath = Join-Path $TargetPath "App_Data\settings.json"
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

trap {
    ($_ | Out-String) | Set-Content -LiteralPath $errorPath -Encoding UTF8
    exit 1
}

if (-not (Test-Path -LiteralPath $settingsPath)) {
    throw "IIS settings file does not exist: $settingsPath"
}

$stored = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
$profile = [pscustomobject]@{
    DepartmentCode = $DepartmentCode
    InboundRdCode = $InboundRdCode
    OutboundRdCode = $OutboundRdCode
    VoucherTemplateId = $VoucherTemplateId
}
$stored.Public | Add-Member -NotePropertyName Shape008 `
    -NotePropertyValue $profile -Force
$stored.Public | Add-Member -NotePropertyName Shape998 `
    -NotePropertyValue ([pscustomobject]@{
        DepartmentCode = $DepartmentCode
        InboundRdCode = $InboundRdCode
        OutboundRdCode = $OutboundRdCode
        VoucherTemplateId = $VoucherTemplateId
    }) -Force

$settingsTemp = $settingsPath + ".tmp"
[IO.File]::WriteAllText(
    $settingsTemp,
    ($stored | ConvertTo-Json -Depth 10),
    (New-Object Text.UTF8Encoding($false)))
$settingsBackup = $settingsPath + ".pre-shape-" + $timestamp
[IO.File]::Replace($settingsTemp, $settingsPath, $settingsBackup)

Import-Module WebAdministration
Restart-WebAppPool -Name $AppPoolName
$healthUrl = "http://localhost:$Port/api/v1/health"
$health = $null
for ($attempt = 1; $attempt -le 15; $attempt++) {
    try {
        $candidate = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
        if ($candidate.success -and $candidate.data.status -eq "running") {
            $health = $candidate
            break
        }
    }
    catch {
        Start-Sleep -Seconds 2
    }
}
if ($null -eq $health -or $health.data.version -notlike "1.2.0*") {
    throw "Post-finalize health/version check failed."
}

[pscustomobject]@{
    Succeeded = $true
    CompletedAt = [DateTime]::Now.ToString("O")
    Version = $health.data.version
    Port = $Port
    SettingsBackup = $settingsBackup
    Profile008 = $stored.Public.Shape008
    Profile998 = $stored.Public.Shape998
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding UTF8
