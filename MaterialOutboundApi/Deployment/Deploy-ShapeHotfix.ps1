$ErrorActionPreference = "Stop"

& "D:\MupiShapeConversionDeploy\package\Deployment\Deploy-IisPackage.ps1" `
    -StagingPath "D:\MupiShapeConversionDeploy\package" `
    -TargetPath "D:\MaterialOutboundApi" `
    -BackupRoot "D:\MaterialOutboundApi-backups" `
    -SiteName "MaterialOutboundApi" `
    -AppPoolName "MaterialOutboundApiPool" `
    -Port 9879
