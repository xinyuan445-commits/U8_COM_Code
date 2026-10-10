param([string]$InteropDirectory='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(!$InteropDirectory){$InteropDirectory=Join-Path (Split-Path -Parent $projectRoot) 'MaterialOutboundConsole\lib'}
foreach($name in @('adodb.dll','Interop.VBA.dll','Interop.Scripting.dll','Interop.USCOMMON.dll','Interop.U8Login.dll','Interop.MSXML2.dll','Interop.USERPCO.dll')){Add-Type -Path (Join-Path $InteropDirectory $name)}
Add-Type -TypeDefinition (Get-Content (Join-Path $projectRoot 'Deployment\OtherVoucherAmountEngine.cs') -Raw -Encoding UTF8) -Language CSharp -ReferencedAssemblies @(
    'System.Data.dll','System.Data.DataSetExtensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.Core.dll',
    (Join-Path $InteropDirectory 'adodb.dll'),(Join-Path $InteropDirectory 'Interop.U8Login.dll'),(Join-Path $InteropDirectory 'Interop.MSXML2.dll'),(Join-Path $InteropDirectory 'Interop.USERPCO.dll'))
[OtherVoucherAmountEngine]::SelfTest()
$private=[Reflection.BindingFlags]'Static,NonPublic'
$inputGuard=[OtherVoucherAmountEngine].GetMethod('ValidateInput',$private)
foreach($values in @(@('11','1','Data Source=test;Initial Catalog=UFDATA_008_2021'),@('08','../bad','Data Source=test;Initial Catalog=UFDATA_008_2021'),@('08','1','Data Source=test;Initial Catalog=OtherDatabase'))) {
    $denied=$false;try{$inputGuard.Invoke($null,[object[]]$values)|Out-Null}catch{$denied=$true}
    if(!$denied){throw 'Invalid route/database/code accepted.'}
}
$snapshot=New-Object Data.DataTable
foreach($field in @('VoucherId','HeadVersion','AutoID','LineVersion','iQuantity','iUnitCost','iPrice','cHandler','dVeriDate')){[void]$snapshot.Columns.Add($field,[object])}
[void]$snapshot.Rows.Add(1,'H1',2,'L1',3,8.9,[DBNull]::Value,[DBNull]::Value,[DBNull]::Value)
$compare=[OtherVoucherAmountEngine].GetMethod('SamePlan',$private)
$validate=[OtherVoucherAmountEngine].GetMethod('Validate',$private)
$validate.Invoke($null,[object[]]@($snapshot.PSObject.BaseObject,'0000006579'))|Out-Null
$copy=$snapshot.Copy();$compare.Invoke($null,[object[]]@($snapshot.PSObject.BaseObject,$copy.PSObject.BaseObject))|Out-Null
foreach($field in @('VoucherId','HeadVersion','AutoID','LineVersion','iQuantity','iUnitCost','iPrice')){
    $changed=$snapshot.Copy();$changed.Rows[0][$field]='changed'
    $denied=$false;try{$compare.Invoke($null,[object[]]@($snapshot.PSObject.BaseObject,$changed.PSObject.BaseObject))|Out-Null}catch{$denied=$true}
    if(!$denied){throw ('Concurrent change accepted: '+$field)}
}
foreach($field in @('cHandler','dVeriDate')){
    $audited=$snapshot.Copy();$audited.Rows[0][$field]=if($field -eq 'cHandler'){'reviewer'}else{Get-Date}
    $denied=$false;try{$validate.Invoke($null,[object[]]@($audited.PSObject.BaseObject,'0000006579'))|Out-Null}catch{$denied=$true}
    if(!$denied){throw ('Audited voucher accepted: '+$field)}
}
& (Join-Path $projectRoot 'Deployment\Repair-U8OtherVoucherAmounts.ps1') -TestInputs
Write-Host '补空金额、零价、负数舍入、原值保留、缺值跳过、重复运行、溢出、并发与审核保护测试通过。未连接 SQL 或 U8。'
