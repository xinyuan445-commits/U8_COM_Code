param([string]$InteropDirectory='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(!$InteropDirectory){$InteropDirectory=Join-Path (Split-Path -Parent $projectRoot) 'MaterialOutboundConsole\lib'}
foreach($name in @('adodb.dll','Interop.VBA.dll','Interop.Scripting.dll','Interop.USCOMMON.dll','Interop.U8Login.dll','Interop.MSXML2.dll','Interop.USERPCO.dll')){Add-Type -Path (Join-Path $InteropDirectory $name)}
$source=Get-Content (Join-Path $projectRoot 'Deployment\OtherVoucherPriceEngine.cs') -Raw -Encoding UTF8
Add-Type -TypeDefinition $source -Language CSharp -ReferencedAssemblies @(
    'System.Data.dll','System.Data.DataSetExtensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.Core.dll',
    (Join-Path $InteropDirectory 'adodb.dll'),(Join-Path $InteropDirectory 'Interop.U8Login.dll'),(Join-Path $InteropDirectory 'Interop.MSXML2.dll'),(Join-Path $InteropDirectory 'Interop.USERPCO.dll'))
[OtherVoucherPriceEngine]::SelfTest()
if($source -notmatch 'i\.iInCost>=0' -or $source -match 'i\.iInCost>0') { throw 'Historical zero price must not be filtered out.' }
# Validate preview guards with in-memory data; never connect to SQL/U8.
$type=[OtherVoucherPriceEngine]
$private=[Reflection.BindingFlags]'Static,NonPublic'
$inputGuard=$type.GetMethod('ValidateInput',$private)
$rejected=0
foreach($values in @(@('11','1','Data Source=test;Initial Catalog=UFDATA_008_2021'),@('08','../bad','Data Source=test;Initial Catalog=UFDATA_008_2021'),@('08','1','Data Source=test;Initial Catalog=OtherDatabase'))) {
    try{$inputGuard.Invoke($null,[object[]]$values)|Out-Null}catch{$rejected++}
}
if($rejected -ne 3){throw 'Invalid route/database/code guard failed.'}
$inputGuard.Invoke($null,[object[]]@('09','0000006579','Data Source=test;Initial Catalog=UFDATA_008_2021'))|Out-Null
$snapshot=New-Object Data.DataTable
foreach($field in @('VoucherId','HeadVersion','AutoID','LineVersion','SourceAutoID','LatestUnitCost','cHandler','dVeriDate')){[void]$snapshot.Columns.Add($field,[object])}
[void]$snapshot.Rows.Add(1,'H1',2,'L1',3,8.9,[DBNull]::Value,[DBNull]::Value)
$compare=$type.GetMethod('SamePlan',$private)
$validate=$type.GetMethod('Validate',$private)
$validate.Invoke($null,[object[]]@($snapshot.PSObject.BaseObject,'0000006579'))|Out-Null
$copy=$snapshot.Copy()
$compare.Invoke($null,[object[]]@($snapshot.PSObject.BaseObject,$copy.PSObject.BaseObject))|Out-Null
foreach($field in @('VoucherId','HeadVersion','AutoID','LineVersion','SourceAutoID','LatestUnitCost')){
    $changed=$snapshot.Copy();$changed.Rows[0][$field]='changed'
    $denied=$false;try{$compare.Invoke($null,[object[]]@($snapshot.PSObject.BaseObject,$changed.PSObject.BaseObject))|Out-Null}catch{$denied=$true}
    if(!$denied){throw ('Concurrent change accepted: '+$field)}
}
foreach($field in @('cHandler','dVeriDate')){
    $audited=$snapshot.Copy();$audited.Rows[0][$field]=if($field -eq 'cHandler'){'reviewer'}else{Get-Date}
    $denied=$false;try{$validate.Invoke($null,[object[]]@($audited.PSObject.BaseObject,'0000006579'))|Out-Null}catch{$denied=$true}
    if(!$denied){throw ('Audited voucher accepted: '+$field)}
}
& (Join-Path $projectRoot 'Deployment\Update-U8OtherVoucherPrices.ps1') -TestInputs
# A changed choice must be rejected before any SQL/U8 operation.
$policyRoot=Join-Path ([IO.Path]::GetTempPath()) ('OtherPricePolicy-'+[guid]::NewGuid().ToString('N'))
$policyFolder=Join-Path $policyRoot '09-0000006579'
[void][IO.Directory]::CreateDirectory($policyFolder)
try {
    $snapshot.TableName='VoucherLines'
    $snapshot.WriteXml((Join-Path $policyFolder 'plan.xml'),[Data.XmlWriteMode]::WriteSchema)
    [IO.File]::WriteAllText((Join-Path $policyFolder 'replace-zero-prices.txt'),'false')
    $policyResult=[OtherVoucherPriceEngine]::Run('09','0000006579','Data Source=unused;Initial Catalog=UFDATA_008_2021','ST','(default)@008','2021','unused','','unused','',$policyRoot,$true)
    if($policyResult.Success -or $policyResult.Error -notlike 'Zero-price policy differs*'){throw 'Changed zero-price choice was accepted.'}
} finally {
    [IO.File]::Delete((Join-Path $policyFolder 'plan.xml'))
    [IO.File]::Delete((Join-Path $policyFolder 'replace-zero-prices.txt'))
    [IO.Directory]::Delete($policyFolder)
    [IO.Directory]::Delete($policyRoot)
}
# Run the complete interactive loop with transport/file-copy stubs only.
$loopSource=Get-Content (Join-Path $projectRoot 'Deployment\Update-U8OtherVoucherPrices.ps1') -Raw -Encoding UTF8
$loopMarker='if($Worker){Invoke-Worker;return}'
$loopStubs=@'
$script:loopAnswers=New-Object 'Collections.Generic.Queue[string]'
foreach($answer in @('1','0000006591','','y','save','2','0000005838','','n','cancel','1','0000006592','','n','save','0')){$script:loopAnswers.Enqueue($answer)}
$script:loopJobs=New-Object 'Collections.Generic.List[object]'
function Read-Host { param([string]$Prompt) if(!$script:loopAnswers.Count){throw 'Unexpected input prompt.'}; return $script:loopAnswers.Dequeue() }
function New-Item { param([string]$ItemType,[string]$Path,[switch]$Force) if($ItemType -ne 'Directory'){throw 'Unexpected file operation.'} }
function Copy-Item { param([string]$LiteralPath,[string]$Destination) }
function Invoke-Job {
    param([string]$Action,[string]$ViewRoot,[string]$ServerRoot,[bool]$Local,[object]$Job)
    $script:loopJobs.Add([pscustomobject]@{Action=$Action;Type=$Job.VoucherType;Code=($Job.VoucherCodes -join ',');Zero=$Job.ReplaceZeroPrices})
    $ok=!($Action -eq 'Save' -and $Job.VoucherCodes[0] -eq '0000006592')
    return [pscustomobject]@{Success=$true;Results=@([pscustomobject]@{VoucherCode=$Job.VoucherCodes[0];Success=$ok;Status='U8SaveSucceeded';Lines=1;PricesFilled=1;RemarksUpdated=1;MissingPriceLines=0;MissingPriceDetails=@();Error='Mock save failure; not a real U8 operation'})}
}
'@
if(!$loopSource.Contains($loopMarker)){throw 'Interactive loop test marker missing.'}
$loopSource=$loopSource.Replace($loopMarker,$loopMarker+[Environment]::NewLine+$loopStubs)
$loopScript=Join-Path ([IO.Path]::GetTempPath()) ('OtherPriceLoop-'+[guid]::NewGuid().ToString('N')+'.ps1')
try {
    [IO.File]::WriteAllText($loopScript,$loopSource,(New-Object Text.UTF8Encoding($true)))
    . $loopScript
    if($script:loopAnswers.Count -ne 0 -or $script:loopJobs.Count -ne 5){throw 'Loop did not process successful/cancelled/failed batches then exit.'}
    $expected=@('Preview|09|0000006591|True','Save|09|0000006591|True','Preview|08|0000005838|False','Preview|09|0000006592|False','Save|09|0000006592|False')
    for($i=0;$i -lt $expected.Count;$i++){
        $entry=$script:loopJobs[$i]
        if(($entry.Action+'|'+$entry.Type+'|'+$entry.Code+'|'+$entry.Zero) -ne $expected[$i]){throw 'A loop reused old voucher inputs or zero-price policy.'}
    }
    . $loopScript -VoucherType 09 -VoucherCodes 0000006591 -ReplaceZeroPrices -PreviewOnly
    if($script:loopJobs.Count -ne 1 -or $script:loopAnswers.Count -ne 16 -or $script:loopJobs[0].Action -ne 'Preview'){throw 'PreviewOnly did not stop after one batch.'}
    . $loopScript -VoucherType 09 -VoucherCodes 0000006591 -ReplaceZeroPrices:$false -Apply
    if($script:loopJobs.Count -ne 2 -or $script:loopAnswers.Count -ne 16 -or $script:loopJobs[1].Action -ne 'Save' -or $script:loopJobs[1].Zero){throw 'Apply did not stop after one batch with the explicit zero-price choice.'}
} finally {
    [IO.File]::Delete($loopScript)
    foreach($stub in @('Read-Host','New-Item','Copy-Item','Invoke-Job')){Remove-Item ('Function:\'+$stub) -ErrorAction SilentlyContinue}
}
Write-Host '零价规则、金额备注、选项绑定、循环成功/取消/失败/退出、输入、并发审核保护检查通过。未连接数据库或 U8。'
