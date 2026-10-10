param([string]$SettingsPath='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$deployment=Join-Path (Split-Path -Parent $PSScriptRoot) 'Deployment'
Import-Module (Join-Path $deployment 'OtherVoucherMemoCleanup.psm1') -Force
function Assert([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function New-Plan {
    $table=New-Object Data.DataTable 'MemoPreview'
    foreach($name in @('VoucherType','AutoID','ID','Memo','MemoHex','LineVersion','HeadVersion','VoucherCode','Audited')){[void]$table.Columns.Add($name)}
    return ,$table
}
function Add-PlanRow($Table,$Type,$AutoID,$ID,$Memo,$Audited=0) {
    $hex='0x'+([BitConverter]::ToString([Text.Encoding]::Unicode.GetBytes($Memo))).Replace('-','')
    [void]$Table.Rows.Add($Type,$AutoID,$ID,$Memo,$hex,'0x0000000000000001','0x0000000000000002','0000000001',$Audited)
}
$empty=New-Plan
Assert ((Get-MemoSummary $empty).Total -eq 0) 'Empty summary failed.'
Assert ((ConvertTo-MemoApprovalXml $empty) -eq '<rows />') 'Empty approval failed.'
Add-PlanRow $empty '其他出库' 11 101 "说明<&`r`n最近单价：0  "
$summary=Get-MemoSummary $empty
Assert ($summary.Outbound -eq 1 -and $summary.Inbound -eq 0 -and $summary.Audited -eq 0) 'Single-row summary failed.'
[xml]$xml=ConvertTo-MemoApprovalXml $empty
Assert ($xml.rows.row.MemoHex -eq $empty.Rows[0].MemoHex) 'Memo binary approval failed.'
Add-PlanRow $empty '其他入库' 11 201 '其他说明；最近单价：25' 1
Assert ((Get-MemoSummary $empty).Audited -eq 1 -and (Get-MemoSummary $empty).Total -eq 2) 'Mixed summary failed.'
$name=New-MemoBackupTableName
$sql=Get-MemoCleanupSql $name
Assert ($sql.Contains("update b set cbMemo=''")) 'Full memo clear missing.'
Assert ($sql.Contains('except select') -and $sql.Contains('rowufts') -and $sql.Contains('updlock,holdlock')) 'Concurrency guard missing.'
$rejected=$false;try{Get-MemoCleanupSql 'x]; drop table a;--'|Out-Null}catch{$rejected=$true}
Assert $rejected 'Unsafe backup identifier accepted.'
Assert (!(Get-MemoSelectionSql).Contains('updlock')) 'Preview must be read-only.'
foreach($file in @('OtherVoucherMemoCleanup.psm1','Clear-U8OtherVoucherPriceMemos.ps1')) {
    $tokens=$null;$parseErrors=$null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $deployment $file),[ref]$tokens,[ref]$parseErrors)
    Assert (@($parseErrors).Count -eq 0) ('Parse errors: '+$file)
    $bytes=[IO.File]::ReadAllBytes((Join-Path $deployment $file))
    Assert ($bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) ('UTF-8 BOM missing: '+$file)
}
# Exercise the actual UI with transport/file-copy stubs only, never a server.
$source=Get-Content (Join-Path $deployment 'Clear-U8OtherVoucherPriceMemos.ps1') -Raw -Encoding UTF8
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)
$main=$ast.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]}|Select-Object -Last 1
$mainBlock=[scriptblock]::Create($main.Extent.Text.Replace('$PSScriptRoot',("'"+$deployment.Replace("'","''")+"'")))
function New-Item {}
function Copy-Item {}
function Test-Path {return $false}
$script:clearCalls=0;$script:mode='cancel';$script:fixtureTotal=2
function Invoke-MemoJob {
    param($Action,$ViewRoot,$ServerRoot,$Local)
    if($Action -eq 'Clear'){$script:clearCalls++;return [pscustomobject]@{Success=$true;Result=@{BackedUp=2;Cleared=2;BackupTable='dbo.test'}}}
    return [pscustomobject]@{Success=$true;Summary=@{Inbound=1;Outbound=1;Total=$script:fixtureTotal;Audited=0};BackupTable='dbo.test'}
}
function Read-Host {param($Prompt);return $script:mode}
$Database='008';$Server='192.0.2.10';$PSCommandPath='C:\fixture.ps1';$PreviewOnly=$false
& $mainBlock
Assert ($script:clearCalls -eq 0) 'Cancel called cleanup.'
$script:mode='clear';$PreviewOnly=$true
& $mainBlock
Assert ($script:clearCalls -eq 0) 'PreviewOnly called cleanup.'
$PreviewOnly=$false;$script:fixtureTotal=0
& $mainBlock
Assert ($script:clearCalls -eq 0) 'Empty result called cleanup.'
$script:fixtureTotal=2
& $mainBlock
Assert ($script:clearCalls -eq 1) 'CLEAR gate failed.'
foreach($functionName in @('New-Item','Copy-Item','Test-Path','Read-Host','Invoke-MemoJob')){Remove-Item ('Function:\'+$functionName)}
Write-Host '纯数据、编码与预览/取消/确认测试通过（模拟保存）。'
if(!$SettingsPath){return}

# Optional SQL integration tests use only session-local #tables in tempdb.
Add-Type -AssemblyName System.Security
$stored=Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8|ConvertFrom-Json
$plain=$null;$builder=$null;$connection=$null
try {
    $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($stored.SqlPasswordProtected),[Text.Encoding]::UTF8.GetBytes('MaterialOutboundApi.Settings.v1'),[Security.Cryptography.DataProtectionScope]::LocalMachine)
    $builder=New-Object Data.SqlClient.SqlConnectionStringBuilder
    $builder['Data Source']=$stored.Public.SqlServer;$builder['Initial Catalog']='tempdb'
    $builder['User ID']=$stored.Public.SqlUser;$builder['Password']=[Text.Encoding]::UTF8.GetString($plain)
    $builder['Encrypt']=$false;$builder['TrustServerCertificate']=$true
    $connection=New-Object Data.SqlClient.SqlConnection $builder.ConnectionString;$connection.Open()
    function Invoke-TestSql([string]$Text,$Transaction=$null) {
        $command=$connection.CreateCommand();$command.CommandText=$Text;$command.Transaction=$Transaction
        try{[void]$command.ExecuteNonQuery()}finally{$command.Dispose()}
    }
    Invoke-TestSql @'
create table #RdRecord08(ID bigint primary key,cCode varchar(30),cHandler nvarchar(30),dVeriDate datetime,ufts rowversion);
create table #RdRecord09(ID bigint primary key,cCode varchar(30),cHandler nvarchar(30),dVeriDate datetime,ufts rowversion);
create table #RdRecords08(AutoID bigint primary key,ID bigint,cbMemo nvarchar(255),rowufts rowversion,iQuantity decimal(20,6),iUnitCost money,iPrice money);
create table #RdRecords09(AutoID bigint primary key,ID bigint,cbMemo nvarchar(255),rowufts rowversion,iQuantity decimal(20,6),iUnitCost money,iPrice money);
insert #RdRecord08(ID,cCode,cHandler) values(201,'in','审员');
insert #RdRecord09(ID,cCode,cHandler) values(101,'out','');
insert #RdRecords08(AutoID,ID,cbMemo,iQuantity,iUnitCost,iPrice) values(11,201,N'说明<&'+char(13)+char(10)+N'最近单价：0  ',2,0,null),(12,201,N'最近入库单价：5',3,5,15);
insert #RdRecords09(AutoID,ID,cbMemo,iQuantity,iUnitCost,iPrice) values(11,101,N'最近单价：25；业务说明',3,25,75),(12,101,N'普通备注',4,6,24),(13,101,null,5,null,null),(14,101,'',6,7,42);
'@
    $selection=(Get-MemoSelectionSql).Replace('dbo.RdRecords08','#RdRecords08').Replace('dbo.RdRecords09','#RdRecords09').Replace('dbo.RdRecord08','#RdRecord08').Replace('dbo.RdRecord09','#RdRecord09')
    $adapter=New-Object Data.SqlClient.SqlDataAdapter ($selection+' order by VoucherType,ID,AutoID'),$connection
    $plan=New-Object Data.DataTable 'MemoPreview';[void]$adapter.Fill($plan);$adapter.Dispose()
    Assert ($plan.Rows.Count -eq 2) 'Exact 最近单价 selection failed.'
    Assert ($plan.Rows[0].LineVersion -match '\A0x[0-9A-Fa-f]{16}\z') 'Rowversion hex conversion failed.'
    $query=(Get-MemoCleanupSql $name).Replace('dbo.RdRecords08','#RdRecords08').Replace('dbo.RdRecords09','#RdRecords09').Replace('dbo.RdRecord08','#RdRecord08').Replace('dbo.RdRecord09','#RdRecord09')
    $query=$query.Replace(('dbo.['+$name+']'),'#Backup').Replace(('dbo.'+$name),'tempdb..#Backup')
    foreach($scenario in @('Success','MemoChanged','HeadChanged','NewMatch','ExistingBackup','InsertFailure','UpdateFailure','WrongDatabase','EmptyPlan')) {
        $transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        $command=$null;$adapter=$null
        try {
            switch($scenario) {
                'MemoChanged' {Invoke-TestSql "update #RdRecords09 set cbMemo=N'最近单价：26' where AutoID=11" $transaction}
                'HeadChanged' {Invoke-TestSql "update #RdRecord09 set cHandler=N'新审核人' where ID=101" $transaction}
                'NewMatch' {Invoke-TestSql "update #RdRecords09 set cbMemo=N'最近单价：6' where AutoID=12" $transaction}
                'ExistingBackup' {Invoke-TestSql 'create table #Backup(a int)' $transaction}
                'UpdateFailure' {Invoke-TestSql "alter table #RdRecords09 add check (AutoID<>11 or cbMemo<>'')" $transaction}
            }
            $testQuery=$query
            if($scenario -eq 'InsertFailure'){$testQuery=$testQuery.Replace('Memo nvarchar(max) not null','Memo nvarchar(max) not null check (Memo=N''reject-all'')')}
            if($scenario -eq 'Success') {
                # Parameterized SQL creates local #Backup in sp_executesql's scope.
                # Assert its contents inside that exact batch before it goes away.
                $checks=@'
if (select count(*) from #Backup)<>2 throw 51000,'Backup count failed',1;
if (select count(*) from tempdb.sys.columns where object_id=object_id('tempdb..#Backup'))<>4 throw 51000,'Backup fields failed',1;
if exists(select 1 from #RdRecords08 where AutoID=11 and (cbMemo<>'' or iQuantity<>2 or iUnitCost<>0 or iPrice is not null)) throw 51000,'Inbound preservation failed',1;
if exists(select 1 from #RdRecords09 where AutoID=11 and (cbMemo<>'' or iQuantity<>3 or iUnitCost<>25 or iPrice<>75)) throw 51000,'Outbound preservation failed',1;
if (select cbMemo from #RdRecords08 where AutoID=12)<>N'最近入库单价：5' throw 51000,'Nonmatching memo changed',1;
if (select cbMemo from #RdRecords09 where AutoID=12)<>N'普通备注' throw 51000,'Ordinary memo changed',1;
if (select cHandler from #RdRecord08 where ID=201)<>N'审员' throw 51000,'Audit state changed',1;
'@
                $testQuery=$testQuery.Replace('select @count as BackedUp,@updated as Cleared;',($checks+"`nselect @count as BackedUp,@updated as Cleared;"))
            }
            $command=$connection.CreateCommand();$command.CommandText=$testQuery;$command.Transaction=$transaction
            [void]$command.Parameters.Add('@Database',[Data.SqlDbType]::NVarChar,128);$command.Parameters['@Database'].Value='tempdb'
            [void]$command.Parameters.Add('@ApprovedXml',[Data.SqlDbType]::Xml);$command.Parameters['@ApprovedXml'].Value=ConvertTo-MemoApprovalXml $plan
            if($scenario -eq 'WrongDatabase'){$command.Parameters['@Database'].Value='wrong'}
            if($scenario -eq 'EmptyPlan'){$command.Parameters['@ApprovedXml'].Value='<rows />'}
            $result=New-Object Data.DataTable;$adapter=New-Object Data.SqlClient.SqlDataAdapter $command
            $failure=$false;try{[void]$adapter.Fill($result)}catch{$failure=$true;Write-Host ($scenario+'：'+$_.Exception.Message)}
            if($scenario -eq 'Success') {
                Assert (!$failure -and $result.Rows[0].Cleared -eq 2) 'SQL success failed.'
            }else{Assert $failure ('Unsafe SQL scenario accepted: '+$scenario)}
        }finally{
            if($null -ne $adapter){$adapter.Dispose()};if($null -ne $command){$command.Dispose()}
            $transaction.Rollback();$transaction.Dispose()
        }
        Invoke-TestSql "if object_id('tempdb..#Backup') is not null throw 51000,'Backup rollback failed',1; if (select cbMemo from #RdRecords09 where AutoID=11)<>N'最近单价：25；业务说明' throw 51000,'Memo rollback failed',1;"
    }
    Write-Host 'SQL临时表测试通过：备份清空、字段保留、并发变化、新匹配行、拒绝覆盖、备份失败回滚。未修改ERP表。'
}finally{
    if($null -ne $connection){$connection.Dispose()};if($null -ne $builder){$builder.Clear()}
    if($null -ne $plain){[Array]::Clear($plain,0,$plain.Length)}
}
