param(
    [ValidateSet('008','998')][string]$Database='008',
    [string]$Server='192.0.2.10',
    [switch]$PreviewOnly,
    [switch]$Worker,
    [string]$RequestPath=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Console]::InputEncoding=[Text.Encoding]::UTF8
[Console]::OutputEncoding=[Text.Encoding]::UTF8
Import-Module (Join-Path $PSScriptRoot 'OtherVoucherMemoCleanup.psm1') -Force

function Save-MemoJson([object]$Value,[string]$Path) {
    $temporary=$Path+'.tmp'
    $Value|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function Invoke-MemoWorker {
    $requestFile=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $RequestPath))
    $root=Split-Path -Parent $requestFile
    if(!$root.StartsWith('D:\MupiOtherVoucherMemoJobs\',[StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $root) -notmatch '\A[a-f0-9]{32}\z'){throw 'Invalid job directory.'}
    $request=Get-Content -LiteralPath $requestFile -Raw -Encoding UTF8|ConvertFrom-Json
    if($request.Action -notin @('Preview','Clear') -or $request.Database -notin @('008','998')){throw 'Invalid request.'}
    $response=Join-Path $root ($request.Action+'-response.json')
    $connection=$null;$builder=$null;$plain=$null
    try {
        $stored=Get-Content 'D:\MaterialOutboundApi\App_Data\settings.json' -Raw -Encoding UTF8|ConvertFrom-Json
        Add-Type -AssemblyName System.Security
        $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($stored.SqlPasswordProtected),
            [Text.Encoding]::UTF8.GetBytes('MaterialOutboundApi.Settings.v1'),[Security.Cryptography.DataProtectionScope]::LocalMachine)
        $builder=New-Object Data.SqlClient.SqlConnectionStringBuilder
        $builder['Data Source']=[string]$stored.Public.SqlServer
        $builder['Initial Catalog']='UFDATA_'+$request.Database+'_2021'
        $builder['User ID']=[string]$stored.Public.SqlUser
        $builder['Password']=[Text.Encoding]::UTF8.GetString($plain)
        $builder['Encrypt']=$false;$builder['TrustServerCertificate']=$true
        $builder['Application Name']='MupiOtherVoucherMemoCleanup'
        $connection=New-Object Data.SqlClient.SqlConnection $builder.ConnectionString
        $connection.Open()
        $planPath=Join-Path $root 'preview.xml'
        $approvalPath=Join-Path $root 'approval.json'
        if($request.Action -eq 'Preview') {
            if(Test-Path -LiteralPath $approvalPath){throw 'Preview already exists; start a new job.'}
            $table=Read-MemoPreview $connection
            $summary=Get-MemoSummary $table
            # Keep the complete memo as UTF-16 hex in the plan: legacy control
            # characters must not corrupt the XML approval transport.
            $table.Columns.Remove('Memo')
            $table.WriteXml($planPath,[Data.XmlWriteMode]::WriteSchema)
            $approved=[pscustomobject]@{Database=$request.Database;SqlServer=$builder.DataSource;
                PlanHash=(Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash;Summary=$summary;BackupTable=(New-MemoBackupTableName)}
            Save-MemoJson $approved $approvalPath
            Save-MemoJson ([pscustomobject]@{Success=$true;Summary=$summary;BackupTable=('dbo.'+$approved.BackupTable)}) $response
        }else{
            $approved=Get-Content -LiteralPath $approvalPath -Raw -Encoding UTF8|ConvertFrom-Json
            if($request.Database -ne $approved.Database -or $builder.DataSource -cne $approved.SqlServer -or
                (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash -cne $approved.PlanHash){throw 'Preview binding changed.'}
            $table=New-Object Data.DataTable 'MemoPreview';[void]$table.ReadXml($planPath)
            if($table.Rows.Count -ne $approved.Summary.Total){throw 'Preview row count changed.'}
            # CreateNew blocks replay even if a response/connection was lost.
            $marker=New-Object IO.FileStream((Join-Path $root 'clear-attempted.txt'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
            $marker.Dispose()
            $result=Invoke-MemoCleanup $connection $table $approved.BackupTable
            Save-MemoJson ([pscustomobject]@{Success=$true;Result=$result}) $response
        }
    }catch{
        Save-MemoJson ([pscustomobject]@{Success=$false;Error=$_.Exception.Message}) $response
    }finally{
        if($null -ne $connection){$connection.Dispose()}
        if($null -ne $builder){$builder.Clear()}
        if($null -ne $plain){[Array]::Clear($plain,0,$plain.Length)}
    }
}
function Invoke-MemoJob([string]$Action,[string]$ViewRoot,[string]$ServerRoot,[bool]$Local) {
    Save-MemoJson ([pscustomobject]@{Action=$Action;Database=$Database}) (Join-Path $ViewRoot ($Action+'-request.json'))
    $taskName='MupiMemoCleanup-'+(Split-Path -Leaf $ServerRoot)+'-'+$Action
    $created=$false;$process=$null
    try {
        $scriptFile=Join-Path $ServerRoot 'w.ps1'
        $requestFile=$Action+'-request.json'
        if($Local) {
            $process=Start-Process -FilePath "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @(
                '-NoProfile','-ExecutionPolicy','Bypass','-File',$scriptFile,'-Worker','-RequestPath',$requestFile) -WindowStyle Hidden -PassThru
        }else{
            $actionText='C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ep Bypass -File '+$scriptFile+' -Worker -RequestPath '+$requestFile
            & schtasks /Create /S $Server /TN $taskName /SC ONCE /ST 23:58 /RU SYSTEM /TR $actionText|Out-Null
            if($LASTEXITCODE -ne 0){throw '不能创建服务器任务，请确认 Windows 管理权限。'}
            $created=$true
            & schtasks /Run /S $Server /TN $taskName|Out-Null
            if($LASTEXITCODE -ne 0){throw '服务器任务启动失败。'}
        }
        $response=Join-Path $ViewRoot ($Action+'-response.json')
        $deadline=(Get-Date).AddMinutes(10)
        while(!(Test-Path -LiteralPath $response)) {
            if((Get-Date) -gt $deadline){throw '结果待核对，请检查日志和备份表；不要直接重试清理。'}
            if($Local -and $process.HasExited -and !(Test-Path -LiteralPath $response)){throw '工作进程退出，结果待核对，请检查日志。'}
            Start-Sleep -Seconds 1
        }
        $result=Get-Content -LiteralPath $response -Raw -Encoding UTF8|ConvertFrom-Json
        if(!$result.Success){throw $result.Error}
        return $result
    }finally{if($created){& schtasks /Delete /S $Server /TN $taskName /F|Out-Null}}
}

if($Worker){Invoke-MemoWorker;return}
try {
    if($Server -notmatch '\A[A-Za-z0-9.-]+\z'){throw '服务器地址格式不正确。'}
    $jobId=[guid]::NewGuid().ToString('N')
    $serverRoot='D:\MupiOtherVoucherMemoJobs\'+$jobId
    $local=Test-Path -LiteralPath 'D:\MaterialOutboundApi\App_Data\settings.json'
    $viewRoot=if($local){$serverRoot}else{'\\'+$Server+'\D$\MupiOtherVoucherMemoJobs\'+$jobId}
    New-Item -ItemType Directory -Path $viewRoot|Out-Null
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $viewRoot 'w.ps1')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OtherVoucherMemoCleanup.psm1') -Destination $viewRoot
    Write-Host ('账套：UFDATA_'+$Database+'_2021；匹配“最近单价”。')
    $preview=Invoke-MemoJob 'Preview' $viewRoot $serverRoot $local
    Write-Host ('其他入库 {0} 行，其他出库 {1} 行，合计 {2} 行。' -f $preview.Summary.Inbound,$preview.Summary.Outbound,$preview.Summary.Total)
    Write-Host ('日志：'+$viewRoot)
    if($preview.Summary.Total -eq 0){Write-Host '没有需要清理的备注。';return}
    Write-Host ('备份表：'+$preview.BackupTable)
    if($PreviewOnly){Write-Host '仅预览，未建备份表、未清理。';return}
    if($preview.Summary.Audited -gt 0){Write-Host ('含已审核 {0} 行；只清备注，不改变审核状态。' -f $preview.Summary.Audited) -ForegroundColor Yellow}
    Write-Host '将备份完整备注，再清空整条备注（含其他说明）。请先关闭这些单据的编辑窗口。' -ForegroundColor Yellow
    if(([string](Read-Host '输入 CLEAR 备份并清空，其他输入取消')).Trim().ToUpperInvariant() -ne 'CLEAR'){Write-Host '已取消，未清理。';return}
    $saved=Invoke-MemoJob 'Clear' $viewRoot $serverRoot $local
    Write-Host ('完成：备份 {0} 行，清空 {1} 行。' -f $saved.Result.BackedUp,$saved.Result.Cleared)
    Write-Host ('备份表：'+$saved.Result.BackupTable)
}catch{Write-Host $_.Exception.Message -ForegroundColor Red;exit 1}
