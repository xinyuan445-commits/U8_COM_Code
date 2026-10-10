param(
    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",
    [string]$OutputPath = "D:\MupiShapeConversionDeploy\profile-inspection.json"
)

$ErrorActionPreference = "Stop"
trap {
    ($_ | Out-String) | Set-Content -LiteralPath `
        "D:\MupiShapeConversionDeploy\profile-inspection-error2.txt" `
        -Encoding UTF8
    exit 1
}
"started $([DateTime]::Now.ToString('O'))" | Set-Content -LiteralPath `
    "D:\MupiShapeConversionDeploy\profile-inspection-started.txt" -Encoding UTF8
$stored = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
Add-Type -AssemblyName System.Security
$entropy = [Text.Encoding]::UTF8.GetBytes("MaterialOutboundApi.Settings.v1")
$protected = [Convert]::FromBase64String($stored.SqlPasswordProtected)
$plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
    $protected,
    $entropy,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
$password = [Text.Encoding]::UTF8.GetString($plain)

$results = foreach ($database in @("UFDATA_008_2021", "UFDATA_998_2021")) {
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder["Data Source"] = [string]$stored.Public.SqlServer
    $builder["Initial Catalog"] = $database
    $builder["User ID"] = [string]$stored.Public.SqlUser
    $builder["Password"] = $password
    $builder["Encrypt"] = $false
    $builder["TrustServerCertificate"] = $true
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = @"
select
  (select top (1) cDepName from dbo.Department where cDepCode=N'04125') DepartmentName,
  (select top (1) cRdName from dbo.Rd_Style where cRdCode=N'112') InboundName,
  (select top (1) cRdName from dbo.Rd_Style where cRdCode=N'216') OutboundName,
  (select top (1) vt_id from dbo.AssemVouch where vt_id is not null order by ID desc) RecentTemplateId,
  (select top (1) cDepCode from dbo.AssemVouch order by ID desc) RecentDepartmentCode,
  (select top (1) cIRdCode from dbo.AssemVouch order by ID desc) RecentInboundRdCode,
  (select top (1) cORdCode from dbo.AssemVouch order by ID desc) RecentOutboundRdCode
"@
        $reader = $command.ExecuteReader()
        [void]$reader.Read()
        [pscustomobject]@{
            Database = $database
            DepartmentName = if ($reader.IsDBNull(0)) { $null } else { $reader.GetString(0) }
            InboundName = if ($reader.IsDBNull(1)) { $null } else { $reader.GetString(1) }
            OutboundName = if ($reader.IsDBNull(2)) { $null } else { $reader.GetString(2) }
            RecentTemplateId = if ($reader.IsDBNull(3)) { $null } else { $reader.GetValue(3) }
            RecentDepartmentCode = if ($reader.IsDBNull(4)) { $null } else { $reader.GetString(4) }
            RecentInboundRdCode = if ($reader.IsDBNull(5)) { $null } else { $reader.GetString(5) }
            RecentOutboundRdCode = if ($reader.IsDBNull(6)) { $null } else { $reader.GetString(6) }
        }
        $reader.Close()
    }
    finally {
        $connection.Dispose()
    }
}

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
