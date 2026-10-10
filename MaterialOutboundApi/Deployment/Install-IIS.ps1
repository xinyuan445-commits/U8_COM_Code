param(
    [string]$SiteName = "MaterialOutboundApi",
    [string]$AppPoolName = "MaterialOutboundApiPool",
    [string]$PhysicalPath = "D:\MaterialOutboundApi",
    [int]$Port = 9879
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script as Administrator."
}

if (-not (Test-Path -LiteralPath "$env:windir\System32\inetsrv\appcmd.exe")) {
    throw "IIS is not installed. Install IIS, ASP.NET 4.x, and ISAPI first."
}
if (-not (Test-Path -LiteralPath $PhysicalPath)) {
    throw "Deployment directory does not exist: $PhysicalPath"
}

Import-Module WebAdministration

if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name managedRuntimeVersion -Value "v4.0"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name managedPipelineMode -Value "Integrated"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name enable32BitAppOnWin64 -Value $true
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name processModel.identityType -Value "ApplicationPoolIdentity"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name processModel.loadUserProfile -Value $true
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name processModel.maxProcesses -Value 1
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name recycling.disallowOverlappingRotation -Value $true
Set-ItemProperty "IIS:\AppPools\$AppPoolName" `
    -Name startMode -Value "AlwaysRunning"

$site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
if ($null -eq $site) {
    New-Website -Name $SiteName `
        -PhysicalPath $PhysicalPath `
        -Port $Port `
        -IPAddress "*" `
        -ApplicationPool $AppPoolName | Out-Null
}
else {
    Set-ItemProperty "IIS:\Sites\$SiteName" `
        -Name physicalPath -Value $PhysicalPath
    Set-ItemProperty "IIS:\Sites\$SiteName" `
        -Name applicationPool -Value $AppPoolName
    Get-WebBinding -Name $SiteName -Protocol "http" |
        Remove-WebBinding
    New-WebBinding -Name $SiteName -Protocol "http" `
        -IPAddress "*" -Port $Port
}
Set-ItemProperty "IIS:\Sites\$SiteName" -Name serverAutoStart -Value $true

foreach ($folderName in @("App_Data", "App_Data\Logs")) {
    $folder = Join-Path $PhysicalPath $folderName
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $acl = Get-Acl -LiteralPath $folder
    $rule = New-Object Security.AccessControl.FileSystemAccessRule(
        "IIS AppPool\$AppPoolName",
        "Modify",
        "ContainerInherit,ObjectInherit",
        "None",
        "Allow")
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $folder -AclObject $acl
}

$firewallName = "MaterialOutboundApi TCP $Port"
$firewall = Get-NetFirewallRule -DisplayName $firewallName `
    -ErrorAction SilentlyContinue
if ($null -eq $firewall) {
    New-NetFirewallRule `
        -DisplayName $firewallName `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $Port `
        -Profile Domain,Private `
        -RemoteAddress LocalSubnet | Out-Null
}
else {
    Set-NetFirewallRule -DisplayName $firewallName `
        -Enabled True `
        -Profile Domain,Private `
        -Action Allow `
        -Direction Inbound `
        -RemoteAddress LocalSubnet | Out-Null
}

Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
Start-Website -Name $SiteName

$healthUrl = "http://localhost:$Port/api/v1/health"
$lastError = $null
for ($attempt = 1; $attempt -le 10; $attempt++) {
    try {
        $response = Invoke-WebRequest $healthUrl -UseBasicParsing `
            -TimeoutSec 10
        if ($response.StatusCode -eq 200) {
            Write-Host "Deployment succeeded: $healthUrl"
            Write-Host "Configuration: http://localhost:$Port/admin/config"
            Write-Host "Swagger: http://localhost:$Port/swagger"
            exit 0
        }
    }
    catch {
        $lastError = $_
        Start-Sleep -Seconds 2
    }
}

throw "IIS was configured but the health check failed: $lastError"
