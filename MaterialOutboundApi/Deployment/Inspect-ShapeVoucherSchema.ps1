param(
    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",
    [string]$OutputPath = "D:\MupiShapeConversionDeploy\shape-schema-inspection.json"
)

$ErrorActionPreference = "Stop"
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
        $tables = @{}
        foreach ($table in @("AssemVouch", "RdRecord09", "RdRecord08", "CurrentStock", "InvPositionSum")) {
            $command = $connection.CreateCommand()
            $command.CommandText = @"
select c.name
from sys.columns c
where c.object_id=object_id(@table)
order by c.column_id
"@
            [void]$command.Parameters.Add("@table", [Data.SqlDbType]::NVarChar, 128)
            $command.Parameters["@table"].Value = "dbo." + $table
            $reader = $command.ExecuteReader()
            $columns = New-Object Collections.Generic.List[string]
            while ($reader.Read()) { $columns.Add($reader.GetString(0)) }
            $reader.Close()
            $tables[$table] = $columns
        }
        [pscustomobject]@{ Database = $database; Tables = $tables }
    }
    finally {
        $connection.Dispose()
    }
}

$results | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $OutputPath -Encoding UTF8
