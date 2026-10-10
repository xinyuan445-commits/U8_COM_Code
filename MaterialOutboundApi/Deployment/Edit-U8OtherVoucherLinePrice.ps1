param(
    [ValidateSet('08','09')][string]$VoucherType='',
    [string]$VoucherCode='',
    [int]$LineNo=0,
    [string]$UnitPrice='',
    [ValidateSet('Keep','Recalculate')][string]$AmountMode='Keep',
    [ValidateSet('008','998')][string]$Database='008',
    [string]$Server='192.0.2.10',
    [switch]$PreviewOnly,
    [switch]$Apply,
    [switch]$Worker,
    [string]$RequestPath='',
    [switch]$TestInputs
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Console]::InputEncoding=[Text.Encoding]::UTF8
[Console]::OutputEncoding=[Text.Encoding]::UTF8

function Confirm-VoucherCode([string]$Code) {
    $code=$Code.Trim()
    if($code -notmatch '\A[A-Za-z0-9_-]{1,30}\z'){throw '请输入一个正确的单号。'}
    return $code
}
function Convert-UnitPrice([string]$Text) {
    $value=$Text.Trim()
    if($value -notmatch '\A[0-9]{1,18}(\.[0-9]{1,10})?\z'){throw '单价须为非负数，最多18位整数和10位小数。'}
    return [decimal]::Parse($value,[Globalization.CultureInfo]::InvariantCulture)
}
function Show-Line([object]$Item) {
    Write-Host ('{0} 第 {1} 行：{2} {3}' -f $Item.VoucherCode,$Item.LineNo,$Item.InventoryCode,$Item.InventoryName)
    Write-Host ('批号：{0}；货位：{1}；数量：{2}' -f $Item.Batch,$Item.Position,$Item.Quantity)
    Write-Host ('当前单价：{0}；当前金额：{1}' -f $Item.OldUnitPrice,$Item.OldAmount)
}
function Save-Json([object]$Value,[string]$Path) {
    # Publish complete responses atomically so the client never reads partial JSON.
    $temporary=$Path+'.tmp'
    $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function Unprotect-Text([string]$Value) {
    if([string]::IsNullOrWhiteSpace($Value)){return ''}
    Add-Type -AssemblyName System.Security
    $bytes=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($Value),[Text.Encoding]::UTF8.GetBytes('MaterialOutboundApi.Settings.v1'),[Security.Cryptography.DataProtectionScope]::LocalMachine)
    try{[Text.Encoding]::UTF8.GetString($bytes)}finally{[Array]::Clear($bytes,0,$bytes.Length)}
}
function Invoke-Worker {
    if(![IO.Path]::IsPathRooted($RequestPath)){$RequestPath=Join-Path $PSScriptRoot $RequestPath}
    $request=Get-Content -LiteralPath $RequestPath -Raw -Encoding UTF8|ConvertFrom-Json
    $root=Split-Path -Parent ([IO.Path]::GetFullPath($RequestPath))
    if(!$root.StartsWith('D:\MupiOtherVoucherLinePriceJobs\',[StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $root) -notmatch '\A[a-f0-9]{32}\z'){throw 'Invalid worker job directory.'}
    $response=Join-Path $root ($request.Action+'-response.json')
    $builder=$null;$u8Password=$null
    try {
        if($request.Action -notin @('Query','Preview','Save') -or $request.Database -notin @('008','998') -or $request.VoucherType -notin @('08','09')){throw 'Invalid worker request.'}
        $codes=@(Confirm-VoucherCode ([string]$request.VoucherCode))
        if([int]$request.LineNo -le 0 -or $request.AmountMode -notin @('Keep','Recalculate')){throw 'Invalid line/amount mode.'}
        $price=if($request.Action -eq 'Query'){[decimal]0}else{Convert-UnitPrice ([string]$request.UnitPrice)}
        $interop='D:\MaterialOutboundApi\bin'
        foreach($name in @('adodb.dll','Interop.VBA.dll','Interop.Scripting.dll','Interop.USCOMMON.dll','Interop.U8Login.dll','Interop.MSXML2.dll','Interop.USERPCO.dll')){Add-Type -Path (Join-Path $interop $name)}
        $legacyInterop='D:\U8SOFT\U8KCSN\bin\Interop.USERPVO.dll'
        if(Test-Path $legacyInterop){Add-Type -Path $legacyInterop}
        Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'OtherVoucherLinePriceEngine.cs') -Raw -Encoding UTF8) -Language CSharp -ReferencedAssemblies @(
            'System.Data.dll','System.Data.DataSetExtensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.Core.dll',
            (Join-Path $interop 'adodb.dll'),(Join-Path $interop 'Interop.U8Login.dll'),(Join-Path $interop 'Interop.MSXML2.dll'),(Join-Path $interop 'Interop.USERPCO.dll'))
        [OtherVoucherLinePriceEngine]::SelfTest()
        $stored=Get-Content 'D:\MaterialOutboundApi\App_Data\settings.json' -Raw -Encoding UTF8|ConvertFrom-Json
        $builder=New-Object Data.SqlClient.SqlConnectionStringBuilder
        $builder['Data Source']=$stored.Public.SqlServer
        $builder['Initial Catalog']='UFDATA_'+$request.Database+'_2021'
        $builder['User ID']=$stored.Public.SqlUser
        $builder['Password']=Unprotect-Text $stored.SqlPasswordProtected
        $builder['Encrypt']=$false;$builder['TrustServerCertificate']=$true
        $account=[string]$stored.Public.U8AccId
        $separator=$account.LastIndexOf('@')
        $account=if($separator -ge 0){$account.Substring(0,$separator)+'@'+$request.Database}else{'(default)@'+$request.Database}
        $results=New-Object 'Collections.Generic.List[object]'
        if($request.Action -eq 'Save') {
            $approved=Get-Content (Join-Path $root 'Preview-request.json') -Raw -Encoding UTF8|ConvertFrom-Json
            if($approved.Database -ne $request.Database -or $approved.VoucherType -ne $request.VoucherType -or
                $approved.VoucherCode -cne $request.VoucherCode -or [int]$approved.LineNo -ne [int]$request.LineNo -or
                $approved.UnitPrice -cne $request.UnitPrice -or $approved.AmountMode -ne $request.AmountMode){throw 'Save does not match the preview request.'}
            $u8Password=Unprotect-Text $stored.U8PasswordProtected
        }
        foreach($code in $codes) {
            try {
                if($request.Action -eq 'Query'){$item=[OtherVoucherLinePriceEngine]::Query($request.VoucherType,$code,$builder.ConnectionString,[int]$request.LineNo)}
                elseif($request.Action -eq 'Preview'){$item=[OtherVoucherLinePriceEngine]::Prepare($request.VoucherType,$code,$builder.ConnectionString,$root,[int]$request.LineNo,$price,($request.AmountMode -eq 'Recalculate'))}
                else{$item=[OtherVoucherLinePriceEngine]::Run($request.VoucherType,$code,$builder.ConnectionString,[string]$stored.Public.U8SubId,$account,[string]$stored.Public.U8Year,[string]$stored.Public.U8UserId,$u8Password,[string]$stored.Public.U8Server,[string]$stored.Public.U8Serial,$root,[int]$request.LineNo,$price,($request.AmountMode -eq 'Recalculate'))}
            }catch{$item=[pscustomobject]@{VoucherCode=$code;Success=$false;Status='NotSaved';LineNo=$request.LineNo;Error=$_.Exception.Message}}
            $results.Add($item)
            Save-Json ([pscustomobject]@{Completed=$false;Action=$request.Action;Processed=$results.Count;Total=$codes.Count}) (Join-Path $root 'progress.json')
        }
        Save-Json ([pscustomobject]@{Completed=$true;Success=$true;Action=$request.Action;Results=$results.ToArray();Error=''}) $response
    }catch{Save-Json ([pscustomobject]@{Completed=$true;Success=$false;Action=$request.Action;Results=@();Error=$_.Exception.Message}) $response}
    finally{$u8Password=$null;if($null -ne $builder){$builder.Clear()}}
}
function Invoke-Job([string]$Action,[string]$ViewRoot,[string]$ServerRoot,[bool]$Local,[object]$Job) {
    $request=[pscustomobject]@{Action=$Action;Database=$Job.Database;VoucherType=$Job.VoucherType;VoucherCode=$Job.VoucherCode;LineNo=$Job.LineNo;UnitPrice=$Job.UnitPrice;AmountMode=$Job.AmountMode}
    Save-Json $request (Join-Path $ViewRoot ($Action+'-request.json'))
    $workerScript=Join-Path $ServerRoot 'w.ps1'
    $workerRequest=$Action+'-request.json'
    $taskName='MupiOtherLinePrice-'+$Job.Id+'-'+$Action
    $created=$false;$process=$null
    try {
        if($Local){
            $process=Start-Process -FilePath "$env:windir\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-STA','-NoProfile','-ExecutionPolicy','Bypass','-File',$workerScript,'-Worker','-RequestPath',$workerRequest) -WindowStyle Hidden -PassThru
        }else{
            $actionText='C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -STA -NoProfile -ep Bypass -File '+$workerScript+' -Worker -RequestPath '+$workerRequest
            & schtasks /Create /S $Server /TN $taskName /SC ONCE /ST 23:58 /RU SYSTEM /TR $actionText | Out-Null
            if($LASTEXITCODE -ne 0){throw '不能启动服务器任务，请确认 Windows 账号有服务器管理权限。'}
            $created=$true
            & schtasks /Run /S $Server /TN $taskName | Out-Null
            if($LASTEXITCODE -ne 0){throw '服务器任务启动失败。'}
        }
        $response=Join-Path $ViewRoot ($Action+'-response.json')
        $deadline=(Get-Date).AddMinutes(30)
        while(!(Test-Path -LiteralPath $response)) {
            if((Get-Date) -gt $deadline){throw '等待结果超时。请查看任务日志，不要重复保存同一批单据。'}
            $progress=Join-Path $ViewRoot 'progress.json'
            if(Test-Path -LiteralPath $progress){$state=Get-Content $progress -Raw -Encoding UTF8|ConvertFrom-Json;Write-Progress -Activity $(if($Action -eq 'Save'){'正在保存'}else{'正在查询'}) -Status ($state.Processed.ToString()+'/'+$state.Total.ToString())}
            if($Local -and $null -ne $process -and $process.HasExited -and !(Test-Path $response)){throw '任务退出但未返回结果，请查看任务日志。'}
            Start-Sleep -Seconds 1
        }
        $result=Get-Content -LiteralPath $response -Raw -Encoding UTF8|ConvertFrom-Json
        if(!$result.Success){throw $result.Error}
        return $result
    }finally{
        Write-Progress -Activity '正在处理' -Completed
        if($created){& schtasks /Delete /S $Server /TN $taskName /F | Out-Null}
    }
}

if($TestInputs) {
    if((Confirm-VoucherCode '0000006579') -ne '0000006579' -or (Convert-UnitPrice '0') -ne 0 -or (Convert-UnitPrice '1.2345678901') -ne [decimal]1.2345678901){throw 'Input regression.'}
    foreach($bad in @('-1','1.23456789012','1,000','abc','1e3')) {
        $denied=$false;try{Convert-UnitPrice $bad|Out-Null}catch{$denied=$true};if(!$denied){throw 'Invalid price accepted.'}
    }
    $denied=$false;try{Confirm-VoucherCode '1,2'|Out-Null}catch{$denied=$true};if(!$denied){throw 'Multiple vouchers accepted.'}
    Write-Host '输入检查通过。';return
}
if($Worker){Invoke-Worker;return}
try {
    if(!$VoucherType){while($VoucherType -notin @('08','09')){$choice=([string](Read-Host '单据类型：1=其他出库，2=其他入库')).Trim();if($choice -eq '1'){$VoucherType='09'}elseif($choice -eq '2'){$VoucherType='08'}}}
    if(!$VoucherCode){$VoucherCode=[string](Read-Host '单号')}
    $VoucherCode=Confirm-VoucherCode $VoucherCode
    if(!$PSBoundParameters.ContainsKey('LineNo')){
        $lineText=([string](Read-Host '行号')).Trim()
        if(![int]::TryParse($lineText,[ref]$LineNo)){throw '行号必须为正整数。'}
    }
    if($LineNo -le 0){throw '行号必须为正整数。'}
    if($Server -notmatch '\A[A-Za-z0-9.-]+\z'){throw '服务器地址格式不正确。'}
    $job=[pscustomobject]@{Id=[guid]::NewGuid().ToString('N');Database=$Database;VoucherType=$VoucherType;VoucherCode=$VoucherCode;LineNo=$LineNo;UnitPrice='';AmountMode='Keep'}
    $serverRoot='D:\MupiOtherVoucherLinePriceJobs\'+$job.Id
    $local=Test-Path 'D:\MaterialOutboundApi\App_Data\settings.json'
    $viewRoot=if($local){$serverRoot}else{'\\'+$Server+'\D$\MupiOtherVoucherLinePriceJobs\'+$job.Id}
    New-Item -ItemType Directory -Path $viewRoot -Force | Out-Null
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $viewRoot 'w.ps1')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OtherVoucherLinePriceEngine.cs') -Destination $viewRoot
    $query=Invoke-Job 'Query' $viewRoot $serverRoot $local $job
    $item=@($query.Results)[0]
    if(!$item.Success){throw $item.Error}
    $queried=$item
    Show-Line $item
    if(!$UnitPrice){$UnitPrice=[string](Read-Host '新单价（可为0）')}
    $newPrice=Convert-UnitPrice $UnitPrice
    if(!$PSBoundParameters.ContainsKey('AmountMode') -and !$PreviewOnly -and !$Apply){
        while($true){
            $choice=([string](Read-Host '同步重算本行金额？Y=重算，N=只改单价')).Trim().ToUpperInvariant()
            if($choice -eq 'Y'){$AmountMode='Recalculate';break}
            if($choice -eq 'N'){$AmountMode='Keep';break}
        }
    }
    $job.UnitPrice=$newPrice.ToString('0.##########',[Globalization.CultureInfo]::InvariantCulture)
    $job.AmountMode=$AmountMode
    $preview=Invoke-Job 'Preview' $viewRoot $serverRoot $local $job
    $item=@($preview.Results)[0]
    if(!$item.Success){throw $item.Error}
    foreach($field in @('InventoryCode','Batch','Position','Quantity','OldUnitPrice','OldAmount')){
        if($queried.$field -cne $item.$field){throw '该行信息已变化，请重新运行。未保存。'}
    }
    Write-Host ('单价：{0} → {1}；金额：{2} → {3}' -f $item.OldUnitPrice,$item.NewUnitPrice,$item.OldAmount,$item.NewAmount)
    if($PreviewOnly){Write-Host '只预览，未保存。';return}
    if(!$item.PriceChanged -and !$item.AmountChanged){Write-Host '无需修改。';return}
    if(!$Apply -and ([string](Read-Host '输入 SAVE 保存，不审核')).Trim().ToUpperInvariant() -ne 'SAVE'){Write-Host '已取消，未保存。';return}
    $saved=Invoke-Job 'Save' $viewRoot $serverRoot $local $job
    $item=@($saved.Results)[0]
    if(!$item.Success){throw $item.Error}
    Write-Host ('{0} 第 {1} 行：{2}' -f $item.VoucherCode,$item.LineNo,$(if($item.Status -eq 'NoChangesNeeded'){'无需修改'}else{'保存成功'}))
    Write-Host ('日志：'+$viewRoot)
}catch{Write-Host $_.Exception.Message -ForegroundColor Red;exit 1}
