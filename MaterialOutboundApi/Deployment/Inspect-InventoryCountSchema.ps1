param(
    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",
    [string]$OutputPath = "D:\MupiInventoryCountResearch\inventory-count-schema.json"
)

$ErrorActionPreference = "Stop"
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

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

function Read-Table {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql
    )

    $command = $Connection.CreateCommand()
    $command.CommandTimeout = 30
    $command.CommandText = $Sql
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
    $table = New-Object System.Data.DataTable
    [void]$adapter.Fill($table)
    $rows = foreach ($row in $table.Rows) {
        $value = [ordered]@{}
        foreach ($column in $table.Columns) {
            $cell = $row[$column.ColumnName]
            $value[$column.ColumnName] = if ($cell -is [DBNull]) { $null } else { $cell }
        }
        [pscustomobject]$value
    }
    return @($rows)
}

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
        $columns = Read-Table $connection @"
select t.name as TableName, c.column_id as ColumnId, c.name as ColumnName,
       ty.name as SqlType, c.max_length as MaxLength, c.precision as Precision,
       c.scale as Scale, c.is_nullable as IsNullable,
       dc.definition as DefaultDefinition
from sys.tables t
join sys.columns c on c.object_id=t.object_id
join sys.types ty on ty.user_type_id=c.user_type_id
left join sys.default_constraints dc on dc.object_id=c.default_object_id
where t.name in (N'CheckVouch',N'CheckVouchs')
order by t.name,c.column_id
"@
        $metadataColumns = Read-Table $connection @"
select t.name as TableName, c.column_id as ColumnId, c.name as ColumnName,
       ty.name as SqlType, c.max_length as MaxLength
from sys.tables t
join sys.columns c on c.object_id=t.object_id
join sys.types ty on ty.user_type_id=c.user_type_id
where t.name in (N'VoucherTemplates',N'Vouchers',N'VoucherNumber')
order by t.name,c.column_id
"@
        $headers = Read-Table $connection @"
select top (5) * from dbo.CheckVouch order by ID desc
"@
        $lines = Read-Table $connection @"
select top (50) s.*
from dbo.CheckVouchs s
join (select top (5) ID from dbo.CheckVouch order by ID desc) h on h.ID=s.ID
order by s.ID desc
"@
        [pscustomobject]@{
            Database = $database
            Columns = $columns
            MetadataColumns = $metadataColumns
            RecentHeaders = $headers
            RecentLines = $lines
        }
    }
    catch {
        [pscustomobject]@{
            Database = $database
            Error = $_.Exception.Message
        }
    }
    finally {
        $connection.Dispose()
    }
}

$results | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $OutputPath -Encoding UTF8
