param([Parameter(Mandatory=$true)][string]$BinDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[void][Reflection.Assembly]::LoadFrom((Join-Path $BinDirectory 'GeneralInventoryCount.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $BinDirectory 'MaterialOutboundApi.dll'))
$editType = $assembly.GetType('MaterialOutboundApi.Services.InventoryCountLineEdit', $true)
$rowType = $assembly.GetType('MaterialOutboundApi.Services.InventoryCountVoucherLineRow', $true)
$editor = $assembly.GetType('MaterialOutboundApi.Services.InventoryCountLineEditor', $true)
$method = $editor.GetMethod('Apply')
$head = '<xml xmlns:z="#RowsetSchema"><z:row bposcheck="1" cbufts="123" /></xml>'
$body = '<xml xmlns:z="#RowsetSchema"><z:row autoid="11" irowno="1" cinvcode="A" ccvbatch="B1" cposition="408" icvquantity="10" icvcquantity="0" iinvexchrate="2" icvnum="5" ijhdj="3" cfree1="GradeA" ddisdate="2027-01-01" cbufts="123.456" /><z:row autoid="12" irowno="2" cinvcode="A" ccvbatch="B2" cposition="409" icvquantity="20" icvcquantity="20" cfree1="GradeA" editprop="" /></xml>'
function New-Edit {
    $inputLine = New-Object GeneralInventoryCount.Models.InventoryCountQuantityUpdateLineRequest
    $inputLine.LineNo = 1
    $inputLine.ActualQuantity = 4
    $row = [Activator]::CreateInstance($rowType, $true)
    $row.AutoId = 11; $row.LineNo = 1; $row.InventoryCode = 'A'
    $row.BatchCode = 'B1'; $row.PositionCode = '408'; $row.BookQuantity = 10
    $row.ActualQuantity = 0
    $edit = [Activator]::CreateInstance($editType, $true)
    $edit.Input = $inputLine; $edit.Current = $row; $edit.PositionCode = '409'
    return $edit
}
function Apply-Edit($edit, [string]$testHead = $head, [string]$testBody = $body, [string]$position = '') {
    $list = [Array]::CreateInstance($editType, 1); $list.SetValue($edit, 0)
    return $method.Invoke($null, @($testHead, $testBody, $position, $list))
}
function Assert-Equal($actual, $expected, [string]$label) {
    if ($actual -ne $expected) { throw "$label : expected $expected, actual $actual" }
}
function Assert-Rejected($edit, [string]$code, [string]$testHead = $head, [string]$testBody = $body, [string]$position = '') {
    try { [void](Apply-Edit $edit $testHead $testBody $position) }
    catch {
        $errorException = $_.Exception
        while ($errorException.InnerException) { $errorException = $errorException.InnerException }
        Assert-Equal $errorException.Code $code 'Error code'
        return
    }
    throw "Expected rejection: $code"
}
$edit = New-Edit
$edit.Input.UpdateBatchCode = $true; $edit.Input.BatchCode = 'B3'
$edit.Input.UpdatePosition = $true
$edit.Input.UpdateReason = $true; $edit.Input.Reason = 'Count & review'
$edit.Input.UpdateRemark = $true; $edit.Input.Remark = 'Chinese: 复盘'
$result = Apply-Edit $edit
[xml]$output = $result.Body
$rows = @($output.SelectNodes("//*[local-name()='row']"))
$row = $rows[0]
Assert-Equal $row.icvcquantity '4' 'Actual quantity'
Assert-Equal $row.yksl '-6' 'Difference'
Assert-Equal $row.icvcnum '2' 'Auxiliary quantity'
Assert-Equal $row.ykjs '-3' 'Auxiliary difference'
Assert-Equal $row.isjdj '12' 'Actual amount'
Assert-Equal $row.isjje '-18' 'Difference amount'
Assert-Equal $row.ccvbatch 'B3' 'Batch'
Assert-Equal $row.cposition '409' 'Position'
Assert-Equal $row.ccvreason 'Count & review' 'XML escaping'
Assert-Equal $row.cbmemo 'Chinese: 复盘' 'Remark'
Assert-Equal $row.cbufts '123.456' 'Timestamp preservation'
Assert-Equal $row.ddisdate '2027-01-01' 'Expiration preservation'
Assert-Equal $row.cfree1 'GradeA' 'Free field preservation'
[xml]$original = $body
Assert-Equal $rows[1].OuterXml $original.SelectNodes("//*[local-name()='row']")[1].OuterXml 'Untouched line'
$edit = New-Edit; $edit.Input.ActualQuantity = 0; $edit.Input.UpdateRemark = $true; $edit.Input.Remark = ''
$result = Apply-Edit $edit
[xml]$output = $result.Body
Assert-Equal $output.SelectNodes("//*[local-name()='row']")[0].GetAttribute('cbmemo') '' 'Clear remark'
$edit = New-Edit
Assert-Rejected $edit 'VOUCHER_LINE_CHANGED' $head ($body.Replace('icvcquantity="0"', 'icvcquantity="1"'))
Assert-Rejected $edit 'VOUCHER_LINE_XML_AMBIGUOUS' $head ($body.Replace('irowno="2"', 'irowno="1"'))
Assert-Rejected $edit 'VOUCHER_NOT_EDITABLE' ($head.Replace('bposcheck="1"', 'bposcheck="1" caccounter="User"'))
$edit.Input.UpdateBatchCode = $true; $edit.Input.BatchCode = 'B2'; $edit.Input.UpdatePosition = $true
Assert-Rejected $edit 'DUPLICATE_COUNT_LINE'
Assert-Rejected $edit 'POSITION_COUNT_SCOPE_MISMATCH' ($head.Replace('bposcheck="1"', 'bposcheck="0"'))
Assert-Rejected $edit 'LINKED_LINE_IDENTITY_CHANGE' $head ($body.Replace('autoid="11"', 'autoid="11" rdsid="19"'))
Assert-Rejected $edit 'POSITION_ALLOCATION_NOT_SUPPORTED' $head $body '<xml xmlns:z="#RowsetSchema"><z:row /></xml>'
Write-Host 'PASS: quantity, batch, position, reason, remark, preservation and safety checks.'
