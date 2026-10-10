param(
    [string]$StagingPath = "D:\MupiShapeConversionDeploy\package",
    [string]$TargetPath = "D:\MaterialOutboundApi",
    [string]$BackupRoot = "D:\MaterialOutboundApi-backups",
    [string]$MigrationPath = "D:\MupiShapeConversionDeploy\019_add_shape_conversions.sql",
    [string]$SiteName = "MaterialOutboundApi",
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
$errorPath = Join-Path $deployRoot "deployment-error.txt"
$settingsPath = Join-Path $TargetPath "App_Data\settings.json"
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

trap {
    ($_ | Out-String) | Set-Content -LiteralPath $errorPath -Encoding UTF8
    exit 1
}

foreach ($required in @($StagingPath, $TargetPath, $MigrationPath, $settingsPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required deployment path does not exist: $required"
    }
}

Add-Type -AssemblyName System.Security
$stored = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
$entropy = [Text.Encoding]::UTF8.GetBytes("MaterialOutboundApi.Settings.v1")
$protected = [Convert]::FromBase64String($stored.SqlPasswordProtected)
$plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
    $protected,
    $entropy,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
$sqlPassword = [Text.Encoding]::UTF8.GetString($plain)

function New-SqlConnection([string]$Database) {
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder["Data Source"] = [string]$stored.Public.SqlServer
    $builder["Initial Catalog"] = $Database
    $builder["User ID"] = [string]$stored.Public.SqlUser
    $builder["Password"] = $sqlPassword
    $builder["Encrypt"] = $false
    $builder["TrustServerCertificate"] = $true
    return New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
}

$connection = New-SqlConnection "master"
try {
    $connection.Open()
    $pathCommand = $connection.CreateCommand()
    $pathCommand.CommandText = "select convert(nvarchar(4000), serverproperty('InstanceDefaultBackupPath'))"
    $defaultBackupPath = [string]$pathCommand.ExecuteScalar()
    if ([string]::IsNullOrWhiteSpace($defaultBackupPath)) {
        throw "SQL Server did not return InstanceDefaultBackupPath."
    }
    $databaseBackup = Join-Path $defaultBackupPath "YX_TEST-pre-shape-$timestamp.bak"
    $escapedBackup = $databaseBackup.Replace("'", "''")
    $backup = $connection.CreateCommand()
    $backup.CommandTimeout = 0
    $backup.CommandText = "BACKUP DATABASE [YX_TEST] TO DISK=N'$escapedBackup' WITH COPY_ONLY, CHECKSUM, COMPRESSION"
    try {
        [void]$backup.ExecuteNonQuery()
    }
    catch {
        $backup.CommandText = "BACKUP DATABASE [YX_TEST] TO DISK=N'$escapedBackup' WITH COPY_ONLY, CHECKSUM"
        [void]$backup.ExecuteNonQuery()
    }
    $verify = $connection.CreateCommand()
    $verify.CommandTimeout = 0
    $verify.CommandText = "RESTORE VERIFYONLY FROM DISK=N'$escapedBackup' WITH CHECKSUM"
    [void]$verify.ExecuteNonQuery()
}
finally {
    $connection.Dispose()
}

$applicationConnection = New-SqlConnection "YX_TEST"
try {
    $applicationConnection.Open()
    $migration = $applicationConnection.CreateCommand()
    $migration.CommandTimeout = 120
    $migration.CommandText = Get-Content -LiteralPath $MigrationPath -Raw -Encoding UTF8
    [void]$migration.ExecuteNonQuery()
    $verifyMigration = $applicationConnection.CreateCommand()
    $verifyMigration.CommandText = @"
select case when object_id(N'dbo.MupiShapeConversion_yx', N'U') is not null
                  and exists(select 1 from dbo.MupiModule_yx where ModuleCode='shape_conversion')
            then 1 else 0 end
"@
    if ([int]$verifyMigration.ExecuteScalar() -ne 1) {
        throw "Migration 019 verification failed."
    }
}
finally {
    $applicationConnection.Dispose()
}

$iisDeploy = Join-Path $StagingPath "Deployment\Deploy-IisPackage.ps1"
& $iisDeploy -StagingPath $StagingPath -TargetPath $TargetPath `
    -BackupRoot $BackupRoot -SiteName $SiteName `
    -AppPoolName $AppPoolName -Port $Port

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
$settingsTemp = $settingsPath + ".shape.tmp"
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
    throw "Post-deployment health/version check failed."
}

[pscustomobject]@{
    Succeeded = $true
    CompletedAt = [DateTime]::Now.ToString("O")
    DatabaseBackup = $databaseBackup
    IisBackupRoot = $BackupRoot
    Version = $health.data.version
    Port = $Port
    Profile008 = $profile
    Profile998 = $stored.Public.Shape998
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding UTF8
