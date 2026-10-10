param(
    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",
    [string]$ApiBaseUrl = "http://localhost:9879/api/v1",
    [ValidateSet("", "008", "888", "998", "UFDATA_008_2021", "UFDATA_888_2021", "UFDATA_998_2021")]
    [string]$Database = "",
    [string]$WarehouseCode = "",
    [string]$DepartmentCode = "",
    [ValidateSet("", "Warehouse", "Positions")]
    [string]$Scope = "",
    [string[]]$PositionNames = @(),
    [ValidateSet("", "Keep", "Zero")]
    [string]$ActualQuantityMode = "",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Read-RequiredText {
    param([string]$Prompt, [string]$Value)
    $result = $Value
    while ([string]::IsNullOrWhiteSpace($result)) {
        $result = Read-Host $Prompt
    }
    return $result.Trim()
}

function Read-Choice {
    param(
        [string]$Prompt,
        [hashtable]$Choices
    )
    while ($true) {
        $answer = ([string](Read-Host $Prompt)).Trim()
        foreach ($key in $Choices.Keys) {
            if ($answer.Equals([string]$key, [StringComparison]::OrdinalIgnoreCase)) {
                return $Choices[$key]
            }
        }
        Write-Host "输入无效，请重新选择。" -ForegroundColor Yellow
    }
}

function Unprotect-Text {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    Add-Type -AssemblyName System.Security
    $entropy = [Text.Encoding]::UTF8.GetBytes("MaterialOutboundApi.Settings.v1")
    $protected = [Convert]::FromBase64String($Value)
    $plain = [Security.Cryptography.ProtectedData]::Unprotect(
        $protected,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Text.Encoding]::UTF8.GetString($plain)
}

function Read-DotEnv {
    param([string]$Path)
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $text = ([string]$line).Trim()
        if (-not $text -or $text.StartsWith("#") -or
            -not $text.Contains("=")) {
            continue
        }
        $parts = $text.Split(@("="), 2, [StringSplitOptions]::None)
        $name = $parts[0].Trim()
        $value = $parts[1].Trim()
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$name] = $value
    }
    return $values
}

function New-SqlConnection {
    param(
        [object]$StoredSettings,
        [string]$DatabaseName,
        [string]$Password
    )
    $builder = New-Object Data.SqlClient.SqlConnectionStringBuilder
    $builder["Data Source"] = [string]$StoredSettings.Public.SqlServer
    $builder["Initial Catalog"] = $DatabaseName
    $builder["User ID"] = [string]$StoredSettings.Public.SqlUser
    $builder["Password"] = $Password
    $builder["Encrypt"] = $false
    $builder["TrustServerCertificate"] = $true
    $builder["Connect Timeout"] = 15
    return New-Object Data.SqlClient.SqlConnection $builder.ConnectionString
}

function Read-Table {
    param(
        [Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql,
        [hashtable]$Parameters = @{}
    )
    $command = $Connection.CreateCommand()
    $command.CommandTimeout = 120
    $command.CommandText = $Sql
    foreach ($name in $Parameters.Keys) {
        $parameter = $command.Parameters.Add("@$name", [Data.SqlDbType]::NVarChar, 120)
        $parameter.Value = [string]$Parameters[$name]
    }
    $adapter = New-Object Data.SqlClient.SqlDataAdapter $command
    $table = New-Object Data.DataTable
    [void]$adapter.Fill($table)
    return ,$table
}

function Split-Names {
    param([string[]]$Values)
    return @($Values |
        ForEach-Object { [regex]::Split([string]$_, '[,，;；\r\n]+') } |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ } |
        Select-Object -Unique)
}

function Resolve-PositionSelection {
    param(
        [Data.SqlClient.SqlConnection]$Connection,
        [string]$Warehouse,
        [string[]]$RequestedNames
    )
    $positions = Read-Table $Connection @"
select cPosCode as PositionCode, cPosName as PositionName
from dbo.Position
where cWhCode = @warehouseCode
order by cPosName, cPosCode
"@ @{ warehouseCode = $Warehouse }
    if ($positions.Rows.Count -eq 0) {
        throw "该仓库启用了货位管理，但没有查到可选货位。"
    }

    $names = @(Split-Names $RequestedNames)
    while ($true) {
        if ($names.Count -eq 0) {
            $names = @(Split-Names @((Read-Host `
                "请输入货位名称，多个名称用逗号分隔")))
        }
        if ($names.Count -eq 0) {
            Write-Host "选择按货位盘点时，至少要输入一个货位名称。" -ForegroundColor Yellow
            continue
        }

        $resolved = @()
        $errors = @()
        foreach ($name in $names) {
            $matches = @($positions.Rows | Where-Object {
                ([string]$_.PositionName).Equals(
                    $name,
                    [StringComparison]::OrdinalIgnoreCase)
            })
            if ($matches.Count -eq 0) {
                $errors += "找不到货位名称：$name"
            }
            elseif ($matches.Count -gt 1) {
                $codes = ($matches | ForEach-Object { $_.PositionCode }) -join ", "
                $errors += "货位名称重复：$name（编码：$codes）"
            }
            else {
                $resolved += [pscustomobject]@{
                    PositionName = [string]$matches[0].PositionName
                    PositionCode = [string]$matches[0].PositionCode
                }
            }
        }
        if ($errors.Count -eq 0) { return @($resolved) }
        $errors | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
        $names = @()
    }
}

function Read-Stock {
    param(
        [Data.SqlClient.SqlConnection]$Connection,
        [string]$Warehouse,
        [bool]$UsesPosition,
        [object[]]$SelectedPositions
    )
    $parameters = @{ warehouseCode = $Warehouse }
    if ($UsesPosition) {
        $filter = ""
        if ($SelectedPositions.Count -gt 0) {
            $parameterNames = @()
            for ($index = 0; $index -lt $SelectedPositions.Count; $index++) {
                $name = "position$index"
                $parameters[$name] = [string]$SelectedPositions[$index].PositionCode
                $parameterNames += "@$name"
            }
            $filter = "and a.cPosCode in (" + ($parameterNames -join ",") + ")"
        }
        $sql = @"
select
    a.cWhCode as WarehouseCode,
    wh.cWhName as WarehouseName,
    a.cInvCode as InventoryCode,
    inv.cInvName as InventoryName,
    inv.cInvStd as Specification,
    a.cPosCode as PositionCode,
    ps.cPosName as PositionName,
    cp.cComUnitName as UnitName,
    sum(isnull(a.iQuantity, 0)) as Quantity,
    sum(isnull(a.iNum, 0)) as Number,
    case when isnull(inv.cSTComUnitCode, '') <> ''
              and inv.cSTComUnitCode <> inv.cComUnitCode
         then inv.cSTComUnitCode else '' end as AuxiliaryUnitCode,
    case when isnull(inv.cSTComUnitCode, '') <> ''
              and inv.cSTComUnitCode <> inv.cComUnitCode
         then isnull(aux.iChangRate, 0) else 0 end as ExchangeRate,
    isnull(a.cBatch, '') as BatchCode,
    isnull(a.cFree1, '') as Free1,
    isnull(a.cFree2, '') as Free2,
    isnull(a.cFree3, '') as Free3,
    isnull(a.cFree4, '') as Free4,
    isnull(a.cFree5, '') as Free5,
    isnull(a.cFree6, '') as Free6,
    isnull(a.cFree7, '') as Free7,
    isnull(a.cFree8, '') as Free8,
    isnull(a.cFree9, '') as Free9,
    isnull(a.cFree10, '') as Free10
from dbo.InvPositionSum a
left join dbo.Inventory inv on a.cInvCode = inv.cInvCode
left join dbo.Warehouse wh on a.cWhCode = wh.cWhCode
left join dbo.ComputationUnit cp on inv.cComUnitCode = cp.cComunitCode
left join dbo.ComputationUnit aux on inv.cSTComUnitCode = aux.cComunitCode
left join dbo.Position ps
  on a.cWhCode = ps.cWhCode and a.cPosCode = ps.cPosCode
where a.cWhCode = @warehouseCode
  and isnull(wh.bWhPos, 0) = 1
  and a.iQuantity > 0
  and wh.dWhEndDate is null
  $filter
group by a.cWhCode, wh.cWhName, a.cInvCode, inv.cInvName, inv.cInvStd,
         a.cPosCode, ps.cPosName, cp.cComUnitName, isnull(a.cBatch, ''),
         case when isnull(inv.cSTComUnitCode, '') <> ''
                   and inv.cSTComUnitCode <> inv.cComUnitCode
              then inv.cSTComUnitCode else '' end,
         case when isnull(inv.cSTComUnitCode, '') <> ''
                   and inv.cSTComUnitCode <> inv.cComUnitCode
              then isnull(aux.iChangRate, 0) else 0 end,
         isnull(a.cFree1, ''), isnull(a.cFree2, ''),
         isnull(a.cFree3, ''), isnull(a.cFree4, ''),
         isnull(a.cFree5, ''), isnull(a.cFree6, ''),
         isnull(a.cFree7, ''), isnull(a.cFree8, ''),
         isnull(a.cFree9, ''), isnull(a.cFree10, '')
having sum(isnull(a.iQuantity, 0)) > 0
order by ps.cPosName, a.cInvCode, isnull(a.cBatch, ''),
         isnull(a.cFree1, ''), isnull(a.cFree2, ''),
         isnull(a.cFree3, ''), isnull(a.cFree4, ''),
         isnull(a.cFree5, ''), isnull(a.cFree6, ''),
         isnull(a.cFree7, ''), isnull(a.cFree8, ''),
         isnull(a.cFree9, ''), isnull(a.cFree10, '')
"@
    }
    else {
        $sql = @"
select
    a.cWhCode as WarehouseCode,
    wh.cWhName as WarehouseName,
    a.cInvCode as InventoryCode,
    inv.cInvName as InventoryName,
    inv.cInvStd as Specification,
    cast('' as nvarchar(50)) as PositionCode,
    cast('' as nvarchar(120)) as PositionName,
    cp.cComUnitName as UnitName,
    sum(isnull(a.iQuantity, 0)) as Quantity,
    sum(isnull(a.iNum, 0)) as Number,
    case when isnull(inv.cSTComUnitCode, '') <> ''
              and inv.cSTComUnitCode <> inv.cComUnitCode
         then inv.cSTComUnitCode else '' end as AuxiliaryUnitCode,
    case when isnull(inv.cSTComUnitCode, '') <> ''
              and inv.cSTComUnitCode <> inv.cComUnitCode
         then isnull(aux.iChangRate, 0) else 0 end as ExchangeRate,
    isnull(a.cBatch, '') as BatchCode,
    isnull(a.cFree1, '') as Free1,
    isnull(a.cFree2, '') as Free2,
    isnull(a.cFree3, '') as Free3,
    isnull(a.cFree4, '') as Free4,
    isnull(a.cFree5, '') as Free5,
    isnull(a.cFree6, '') as Free6,
    isnull(a.cFree7, '') as Free7,
    isnull(a.cFree8, '') as Free8,
    isnull(a.cFree9, '') as Free9,
    isnull(a.cFree10, '') as Free10
from dbo.CurrentStock a
left join dbo.Inventory inv on a.cInvCode = inv.cInvCode
left join dbo.Warehouse wh on a.cWhCode = wh.cWhCode
left join dbo.ComputationUnit cp on inv.cComUnitCode = cp.cComunitCode
left join dbo.ComputationUnit aux on inv.cSTComUnitCode = aux.cComunitCode
where a.cWhCode = @warehouseCode
  and isnull(wh.bWhPos, 0) = 0
  and a.iQuantity > 0
  and wh.dWhEndDate is null
group by a.cWhCode, wh.cWhName, a.cInvCode, inv.cInvName, inv.cInvStd,
         cp.cComUnitName, isnull(a.cBatch, ''),
         case when isnull(inv.cSTComUnitCode, '') <> ''
                   and inv.cSTComUnitCode <> inv.cComUnitCode
              then inv.cSTComUnitCode else '' end,
         case when isnull(inv.cSTComUnitCode, '') <> ''
                   and inv.cSTComUnitCode <> inv.cComUnitCode
              then isnull(aux.iChangRate, 0) else 0 end,
         isnull(a.cFree1, ''), isnull(a.cFree2, ''),
         isnull(a.cFree3, ''), isnull(a.cFree4, ''),
         isnull(a.cFree5, ''), isnull(a.cFree6, ''),
         isnull(a.cFree7, ''), isnull(a.cFree8, ''),
         isnull(a.cFree9, ''), isnull(a.cFree10, '')
having sum(isnull(a.iQuantity, 0)) > 0
order by a.cInvCode, isnull(a.cBatch, ''),
         isnull(a.cFree1, ''), isnull(a.cFree2, ''),
         isnull(a.cFree3, ''), isnull(a.cFree4, ''),
         isnull(a.cFree5, ''), isnull(a.cFree6, ''),
         isnull(a.cFree7, ''), isnull(a.cFree8, ''),
         isnull(a.cFree9, ''), isnull(a.cFree10, '')
"@
    }
    return ,(Read-Table $Connection $sql $parameters)
}

function Get-ApiErrorText {
    param([Management.Automation.ErrorRecord]$ErrorRecord)

    $details = [string]$ErrorRecord.ErrorDetails.Message
    if (-not [string]::IsNullOrWhiteSpace($details)) {
        try {
            $payload = $details | ConvertFrom-Json
            if (-not [string]::IsNullOrWhiteSpace([string]$payload.message)) {
                return [string]$payload.message
            }
        }
        catch {
            # The server may return plain text instead of JSON.
        }
        return $details.Trim()
    }

    $exception = $ErrorRecord.Exception
    try {
        $response = $exception.Response
        if ($null -ne $response) {
            $body = ""
            if ($null -ne $response.PSObject.Properties["Content"] -and
                $null -ne $response.Content) {
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
            elseif ($null -ne $response.PSObject.Methods["GetResponseStream"]) {
                $stream = $response.GetResponseStream()
                if ($null -ne $stream) {
                    $reader = New-Object IO.StreamReader $stream
                    try { $body = $reader.ReadToEnd() }
                    finally { $reader.Dispose() }
                }
            }
            if (-not [string]::IsNullOrWhiteSpace($body)) {
                try {
                    $payload = $body | ConvertFrom-Json
                    if (-not [string]::IsNullOrWhiteSpace([string]$payload.message)) {
                        return [string]$payload.message
                    }
                }
                catch {
                    # Preserve non-JSON response text below.
                }
                return $body.Trim()
            }
        }
    }
    catch {
        # Fall through to the exception message.
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$exception.Message)) {
        return [string]$exception.Message
    }
    $fallback = [string]$ErrorRecord
    if (-not [string]::IsNullOrWhiteSpace($fallback)) { return $fallback }
    return "接口没有返回错误详情，请查看服务器 API 日志。"
}

function Invoke-InventoryCountApi {
    param(
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body
    )
    $json = $Body | ConvertTo-Json -Depth 12 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    try {
        return Invoke-RestMethod -Method Post -Uri $Uri -Headers $Headers `
            -ContentType "application/json; charset=utf-8" -Body $bytes `
            -TimeoutSec 300
    }
    catch {
        throw "接口调用失败：$(Get-ApiErrorText $_)"
    }
}

if (Test-Path -LiteralPath $SettingsPath -PathType Leaf) {
    $stored = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $sqlPassword = Unprotect-Text ([string]$stored.SqlPasswordProtected)
    $apiKey = Unprotect-Text ([string]$stored.ApiKeyProtected)
}
else {
    $workspaceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $environmentPath = Join-Path $workspaceRoot ".env.local"
    if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
        throw "既找不到 IIS 配置文件，也找不到仓库根目录 .env.local。"
    }
    $environment = Read-DotEnv $environmentPath
    $sqlServer = [string]$environment["DB_SERVER"]
    $sqlPort = [string]$environment["DB_PORT"]
    if ($sqlPort -and $sqlPort -ne "1433" -and
        -not $sqlServer.Contains(",")) {
        $sqlServer = $sqlServer + "," + $sqlPort
    }
    $stored = [pscustomobject]@{
        Public = [pscustomobject]@{
            SqlServer = $sqlServer
            SqlUser = [string]$environment["DB_USER"]
        }
    }
    $sqlPassword = [string]$environment["DB_PASSWORD"]
    $apiKey = [string]$environment["MATERIAL_OUTBOUND_API_KEY"]
    $configuredApiBaseUrl = [string]$environment[
        "MATERIAL_OUTBOUND_API_BASE_URL"]
    if ($configuredApiBaseUrl) {
        $ApiBaseUrl = $configuredApiBaseUrl.TrimEnd('/')
    }
}
if ([string]::IsNullOrWhiteSpace($sqlPassword) -or
    [string]::IsNullOrWhiteSpace($apiKey)) {
    throw "没有找到可用的 SQL 密码或盘点 API Key 配置。"
}

if ([string]::IsNullOrWhiteSpace($Database)) {
    $Database = Read-Choice "请选择账套：1=008，2=888，3=998（输入1、2或3）" @{
        "1" = "UFDATA_008_2021"
        "2" = "UFDATA_888_2021"
        "3" = "UFDATA_998_2021"
        "008" = "UFDATA_008_2021"
        "888" = "UFDATA_888_2021"
        "998" = "UFDATA_998_2021"
    }
}
elseif ($Database -eq "008") { $Database = "UFDATA_008_2021" }
elseif ($Database -eq "888") { $Database = "UFDATA_888_2021" }
elseif ($Database -eq "998") { $Database = "UFDATA_998_2021" }

$WarehouseCode = Read-RequiredText "请输入仓库编码" $WarehouseCode
$DepartmentCode = Read-RequiredText "请输入部门编码" $DepartmentCode
$connection = New-SqlConnection $stored $Database $sqlPassword
try {
    $connection.Open()
    $warehouse = Read-Table $connection @"
select top (1)
    cWhCode as WarehouseCode,
    cWhName as WarehouseName,
    cast(isnull(bWhPos, 0) as bit) as UsesPosition
from dbo.Warehouse
where cWhCode = @warehouseCode and dWhEndDate is null
"@ @{ warehouseCode = $WarehouseCode }
    if ($warehouse.Rows.Count -eq 0) {
        throw "仓库不存在或已经停用：$WarehouseCode"
    }
    $usesPosition = [bool]$warehouse.Rows[0].UsesPosition
    Write-Host ""
    Write-Host ("账套：{0}" -f $Database) -ForegroundColor Cyan
    Write-Host ("仓库：{0} {1}" -f $WarehouseCode, $warehouse.Rows[0].WarehouseName)
    Write-Host ("部门：{0}（创建前由 API 校验）" -f $DepartmentCode)
    Write-Host ("货位管理：{0}" -f $(if ($usesPosition) { "是" } else { "否" }))

    $selectedPositions = @()
    if ($usesPosition) {
        if ([string]::IsNullOrWhiteSpace($Scope)) {
            $Scope = Read-Choice "请选择盘点范围：1=整个仓库，2=指定货位" @{
                "1" = "Warehouse"
                "2" = "Positions"
            }
        }
        if ($Scope -eq "Positions") {
            $selectedPositions = @(Resolve-PositionSelection `
                $connection $WarehouseCode $PositionNames)
        }
        elseif ($Scope -ne "Warehouse") {
            throw "启用货位管理的仓库必须明确选择整个仓库或指定货位。"
        }
    }
    else {
        if ($Scope -eq "Positions") {
            throw "仓库 $WarehouseCode 未启用货位管理，不能按货位盘点，请重新选择整仓盘点。"
        }
        $Scope = "Warehouse"
        Write-Host "该仓库未启用货位管理，将按整个仓库盘点。"
    }

    $stock = Read-Stock $connection $WarehouseCode $usesPosition $selectedPositions
}
finally {
    $connection.Dispose()
}

if ($stock.Rows.Count -eq 0) {
    throw "所选盘点范围没有大于 0 的库存。"
}
$totalQuantity = ($stock.Rows | Measure-Object -Property Quantity -Sum).Sum
Write-Host ""
Write-Host ("查到 {0} 条库存，合计数量 {1:N6}" -f `
    $stock.Rows.Count, $totalQuantity) -ForegroundColor Green

if ($selectedPositions.Count -gt 1) {
    foreach ($position in $selectedPositions) {
        $positionRows = @($stock.Rows | Where-Object {
            [string]$_.PositionCode -eq [string]$position.PositionCode
        })
        $positionQuantity = 0
        if ($positionRows.Count -gt 0) {
            $positionMeasure = $positionRows |
                Measure-Object -Property Quantity -Sum
            if ($null -ne $positionMeasure.Sum) {
                $positionQuantity = $positionMeasure.Sum
            }
        }
        Write-Host ("货位 {0}：查到 {1} 条库存，合计数量 {2:N6}" -f `
            $position.PositionName, $positionRows.Count, $positionQuantity) `
            -ForegroundColor Green
    }
}

if ([string]::IsNullOrWhiteSpace($ActualQuantityMode)) {
    $ActualQuantityMode = Read-Choice `
        "是否把全部盘点数清零为 0？Y=清零，N=按当前库存带入" @{
            "Y" = "Zero"; "YES" = "Zero"; "是" = "Zero"
            "N" = "Keep"; "NO" = "Keep"; "否" = "Keep"
        }
}
$zeroActualQuantity = $ActualQuantityMode -eq "Zero"
$allLines = @($stock.Rows | ForEach-Object {
    $bookQuantity = [decimal]$_.Quantity
    $bookNumber = [decimal]$_.Number
    $auxiliaryUnitCode = [string]$_.AuxiliaryUnitCode
    $exchangeRate = [decimal]$_.ExchangeRate
    [pscustomobject]@{
        inventoryCode = [string]$_.InventoryCode
        batchCode = if ([string]::IsNullOrWhiteSpace([string]$_.BatchCode)) {
            $null
        } else { [string]$_.BatchCode }
        positionCode = if ([string]::IsNullOrWhiteSpace([string]$_.PositionCode)) {
            $null
        } else { [string]$_.PositionCode }
        freeFields = [pscustomobject]@{
            free1 = if ([string]::IsNullOrWhiteSpace([string]$_.Free1)) { $null } else { [string]$_.Free1 }
            free2 = if ([string]::IsNullOrWhiteSpace([string]$_.Free2)) { $null } else { [string]$_.Free2 }
            free3 = if ([string]::IsNullOrWhiteSpace([string]$_.Free3)) { $null } else { [string]$_.Free3 }
            free4 = if ([string]::IsNullOrWhiteSpace([string]$_.Free4)) { $null } else { [string]$_.Free4 }
            free5 = if ([string]::IsNullOrWhiteSpace([string]$_.Free5)) { $null } else { [string]$_.Free5 }
            free6 = if ([string]::IsNullOrWhiteSpace([string]$_.Free6)) { $null } else { [string]$_.Free6 }
            free7 = if ([string]::IsNullOrWhiteSpace([string]$_.Free7)) { $null } else { [string]$_.Free7 }
            free8 = if ([string]::IsNullOrWhiteSpace([string]$_.Free8)) { $null } else { [string]$_.Free8 }
            free9 = if ([string]::IsNullOrWhiteSpace([string]$_.Free9)) { $null } else { [string]$_.Free9 }
            free10 = if ([string]::IsNullOrWhiteSpace([string]$_.Free10)) { $null } else { [string]$_.Free10 }
        }
        bookQuantity = $bookQuantity
        actualQuantity = if ($zeroActualQuantity) { 0 } else { $bookQuantity }
        auxiliaryUnitCode = if ([string]::IsNullOrWhiteSpace($auxiliaryUnitCode)) {
            $null
        } else { $auxiliaryUnitCode }
        exchangeRate = if ($exchangeRate -gt 0) { $exchangeRate } else { $null }
        bookNumber = if ($exchangeRate -gt 0) { $bookNumber } else { $null }
        actualNumber = if ($exchangeRate -gt 0) {
            if ($zeroActualQuantity) { 0 } else { $bookNumber }
        } else { $null }
    }
})

$batchSize = 1000
$batchCount = [Math]::Ceiling($allLines.Count / [double]$batchSize)
if ($batchCount -gt 1) {
    Write-Host ("库存超过 1000 条，将创建 {0} 张盘点单。" -f $batchCount) `
        -ForegroundColor Yellow
}

$timestamp = Get-Date -Format "yyyyMMddHHmmss"
$scopeText = if ($selectedPositions.Count -gt 0) {
    "货位：" + (($selectedPositions | ForEach-Object { $_.PositionName }) -join "、")
} else { "整个仓库" }
$modeText = if ($zeroActualQuantity) { "盘点数清零" } else { "盘点数按库存带入" }
$headers = @{
    "X-API-Key" = $apiKey
    "X-U8-Database" = $Database
}
$requests = @()
for ($batchIndex = 0; $batchIndex -lt $batchCount; $batchIndex++) {
    $start = $batchIndex * $batchSize
    $lines = @($allLines | Select-Object -Skip $start -First $batchSize)
    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $lines[$lineIndex] | Add-Member -NotePropertyName lineNo `
            -NotePropertyValue ($lineIndex + 1)
    }
    $requestNo = "count-$timestamp-{0:D3}" -f ($batchIndex + 1)
    $body = [ordered]@{
        externalRequestNo = $requestNo
        voucherDate = (Get-Date).ToString("yyyy-MM-dd")
        countDate = (Get-Date).ToString("yyyy-MM-dd")
        warehouseCode = $WarehouseCode
        departmentCode = $DepartmentCode
        countType = "普通仓库盘点"
        memo = "脚本盘点；$scopeText；$modeText；第$($batchIndex + 1)/${batchCount}张"
        autoVerify = $false
        lines = $lines
    }
    Write-Host ("正在校验第 {0}/{1} 批，共 {2} 行……" -f `
        ($batchIndex + 1), $batchCount, $lines.Count)
    $validation = Invoke-InventoryCountApi `
        "$ApiBaseUrl/general-inventory-count-orders/validate" $headers $body
    if (-not $validation.success -or -not $validation.data.valid) {
        throw "第 $($batchIndex + 1) 批校验未通过。"
    }
    if ($validation.data.warnings.Count -gt 0) {
        Write-Host "校验警告：" -ForegroundColor Yellow
        $validation.data.warnings | ForEach-Object {
            Write-Host "- $_" -ForegroundColor Yellow
        }
    }
    $requests += [pscustomobject]@{
        RequestNo = $requestNo
        Body = $body
        LineCount = $lines.Count
    }
}

Write-Host ""
Write-Host "全部批次校验通过。" -ForegroundColor Green
Write-Host ("盘点范围：{0}；盘点数：{1}；预计单据数：{2}" -f `
    $scopeText, $modeText, $batchCount)
if ($DryRun) {
    Write-Host "DryRun 已启用，不创建盘点单。" -ForegroundColor Cyan
    exit 0
}

$confirmation = ([string](Read-Host `
    "确认创建未审核盘点单？请输入 CREATE 继续")).Trim()
if (-not $confirmation.Equals("CREATE", [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "已取消，没有创建盘点单。" -ForegroundColor Yellow
    exit 0
}

$created = @()
foreach ($request in $requests) {
    $createHeaders = @{
        "X-API-Key" = $apiKey
        "X-U8-Database" = $Database
        "Idempotency-Key" = $request.RequestNo
    }
    $response = Invoke-InventoryCountApi `
        "$ApiBaseUrl/general-inventory-count-orders" `
        $createHeaders $request.Body
    $created += [pscustomobject]@{
        VoucherCode = $response.data.voucherCode
        VoucherId = $response.data.voucherId
        Lines = $request.LineCount
        Verified = $response.data.isVerified
    }
}

Write-Host ""
Write-Host "盘点单创建完成（均未自动审核）：" -ForegroundColor Green
$created | Format-Table -AutoSize
