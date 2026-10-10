Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-XlsxXml($Archive, [string]$Name) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry -or $entry.Length -gt 33554432) { throw "Excel 结构缺失或过大：$Name" }
    $stream = $entry.Open()
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 33554432
    $reader = [Xml.XmlReader]::Create($stream, $settings)
    try { $doc = New-Object Xml.XmlDocument; $doc.XmlResolver=$null; $doc.Load($reader); return ,$doc }
    finally { $reader.Dispose(); $stream.Dispose() }
}
function New-XlsxNamespace($Document) {
    $ns = New-Object Xml.XmlNamespaceManager $Document.NameTable
    $ns.AddNamespace('m','http://schemas.openxmlformats.org/spreadsheetml/2006/main')
    $ns.AddNamespace('r','http://schemas.openxmlformats.org/officeDocument/2006/relationships')
    return ,$ns
}
function Get-CellText($Cell, $Namespace, [string[]]$SharedStrings) {
    if ($null -ne $Cell.SelectSingleNode('m:f',$Namespace)) { throw '导入数据不能使用公式，请粘贴为值。' }
    $type=$Cell.GetAttribute('t')
    if ($type -eq 'inlineStr') { return (@($Cell.SelectNodes('m:is//m:t',$Namespace) | ForEach-Object InnerText) -join '') }
    $value=$Cell.SelectSingleNode('m:v',$Namespace)
    if ($null -eq $value) { return '' }
    if ($type -eq 's') {
        $index=0
        if (![int]::TryParse($value.InnerText,[ref]$index) -or $index -lt 0 -or $index -ge $SharedStrings.Count) { throw 'Excel 文本索引无效。' }
        return $SharedStrings[$index]
    }
    if ($type -in @('e','b','d')) { throw '导入单元格类型不正确。' }
    return $value.InnerText
}
function Convert-CountDecimal([string]$Text,[string]$Label,[int]$Row,[int]$Scale) {
    $number=[decimal]0
    if ([string]::IsNullOrWhiteSpace($Text) -or ![decimal]::TryParse($Text,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$number) -or $number -lt 0 -or [decimal]::Round($number,$Scale) -ne $number -or $number -gt 999999999999999999.9999999999d) {
        throw "Excel 第 $Row 行${Label}须为非负数，最多 $Scale 位小数，不能为空。"
    }
    return $number
}
function Read-InventoryCountExcel([string]$Path) {
    if ([IO.Path]::GetExtension($Path) -ine '.xlsx') { throw '请使用 .xlsx 文件。' }
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
    try {
        $workbook=Read-XlsxXml $archive 'xl/workbook.xml'
        $ns=New-XlsxNamespace $workbook
        $sheet=$workbook.SelectSingleNode("/m:workbook/m:sheets/m:sheet[@name='盘点导入']",$ns)
        if ($null -eq $sheet) { throw '找不到“盘点导入”工作表。' }
        $relationships=Read-XlsxXml $archive 'xl/_rels/workbook.xml.rels'
        $rid=$sheet.GetAttribute('id','http://schemas.openxmlformats.org/officeDocument/2006/relationships')
        $relationship=@($relationships.DocumentElement.ChildNodes | Where-Object { $_.GetAttribute('Id') -ceq $rid })
        if ($relationship.Count -ne 1 -or $relationship[0].GetAttribute('TargetMode') -eq 'External') { throw 'Excel 工作表关系无效。' }
        $uri=New-Object Uri ([uri]'http://xlsx/xl/workbook.xml'),$relationship[0].GetAttribute('Target')
        $sheetPath=[Uri]::UnescapeDataString($uri.AbsolutePath.TrimStart('/'))
        if (!$sheetPath.StartsWith('xl/') -or !$sheetPath.EndsWith('.xml')) { throw 'Excel 工作表路径无效。' }
        $strings=@()
        if ($null -ne $archive.GetEntry('xl/sharedStrings.xml')) {
            $shared=Read-XlsxXml $archive 'xl/sharedStrings.xml'; $sharedNs=New-XlsxNamespace $shared
            $strings=@($shared.SelectNodes('/m:sst/m:si',$sharedNs) | ForEach-Object { @($_.SelectNodes('.//m:t',$sharedNs) | ForEach-Object InnerText) -join '' })
        }
        $document=Read-XlsxXml $archive $sheetPath; $ns=New-XlsxNamespace $document
        $rows=@($document.SelectNodes('/m:worksheet/m:sheetData/m:row',$ns))
        if ($rows.Count -gt 100001) { throw '一次最多导入 100000 行。' }
        $header=@{}; $names=@{}
        $allowed=@('分单标识','仓库编码','料号','盘点数量','批号','货位名称','单价','部门编码') + @(1..10 | ForEach-Object { '自由项'+$_ })
        $headerRow=@($rows | Where-Object { $_.GetAttribute('r') -eq '1' })
        if ($headerRow.Count -ne 1) { throw '第一行必须是列标题。' }
        foreach ($cell in $headerRow[0].SelectNodes('m:c',$ns)) {
            $name=(Get-CellText $cell $ns $strings).Trim()
            if (!$name) { continue }
            if ($name -notin $allowed -or $names.ContainsKey($name)) { throw "列标题未知或重复：$name" }
            $column=$cell.GetAttribute('r') -replace '\d+$',''
            if ($column -notmatch '^[A-Z]+$' -or $header.ContainsKey($column)) { throw 'Excel 列引用无效。' }
            $header[$column]=$name; $names[$name]=$true
        }
        foreach ($name in @('仓库编码','料号','盘点数量','批号','货位名称','单价')) { if (!$names.ContainsKey($name)) { throw "缺少列：$name" } }
        $result=New-Object 'Collections.Generic.List[object]'
        foreach ($row in $rows) {
            $rowNo=[int]$row.GetAttribute('r'); if ($rowNo -eq 1) { continue }
            $values=@{}; $types=@{}
            foreach ($cell in $row.SelectNodes('m:c',$ns)) {
                $text=(Get-CellText $cell $ns $strings).Trim(); if (!$text) { continue }
                $reference=$cell.GetAttribute('r')
                if ($reference -notmatch '^([A-Z]+)([0-9]+)$' -or [int]$Matches[2] -ne $rowNo) { throw 'Excel 单元格引用无效。' }
                $column=$Matches[1]
                if (!$header.ContainsKey($column)) { throw "Excel 第 $rowNo 行存在无标题的数据。" }
                $name=$header[$column]
                if ($values.ContainsKey($name)) { throw "Excel 第 $rowNo 行单元格重复。" }
                $values[$name]=$text; $types[$name]=$cell.GetAttribute('t')
            }
            if ($values.Count -eq 0) { continue }
            foreach ($name in $allowed) { if (!$values.ContainsKey($name)) { $values[$name]='' } }
            foreach ($name in @('仓库编码','料号')) { if (!$values[$name]) { throw "Excel 第 $rowNo 行缺少$name。" } }
            foreach ($name in $allowed | Where-Object { $_ -notin @('盘点数量','单价') }) {
                if ($values[$name] -and $types[$name] -notin @('s','inlineStr','str')) { throw "Excel 第 $rowNo 行$name 请设置为文本后重新填写，避免丢失前导零。" }
            }
            foreach ($limit in @{仓库编码=10;料号=60;批号=60;货位名称=120;分单标识=60;部门编码=12}.GetEnumerator()) {
                if ($values[$limit.Key].Length -gt $limit.Value) { throw "Excel 第 $rowNo 行$($limit.Key)过长。" }
            }
            $quantity=Convert-CountDecimal $values['盘点数量'] '盘点数量' $rowNo 6
            $price=Convert-CountDecimal $values['单价'] '单价' $rowNo 10
            $free=[ordered]@{}
            foreach ($n in 1..10) { $v=$values['自由项'+$n]; if ($v.Length -gt 60) { throw "Excel 第 $rowNo 行自由项$n 过长。" }; $free['free'+$n]=$v }
            $result.Add([pscustomobject]@{ExcelRow=$rowNo;Group=$values['分单标识'];Warehouse=$values['仓库编码'];Inventory=$values['料号'];Quantity=$quantity;Batch=$values['批号'];PositionName=$values['货位名称'];Price=$price;Department=$values['部门编码'];Free=$free;PositionCode=''})
        }
        if ($result.Count -eq 0) { throw '没有可导入的盘点数据。' }
        return $result.ToArray()
    } finally { $archive.Dispose() }
}
function Read-CountTable($Connection,[string]$Sql,[hashtable]$Parameters) {
    $command=$Connection.CreateCommand(); $command.CommandTimeout=120; $command.CommandText=$Sql
    foreach ($name in $Parameters.Keys) { [void]$command.Parameters.Add('@'+$name,[Data.SqlDbType]::NVarChar,120); $command.Parameters['@'+$name].Value=[string]$Parameters[$name] }
    $adapter=New-Object Data.SqlClient.SqlDataAdapter $command
    try { $table=New-Object Data.DataTable; [void]$adapter.Fill($table); return ,$table }
    finally { $adapter.Dispose(); $command.Dispose() }
}
function Resolve-InventoryCountRows([object[]]$Rows,$Connection) {
    $warehouses=@{}; $inventories=@{}; $positions=@{}
    foreach ($row in $Rows) {
        if (!$warehouses.ContainsKey($row.Warehouse)) {
            $table=Read-CountTable $Connection 'select cWhCode,bWhPos from dbo.Warehouse where cWhCode=@code and dWhEndDate is null' @{code=$row.Warehouse}
            if ($table.Rows.Count -ne 1) { throw "仓库不存在或已停用：$($row.Warehouse)" }
            $warehouses[$row.Warehouse]=($table.Rows[0].bWhPos -isnot [DBNull] -and [bool]$table.Rows[0].bWhPos)
        }
        $usesPosition=$warehouses[$row.Warehouse]
        if ($usesPosition -and !$row.PositionName) { throw "Excel 第 $($row.ExcelRow) 行：该仓库管理货位，必须填写货位名称。" }
        if (!$usesPosition -and $row.PositionName) { throw "Excel 第 $($row.ExcelRow) 行：该仓库不管理货位，请清空货位名称。" }
        if ($row.PositionName) {
            $key=@($row.Warehouse,$row.PositionName) | ConvertTo-Json -Compress
            if (!$positions.ContainsKey($key)) {
                $table=Read-CountTable $Connection 'select cPosCode from dbo.Position where cWhCode=@warehouse and cPosName=@name' @{warehouse=$row.Warehouse;name=$row.PositionName}
                if ($table.Rows.Count -ne 1) { throw "Excel 第 $($row.ExcelRow) 行：货位名称不存在或重名：$($row.PositionName)" }
                $positions[$key]=[string]$table.Rows[0].cPosCode
            }
            $row.PositionCode=$positions[$key]
        }
        if (!$inventories.ContainsKey($row.Inventory)) {
            $table=Read-CountTable $Connection 'select cInvCode,bInvBatch,bFree1,bFree2,bFree3,bFree4,bFree5,bFree6,bFree7,bFree8,bFree9,bFree10 from dbo.Inventory where cInvCode=@code' @{code=$row.Inventory}
            if ($table.Rows.Count -ne 1) { throw "Excel 第 $($row.ExcelRow) 行：料号不存在：$($row.Inventory)" }
            $inventories[$row.Inventory]=$table.Rows[0]
        }
        $inventory=$inventories[$row.Inventory]
        if ($inventory.bInvBatch -isnot [DBNull] -and [bool]$inventory.bInvBatch -and !$row.Batch) { throw "Excel 第 $($row.ExcelRow) 行：批次管理存货必须填写批号。" }
        $missing=@(1..10 | Where-Object { $inventory['bFree'+$_] -isnot [DBNull] -and [bool]$inventory['bFree'+$_] -and !$row.Free['free'+$_] })
        if ($missing.Count -gt 0) {
            $parameters=@{warehouse=$row.Warehouse;inventory=$row.Inventory;batch=$row.Batch}
            $freeColumns=@(1..10 | ForEach-Object { "isnull(cFree$_,'') as free$_" }) -join ','
            $filters=New-Object 'Collections.Generic.List[string]'
            foreach ($n in 1..10) { if ($row.Free['free'+$n]) { $filters.Add("and isnull(cFree$n,'')=@free$n"); $parameters['free'+$n]=$row.Free['free'+$n] } }
            $source='dbo.CurrentStock'; $positionFilter=''
            if ($usesPosition) { $source='dbo.InvPositionSum'; $positionFilter='and cPosCode=@position'; $parameters.position=$row.PositionCode }
            $sql="select distinct $freeColumns from $source where cWhCode=@warehouse and cInvCode=@inventory and isnull(cBatch,'')=@batch $positionFilter " + ($filters -join ' ')
            $matches=Read-CountTable $Connection $sql $parameters
            if ($matches.Rows.Count -ne 1) { throw "Excel 第 $($row.ExcelRow) 行：存货等级/自由项无法唯一确定，请增加自由项列填写后导入。" }
            foreach ($n in 1..10) { if (!$row.Free['free'+$n]) { $row.Free['free'+$n]=[string]$matches.Rows[0]['free'+$n] } }
            foreach ($n in $missing) { if (!$row.Free['free'+$n]) { throw "Excel 第 $($row.ExcelRow) 行：缺少自由项$n，请补充。" } }
        }
    }
    return $Rows
}
function New-InventoryCountRequests([object[]]$Rows,[string]$Department,[string]$Date,[string]$ImportId) {
    $groups=[ordered]@{}; $seen=@{}
    foreach ($row in $Rows) {
        $departmentCode=if ($row.Department) { $row.Department } else { $Department }
        if (!$departmentCode -or $departmentCode.Length -gt 12) { throw "Excel 第 $($row.ExcelRow) 行需要有效部门编码。" }
        $groupKey=@($row.Group,$row.Warehouse,$departmentCode) | ConvertTo-Json -Compress
        # A duplicate must be caught before splitting into 1000-row vouchers.
        $identity=@($row.Warehouse,$row.Inventory,$row.Batch,$row.PositionCode) + @(1..10 | ForEach-Object { $row.Free['free'+$_] })
        $duplicateKey=@($groupKey,($identity | ConvertTo-Json -Compress)) | ConvertTo-Json -Compress
        if ($seen.ContainsKey($duplicateKey)) { throw "Excel 第 $($row.ExcelRow) 行与第 $($seen[$duplicateKey]) 行重复，请合并盘点数量后导入。" }
        $seen[$duplicateKey]=$row.ExcelRow
        if (!$groups.Contains($groupKey)) { $groups[$groupKey]=New-Object 'Collections.Generic.List[object]' }
        $groups[$groupKey].Add($row)
    }
    $requests=New-Object 'Collections.Generic.List[object]'
    foreach ($list in $groups.Values) {
        for ($start=0; $start -lt $list.Count; $start+=1000) {
            $lines=New-Object 'Collections.Generic.List[object]'; $sourceRows=@()
            foreach ($row in @($list.ToArray() | Select-Object -Skip $start -First 1000)) {
                $lines.Add([pscustomobject][ordered]@{lineNo=$lines.Count+1;inventoryCode=$row.Inventory;actualQuantity=$row.Quantity;batchCode=$row.Batch;positionCode=$row.PositionCode;planUnitPrice=$row.Price;freeFields=[pscustomobject]$row.Free})
                $sourceRows+=$row.ExcelRow
            }
            $first=$list[0]; $dep=if ($first.Department) { $first.Department } else { $Department }
            $requestNo='excel-count-'+$ImportId.Substring(0,32)+'-'+($requests.Count+1).ToString('D4')
            $body=[pscustomobject][ordered]@{externalRequestNo=$requestNo;voucherDate=$Date;countDate=$Date;warehouseCode=$first.Warehouse;departmentCode=$dep;countType='普通仓库盘点';memo='Excel盘点导入';autoVerify=$false;lines=$lines.ToArray()}
            $requests.Add([pscustomobject]@{Group=$first.Group;Warehouse=$first.Warehouse;Department=$dep;SourceRows=$sourceRows;RequestNo=$requestNo;Body=$body;BodyJson=($body | ConvertTo-Json -Depth 25 -Compress);LineCount=$lines.Count;Status='Ready';VoucherCode='';VoucherId='';Error=''})
        }
    }
    return $requests.ToArray()
}
function Write-CountJson($Value,[string]$Path) {
    $temporary=$Path+'.tmp'
    $Value | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function Get-CountHash([string]$Value) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Get-CountApiError($Record) {
    if ($Record.ErrorDetails -and $Record.ErrorDetails.Message) {
        try { $payload=$Record.ErrorDetails.Message | ConvertFrom-Json; if ($payload.message) { return [string]$payload.message } } catch {}
        return [string]$Record.ErrorDetails.Message
    }
    return [string]$Record.Exception.Message
}
function Invoke-CountApi([string]$Uri,[hashtable]$Headers,$Body) {
    # Preserve the exact decimal-bearing payload across restart; ConvertFrom-Json
    # in Windows PowerShell 5.1 would otherwise round numbers through Double.
    $json=if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 25 -Compress }
    $bytes=[Text.Encoding]::UTF8.GetBytes($json)
    try {
        $response=Invoke-RestMethod -Method Post -Uri $Uri -Headers $Headers -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 300
        if (!$response.success -or $null -eq $response.data) { throw '接口没有返回有效结果。' }
        return $response.data
    } catch { throw (Get-CountApiError $_) }
}
function Invoke-CountImportSave($State,[string]$StatePath,[string]$ApiBaseUrl,[hashtable]$Headers) {
    foreach ($request in @($State.Requests)) {
        if ($request.Status -eq 'Created') { continue }
        if ($request.Status -ne 'Ready') { throw '存在结果待确认的单据，请先在 U8 核对，不能自动重试。' }
        $request.Status='Sending'; Write-CountJson $State $StatePath
        try {
            $createHeaders=$Headers.Clone(); $createHeaders['Idempotency-Key']=$request.RequestNo
            $result=Invoke-CountApi ($ApiBaseUrl+'/general-inventory-count-orders') $createHeaders $request.BodyJson
            if ([string]::IsNullOrWhiteSpace([string]$result.voucherCode) -or [string]::IsNullOrWhiteSpace([string]$result.voucherId) -or $result.isVerified -ne $false) { throw '接口返回的单号、ID或未审核状态异常，请在 U8 核对。' }
            $request.VoucherCode=[string]$result.voucherCode; $request.VoucherId=[string]$result.voucherId; $request.Status='Created'; $request.Error=''
            Write-CountJson $State $StatePath
            Write-Host ('创建成功：{0}，{1} 行，未审核' -f $request.VoucherCode,$request.LineCount)
        } catch {
            $request.Status='Unknown'; $request.Error=[string]$_.Exception.Message; Write-CountJson $State $StatePath
            throw "创建未完成：$($request.Error)。已停止后续单据；请先核对 U8 和任务日志，不要重新另建导入批次。"
        }
    }
}
Export-ModuleMember -Function Read-InventoryCountExcel,Resolve-InventoryCountRows,New-InventoryCountRequests,Write-CountJson,Get-CountHash,Invoke-CountApi,Invoke-CountImportSave
