Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Get-MemoSelectionSql([switch]$Lock) {
    $hint=if($Lock){'with (updlock,holdlock)'}else{''}
    $parts=foreach($type in @('08','09')) {
        $label=if($type -eq '08'){'其他入库'}else{'其他出库'}
        @"
select N'$label' as VoucherType, convert(bigint,b.AutoID) as AutoID, convert(bigint,b.ID) as ID,
convert(nvarchar(max),b.cbMemo) as Memo,
convert(varchar(max),convert(varbinary(max),convert(nvarchar(max),b.cbMemo)),1) as MemoHex,
convert(varchar(18),convert(varbinary(8),b.rowufts),1) as LineVersion,
convert(varchar(18),convert(varbinary(8),h.ufts),1) as HeadVersion, h.cCode as VoucherCode,
case when nullif(ltrim(rtrim(h.cHandler)),'') is not null or h.dVeriDate is not null then 1 else 0 end as Audited
from dbo.RdRecords$type b $hint join dbo.RdRecord$type h $hint on h.ID=b.ID
where charindex(N'最近单价',convert(nvarchar(max),b.cbMemo))>0
"@
    }
    return ($parts -join "`nunion all`n")
}
function ConvertTo-MemoApprovalXml([Data.DataTable]$Table) {
    $document=New-Object Xml.XmlDocument
    $root=$document.CreateElement('rows');[void]$document.AppendChild($root)
    foreach($row in $Table.Rows) {
        $element=$document.CreateElement('row')
        foreach($name in @('VoucherType','AutoID','ID','MemoHex','LineVersion','HeadVersion')) {
            if($row[$name] -is [DBNull]){throw "预览缺少字段：$name"}
            $element.SetAttribute($name,[string]$row[$name])
        }
        [void]$root.AppendChild($element)
    }
    return $document.OuterXml
}
function New-MemoBackupTableName {
    return 'MupiOtherVoucherMemoBackup_'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+[guid]::NewGuid().ToString('N').Substring(0,8)
}
function Get-MemoCleanupSql([string]$BackupTable) {
    if($BackupTable -notmatch '\AMupiOtherVoucherMemoBackup_\d{8}_\d{6}_[a-f0-9]{8}\z'){throw '备份表名不合法。'}
    $selection=Get-MemoSelectionSql -Lock
    @"
set nocount on;
set xact_abort on;
set lock_timeout 30000;
if db_name()<>@Database throw 51000, N'目标数据库不符。',1;
if exists(select 1 from sys.triggers t join sys.trigger_events e on e.object_id=t.object_id
where t.parent_id in (object_id(N'dbo.RdRecords08'),object_id(N'dbo.RdRecords09')) and t.is_disabled=0 and e.type_desc='UPDATE')
throw 51000,N'明细存在启用的扩展规则，需先评估副作用；未清理。',1;
declare @lock int;
exec @lock=sys.sp_getapplock @Resource=N'MupiOtherVoucherMemoCleanup',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000;
if @lock<0 throw 51000,N'已有清理任务执行中。',1;
select * into #Current from ($selection) s;
create table #Approved(VoucherType nvarchar(4) not null,AutoID bigint not null,ID bigint not null,
MemoHex varchar(max) not null,LineVersion varchar(18) not null,HeadVersion varchar(18) not null,
primary key(VoucherType,AutoID));
insert #Approved select
r.value('@VoucherType','nvarchar(4)'),r.value('@AutoID','bigint'),r.value('@ID','bigint'),
r.value('@MemoHex','varchar(max)'),r.value('@LineVersion','varchar(18)'),r.value('@HeadVersion','varchar(18)')
from @ApprovedXml.nodes('/rows/row') n(r);
if exists(select 1 from #Approved where VoucherType not in (N'其他入库',N'其他出库'))
throw 51000,N'无效的预览类型。',1;
if exists(select 1 from #Current where LineVersion is null or HeadVersion is null)
throw 51000,N'单据缺少版本信息。',1;
if exists(select VoucherType,AutoID,ID,MemoHex,LineVersion,HeadVersion from #Current
except select VoucherType,AutoID,ID,MemoHex,LineVersion,HeadVersion from #Approved)
or exists(select VoucherType,AutoID,ID,MemoHex,LineVersion,HeadVersion from #Approved
except select VoucherType,AutoID,ID,MemoHex,LineVersion,HeadVersion from #Current)
throw 51000,N'单据或备注已变化，请重新预览；未清理。',1;
declare @count int=(select count(*) from #Approved),@updated int=0;
if @count=0 throw 51000,N'没有可清理的备注。',1;
if object_id(N'dbo.$BackupTable') is not null throw 51000,N'备份表已存在，拒绝覆盖。',1;
create table dbo.[$BackupTable](VoucherType nvarchar(4) not null,AutoID bigint not null,
ID bigint not null,Memo nvarchar(max) not null,primary key(VoucherType,AutoID));
insert dbo.[$BackupTable](VoucherType,AutoID,ID,Memo)
select VoucherType,AutoID,ID,Memo from #Current;
if @@rowcount<>@count throw 51000,N'备份行数不符。',1;
update b set cbMemo=''
from dbo.RdRecords08 b join #Current c on c.AutoID=b.AutoID and c.ID=b.ID and c.VoucherType=N'其他入库';
set @updated=@@rowcount;
update b set cbMemo=''
from dbo.RdRecords09 b join #Current c on c.AutoID=b.AutoID and c.ID=b.ID and c.VoucherType=N'其他出库';
set @updated=@updated+@@rowcount;
if @updated<>@count throw 51000,N'清理行数不符。',1;
if exists(select 1 from dbo.RdRecords08 b join #Current c on c.AutoID=b.AutoID and c.ID=b.ID
where c.VoucherType=N'其他入库' and (b.cbMemo is null or datalength(b.cbMemo)<>0))
or exists(select 1 from dbo.RdRecords09 b join #Current c on c.AutoID=b.AutoID and c.ID=b.ID
where c.VoucherType=N'其他出库' and (b.cbMemo is null or datalength(b.cbMemo)<>0))
throw 51000,N'备注清空校验失败。',1;
if exists(select VoucherType,AutoID,ID,convert(varbinary(max),Memo) from #Current
except select VoucherType,AutoID,ID,convert(varbinary(max),Memo) from dbo.[$BackupTable])
or (select count(*) from dbo.[$BackupTable])<>@count
throw 51000,N'备份内容校验失败。',1;
select @count as BackedUp,@updated as Cleared;
"@
}
function Read-MemoPreview([Data.SqlClient.SqlConnection]$Connection) {
    $command=$Connection.CreateCommand()
    $adapter=$null
    try {
        $command.CommandText=(Get-MemoSelectionSql)+' order by VoucherType,ID,AutoID'
        $command.CommandTimeout=120
        $adapter=New-Object Data.SqlClient.SqlDataAdapter $command
        $table=New-Object Data.DataTable 'MemoPreview'
        [void]$adapter.Fill($table)
        return ,$table
    } finally {if($null -ne $adapter){$adapter.Dispose()};$command.Dispose()}
}
function Get-MemoSummary([Data.DataTable]$Table) {
    return [pscustomobject]@{
        Inbound=@($Table.Rows|Where-Object {$_.VoucherType -eq '其他入库'}).Count
        Outbound=@($Table.Rows|Where-Object {$_.VoucherType -eq '其他出库'}).Count
        Audited=@($Table.Rows|Where-Object {$_.Audited -eq 1}).Count
        Total=$Table.Rows.Count
    }
}
function Invoke-MemoCleanup([Data.SqlClient.SqlConnection]$Connection,[Data.DataTable]$Plan,[string]$BackupTable) {
    if($Connection.Database -notin @('UFDATA_008_2021','UFDATA_998_2021')){throw '仅支持008、998账套。'}
    $sql=Get-MemoCleanupSql $BackupTable
    $approved=ConvertTo-MemoApprovalXml $Plan
    $mutex=New-Object Threading.Mutex($false,'Global\MaterialOutboundApi.U8Operations')
    $held=$false;$transaction=$null;$command=$null;$adapter=$null;$committing=$false
    try {
        try{$held=$mutex.WaitOne(30000)}catch [Threading.AbandonedMutexException]{$held=$true}
        if(!$held){throw 'U8 正忙，请稍后重新预览。'}
        $transaction=$Connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        $command=$Connection.CreateCommand();$command.Transaction=$transaction
        $command.CommandText=$sql;$command.CommandTimeout=300
        [void]$command.Parameters.Add('@Database',[Data.SqlDbType]::NVarChar,128)
        $command.Parameters['@Database'].Value=$Connection.Database
        [void]$command.Parameters.Add('@ApprovedXml',[Data.SqlDbType]::Xml)
        $command.Parameters['@ApprovedXml'].Value=$approved
        $adapter=New-Object Data.SqlClient.SqlDataAdapter $command
        $result=New-Object Data.DataTable
        [void]$adapter.Fill($result)
        if($result.Rows.Count -ne 1 -or $result.Rows[0].BackedUp -ne $Plan.Rows.Count -or $result.Rows[0].Cleared -ne $Plan.Rows.Count){throw '事务返回行数不符。'}
        $committing=$true
        $transaction.Commit()
        return [pscustomobject]@{BackedUp=[int]$result.Rows[0].BackedUp;Cleared=[int]$result.Rows[0].Cleared;BackupTable='dbo.'+$BackupTable}
    }catch{
        if($null -ne $transaction){try{$transaction.Rollback()}catch{}}
        if($committing){throw "提交结果待核对，请检查备份表 dbo.$BackupTable 和原备注，勿直接重试。"}
        throw
    }finally{
        if($null -ne $adapter){$adapter.Dispose()};if($null -ne $command){$command.Dispose()}
        if($null -ne $transaction){$transaction.Dispose()}
        if($held){$mutex.ReleaseMutex()};$mutex.Dispose()
    }
}
Export-ModuleMember -Function Get-MemoSelectionSql,ConvertTo-MemoApprovalXml,New-MemoBackupTableName,Get-MemoCleanupSql,Read-MemoPreview,Get-MemoSummary,Invoke-MemoCleanup
