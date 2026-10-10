param([string]$TemplatePath='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'Deployment\InventoryCountExcel.psm1') -Force -DisableNameChecking -PassThru
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('InventoryExcelTests-'+[guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testRoot)
function Assert($Condition,[string]$Message) { if (!$Condition) { throw $Message } }
function Deny([scriptblock]$Action,[string]$Message) { $denied=$false;try { & $Action | Out-Null } catch { $denied=$true };Assert $denied $Message }
function Write-Fixture([string]$Name,[object[]]$Rows,[string]$Mode='normal') {
    $path=Join-Path $testRoot ($Name+'.xlsx')
    $archive=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
    try {
        $headers=@('分单标识','仓库编码','料号','盘点数量','批号','货位名称','单价','部门编码')
        if ($Mode -eq 'duplicateHeader') { $headers[2]='仓库编码' }
        $all=@(,$headers)+$Rows
        $xml=New-Object Text.StringBuilder
        [void]$xml.Append('<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>')
        for ($r=0;$r -lt $all.Count;$r++) {
            [void]$xml.Append('<row r="'+($r+1)+'">')
            for ($c=0;$c -lt $all[$r].Count;$c++) {
                $value=[Security.SecurityElement]::Escape([string]$all[$r][$c]);$reference=[char](65+$c)+[string]($r+1)
                if ($r -gt 0 -and ($c -in @(3,6) -or ($Mode -eq 'numericCode' -and $c -eq 1))) {
                    $formula=if ($Mode -eq 'formula' -and $c -eq 3) { '<f>1+1</f>' } else { '' }
                    [void]$xml.Append('<c r="'+$reference+'">'+$formula+'<v>'+$value+'</v></c>')
                } else { [void]$xml.Append('<c r="'+$reference+'" t="inlineStr"><is><t>'+$value+'</t></is></c>') }
            }
            [void]$xml.Append('</row>')
        }
        [void]$xml.Append('</sheetData></worksheet>')
        $parts=@{
            'xl/workbook.xml'='<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="盘点导入" sheetId="1" r:id="rId1"/></sheets></workbook>'
            'xl/_rels/workbook.xml.rels'='<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet7.xml"/></Relationships>'
            'xl/worksheets/sheet7.xml'=$xml.ToString()
        }
        foreach ($part in $parts.GetEnumerator()) {
            $writer=New-Object IO.StreamWriter ($archive.CreateEntry($part.Key).Open()),(New-Object Text.UTF8Encoding($false))
            try { $writer.Write($part.Value) } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
    return $path
}
$valid=@('PD01','001','0000123','0','000001','1101','0','0002')
$path=Write-Fixture 'valid' @(,$valid)
$rows=@(Read-InventoryCountExcel $path)
Assert ($rows.Count -eq 1 -and $rows[0].Warehouse -ceq '001' -and $rows[0].Inventory -ceq '0000123' -and $rows[0].Batch -ceq '000001') 'Identifiers lost leading zeros.'
Assert ($rows[0].Quantity -eq 0 -and $rows[0].Price -eq 0) 'Zero values rejected.'
foreach ($mode in @('formula','numericCode','duplicateHeader')) { $badPath=Write-Fixture $mode @(,$valid) $mode;Deny { Read-InventoryCountExcel $badPath } ('Unsafe XLSX accepted: '+$mode) }
foreach ($case in @(@('blankQuantity',3,''),@('negativeQuantity',3,'-1'),@('quantityPrecision',3,'0.0000001'),@('blankPrice',6,''),@('pricePrecision',6,'0.00000000001'))) {
    $bad=$valid.Clone();$bad[$case[1]]=$case[2];$badPath=Write-Fixture $case[0] @(,$bad)
    Deny { Read-InventoryCountExcel $badPath } ('Invalid value accepted: '+$case[0])
}
$id='a'*64
$requests=@(New-InventoryCountRequests $rows '' '2026-10-07' $id)
Assert ($requests.Count -eq 1 -and $requests[0].Body.autoVerify -eq $false -and $requests[0].Body.lines[0].planUnitPrice -eq 0 -and $requests[0].Body.lines[0].actualQuantity -eq 0) 'Mapping/zero/audit failed.'
Assert ($null -eq $requests[0].Body.lines[0].PSObject.Properties['bookQuantity']) 'Book quantity overridden.'
$same=@(Read-InventoryCountExcel $path)[0];$other=@(Read-InventoryCountExcel $path)[0];$other.Group='PD02'
Assert (@(New-InventoryCountRequests @($same,$other) '' '2026-10-07' $id).Count -eq 2) 'Explicit groups failed.'
$other.Group='PD01';$other.Warehouse='002'
Assert (@(New-InventoryCountRequests @($same,$other) '' '2026-10-07' $id).Count -eq 2) 'Warehouse split failed.'
$other.Warehouse='001';$other.Department='0003'
Assert (@(New-InventoryCountRequests @($same,$other) '' '2026-10-07' $id).Count -eq 2) 'Department split failed.'
$other.Department='0002'
Deny { New-InventoryCountRequests @($same,$other) '' '2026-10-07' $id } 'Duplicate row accepted.'
$many=@(1..1001 | ForEach-Object { $free=[ordered]@{};foreach($n in 1..10){$free['free'+$n]=''};[pscustomobject]@{ExcelRow=$_+1;Group='';Warehouse='W';Inventory='I'+$_;Quantity=[decimal]1;Batch='';PositionName='';Price=[decimal]1;Department='D';Free=$free;PositionCode=''} })
$split=@(New-InventoryCountRequests $many '' '2026-10-07' $id)
Assert ($split.Count -eq 2 -and $split[0].LineCount -eq 1000 -and $split[1].LineCount -eq 1 -and $split[1].Body.lines[0].lineNo -eq 1) '1000-row splitting failed.'
$many[1000].Inventory=$many[0].Inventory
Deny { New-InventoryCountRequests $many '' '2026-10-07' $id } 'Duplicate across split accepted.'

# SQL and HTTP mocks live only in this test process. No external connections.
& $module {
    $script:WarehousePositions=$true;$script:PositionMatches=1;$script:FreeMatches=1
    function script:Read-CountTable($Connection,[string]$Sql,[hashtable]$Parameters) {
        $table=New-Object Data.DataTable
        if ($Sql.Contains('dbo.Warehouse')) { [void]$table.Columns.Add('bWhPos',[bool]);[void]$table.Rows.Add($script:WarehousePositions) }
        elseif ($Sql.Contains('dbo.Position')) { [void]$table.Columns.Add('cPosCode',[string]);foreach($i in 1..$script:PositionMatches){[void]$table.Rows.Add('408')} }
        elseif ($Sql.Contains('dbo.Inventory')) { [void]$table.Columns.Add('bInvBatch',[bool]);foreach($n in 1..10){[void]$table.Columns.Add('bFree'+$n,[bool])};$r=$table.NewRow();$r.bInvBatch=$true;foreach($n in 1..10){$r['bFree'+$n]=($n -eq 1)};[void]$table.Rows.Add($r) }
        else { foreach($n in 1..10){[void]$table.Columns.Add('free'+$n,[string])};foreach($i in 1..$script:FreeMatches){$r=$table.NewRow();foreach($n in 1..10){$r['free'+$n]=if($n -eq 1){'等级'+$i}else{''}};[void]$table.Rows.Add($r)} }
        return ,$table
    }
}
$row=@(Read-InventoryCountExcel $path)[0]
$resolved=@(Resolve-InventoryCountRows @($row) $null)
Assert ($resolved[0].PositionCode -eq '408' -and $resolved[0].Free.free1 -eq '等级1') ('Position/free field enrichment failed: '+($resolved | ConvertTo-Json -Depth 4 -Compress))
& $module { $script:FreeMatches=2 }
$row=@(Read-InventoryCountExcel $path)[0]
Deny { Resolve-InventoryCountRows @($row) $null } 'Ambiguous grade selected silently.'
& $module { $script:FreeMatches=1;$script:PositionMatches=2 }
Deny { Resolve-InventoryCountRows @($row) $null } 'Ambiguous position selected silently.'
& $module { $script:PositionMatches=1 }
$row.PositionName=''
Deny { Resolve-InventoryCountRows @($row) $null } 'Required position omitted.'
& $module { $script:WarehousePositions=$false }
$row.PositionName='1101'
Deny { Resolve-InventoryCountRows @($row) $null } 'Position accepted for non-position warehouse.'

& $module {
    $script:Calls=0;$script:FailCall=0;$script:RawJson=''
    function script:Invoke-CountApi([string]$Uri,[hashtable]$Headers,$Body) {
        $script:Calls++;$script:RawJson=$Body
        if ($script:Calls -eq $script:FailCall) { throw 'Simulated lost response' }
        if (!$Headers['Idempotency-Key']) { throw 'Missing idempotency key' }
        return [pscustomobject]@{voucherCode='PD-'+$script:Calls;voucherId=[string](100+$script:Calls);isVerified=$false}
    }
}
$same.Group='G1';$other.Group='G2'
$third=@(Read-InventoryCountExcel $path)[0];$third.Group='G3'
$same.Price=[decimal]::Parse('123456789012345678.1234567890',[Globalization.CultureInfo]::InvariantCulture)
$plan=@(New-InventoryCountRequests @($same,$other,$third) '' '2026-10-07' $id)
$state=[pscustomobject]@{Requests=$plan}
$statePath=Join-Path $testRoot 'state.json'
Write-CountJson $state $statePath
$loaded=Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert ($loaded.Requests[0].BodyJson -ceq $plan[0].BodyJson -and $loaded.Requests[0].BodyJson.Contains('123456789012345678.123456789')) 'Restart rounded decimal payload.'
& $module { $script:FailCall=2 }
Deny { Invoke-CountImportSave $loaded $statePath 'http://mock/api/v1' @{} } 'Lost response continued creating vouchers.'
$saved=Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert ($saved.Requests[0].Status -eq 'Created' -and $saved.Requests[1].Status -eq 'Unknown' -and $saved.Requests[2].Status -eq 'Ready') 'Partial failure journal incorrect.'
Assert ((& $module { $script:Calls }) -eq 2) 'Later voucher created after failure.'
Deny { Invoke-CountImportSave $saved $statePath 'http://mock/api/v1' @{} } 'Unknown result replayed.'
Assert ((& $module { $script:Calls }) -eq 2) 'Unknown retry reached HTTP.'
& $module { $script:FailCall=0 }
$success=[pscustomobject]@{Requests=@(New-InventoryCountRequests @($same) '' '2026-10-07' $id)}
Invoke-CountImportSave $success (Join-Path $testRoot 'success.json') 'http://mock/api/v1' @{}
$calls=& $module { $script:Calls }
Invoke-CountImportSave $success (Join-Path $testRoot 'success.json') 'http://mock/api/v1' @{}
Assert ((& $module { $script:Calls }) -eq $calls) 'Successful request duplicated.'
if ($TemplatePath) {
    $message='';try { Read-InventoryCountExcel $TemplatePath | Out-Null } catch { $message=$_.Exception.Message }
    Assert ($message -eq '没有可导入的盘点数据。') ('Generated blank template could not be read: '+$message)
}
Write-Host 'Excel读取、前导零、零值、非法输入、多单拆分、重复行、货位自由项校验、精度、部分成功及未知结果保护测试通过。未连接 SQL 或 U8。'
