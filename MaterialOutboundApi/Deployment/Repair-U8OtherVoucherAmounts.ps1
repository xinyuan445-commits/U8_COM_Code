param(
    [ValidateSet('08','09')][string]$VoucherType='',
    [string[]]$VoucherCodes=@(),
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

function Convert-VoucherCodes([string[]]$Values) {
    $codes=New-Object 'Collections.Generic.List[string]'
    foreach($value in @($Values)) {
        foreach($part in @(([string]$value -split '[,，;；\s|]+'))) {
            $code=$part.Trim()
            if(!$code -or $code -match '^-+$'){continue}
            if($code -notmatch '\A[A-Za-z0-9_-]{1,30}\z'){throw "单号格式不正确：$code"}
            if(!$codes.Contains($code)){$codes.Add($code)}
        }
    }
    if($codes.Count -gt 100){throw '一次最多100张单。'}
    return $codes.ToArray()
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
    if(!$root.StartsWith('D:\MupiOtherVoucherAmountJobs\',[StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $root) -notmatch '\A[a-f0-9]{32}\z'){throw 'Invalid worker job directory.'}
    $response=Join-Path $root ($request.Action+'-response.json')
    $builder=$null;$u8Password=$null
    try {
        if($request.Action -notin @('Preview','Save') -or $request.Database -notin @('008','998') -or $request.VoucherType -notin @('08','09')){throw 'Invalid worker request.'}
        $codes=@(Convert-VoucherCodes @($request.VoucherCodes))
        if($codes.Count -eq 0){throw 'No vouchers supplied.'}
        $interop='D:\MaterialOutboundApi\bin'
        foreach($name in @('adodb.dll','Interop.VBA.dll','Interop.Scripting.dll','Interop.USCOMMON.dll','Interop.U8Login.dll','Interop.MSXML2.dll','Interop.USERPCO.dll')){Add-Type -Path (Join-Path $interop $name)}
        $legacyInterop='D:\U8SOFT\U8KCSN\bin\Interop.USERPVO.dll'
        if(Test-Path $legacyInterop){Add-Type -Path $legacyInterop}
        Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'OtherVoucherAmountEngine.cs') -Raw -Encoding UTF8) -Language CSharp -ReferencedAssemblies @(
            'System.Data.dll','System.Data.DataSetExtensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.Core.dll',
            (Join-Path $interop 'adodb.dll'),(Join-Path $interop 'Interop.U8Login.dll'),(Join-Path $interop 'Interop.MSXML2.dll'),(Join-Path $interop 'Interop.USERPCO.dll'))
        [OtherVoucherAmountEngine]::SelfTest()
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
                (@($approved.VoucherCodes) -join '|') -cne ($codes -join '|')){throw 'Save does not match the preview request.'}
            $u8Password=Unprotect-Text $stored.U8PasswordProtected
        }
        foreach($code in $codes) {
            try {
                if($request.Action -eq 'Preview'){$item=[OtherVoucherAmountEngine]::Prepare($request.VoucherType,$code,$builder.ConnectionString,$root)}
                else{$item=[OtherVoucherAmountEngine]::Run($request.VoucherType,$code,$builder.ConnectionString,[string]$stored.Public.U8SubId,$account,[string]$stored.Public.U8Year,[string]$stored.Public.U8UserId,$u8Password,[string]$stored.Public.U8Server,[string]$stored.Public.U8Serial,$root)}
            }catch{$item=[pscustomobject]@{VoucherCode=$code;Success=$false;Status='NotSaved';Lines=0;AmountsFilled=0;ZeroPriceAmounts=0;MissingInputsLines=0;MissingInputsDetails=@();Error=$_.Exception.Message}}
            $results.Add($item)
            Save-Json ([pscustomobject]@{Completed=$false;Action=$request.Action;Processed=$results.Count;Total=$codes.Count}) (Join-Path $root 'progress.json')
        }
        Save-Json ([pscustomobject]@{Completed=$true;Success=$true;Action=$request.Action;Results=$results.ToArray();Error=''}) $response
    }catch{Save-Json ([pscustomobject]@{Completed=$true;Success=$false;Action=$request.Action;Results=@();Error=$_.Exception.Message}) $response}
    finally{$u8Password=$null;if($null -ne $builder){$builder.Clear()}}
}
function Invoke-Job([string]$Action,[string]$ViewRoot,[string]$ServerRoot,[bool]$Local,[object]$Job) {
    $request=[pscustomobject]@{Action=$Action;Database=$Job.Database;VoucherType=$Job.VoucherType;VoucherCodes=$Job.VoucherCodes}
    Save-Json $request (Join-Path $ViewRoot ($Action+'-request.json'))
    $workerScript=Join-Path $ServerRoot 'w.ps1'
    $workerRequest=$Action+'-request.json'
    $taskName='MupiOtherAmounts-'+$Job.Id+'-'+$Action
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
    $parsed=@(Convert-VoucherCodes @('| 0000005818 |','0000005818，0000005819','0000005821'))
    if($parsed.Count -ne 3 -or $parsed[0] -ne '0000005818'){throw 'Code parsing regression.'}
    $rejected=$false;try{Convert-VoucherCodes @('../bad')|Out-Null}catch{$rejected=$true}
    if(!$rejected){throw 'Unsafe voucher code accepted.'}
    Write-Host '输入检查通过。';return
}
if($Worker){Invoke-Worker;return}
try {
    if(!$VoucherType){while($VoucherType -notin @('08','09')){$choice=([string](Read-Host '单据类型：1=其他出库，2=其他入库')).Trim();if($choice -eq '1'){$VoucherType='09'}elseif($choice -eq '2'){$VoucherType='08'}}}
    $codes=@(Convert-VoucherCodes $VoucherCodes)
    if($codes.Count -eq 0) {
        Write-Host '请输入单号，逗号分隔或逐行粘贴；空行结束。'
        $inputLines=New-Object 'Collections.Generic.List[string]'
        while($true){$line=[string](Read-Host '单号');if([string]::IsNullOrWhiteSpace($line)){break};$inputLines.Add($line)}
        $codes=@(Convert-VoucherCodes $inputLines.ToArray())
    }
    if($codes.Count -eq 0){throw '没有输入单号。'}
    if($Server -notmatch '\A[A-Za-z0-9.-]+\z'){throw '服务器地址格式不正确。'}
    $job=[pscustomobject]@{Id=[guid]::NewGuid().ToString('N');Database=$Database;VoucherType=$VoucherType;VoucherCodes=$codes}
    $serverRoot='D:\MupiOtherVoucherAmountJobs\'+$job.Id
    $local=Test-Path 'D:\MaterialOutboundApi\App_Data\settings.json'
    $viewRoot=if($local){$serverRoot}else{'\\'+$Server+'\D$\MupiOtherVoucherAmountJobs\'+$job.Id}
    New-Item -ItemType Directory -Path $viewRoot -Force | Out-Null
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $viewRoot 'w.ps1')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OtherVoucherAmountEngine.cs') -Destination $viewRoot
    $preview=Invoke-Job 'Preview' $viewRoot $serverRoot $local $job
    foreach($item in @($preview.Results)) {
        if(!$item.Success){Write-Host ($item.VoucherCode+'：'+$item.Error) -ForegroundColor Yellow;continue}
        Write-Host ('{0}：{1} 行，补金额 {2} 行（其中零单价 {3} 行），缺单价/数量 {4} 行' -f $item.VoucherCode,$item.Lines,$item.AmountsFilled,$item.ZeroPriceAmounts,$item.MissingInputsLines)
        foreach($detail in @($item.MissingInputsDetails)){Write-Host ('  '+$detail) -ForegroundColor Yellow}
    }
    if($PreviewOnly){Write-Host '只查询，未保存。';if(@($preview.Results|Where-Object {!$_.Success}).Count -gt 0){exit 1};return}
    if(@($preview.Results|Where-Object {!$_.Success}).Count -gt 0){throw '有单据无法处理，请更正单号或状态后再运行。未保存任何单据。'}
    if(!$Apply -and ([string](Read-Host '输入 SAVE 保存，不审核')).Trim().ToUpperInvariant() -ne 'SAVE'){Write-Host '已取消，未保存。';return}
    $saved=Invoke-Job 'Save' $viewRoot $serverRoot $local $job
    foreach($item in @($saved.Results)){
        if($item.Success){Write-Host ('{0}：{1}，补金额 {2} 行（其中零单价 {3} 行），缺单价/数量 {4} 行' -f $item.VoucherCode,$(if($item.Status -eq 'NoChangesNeeded'){'无需修改'}else{'保存成功'}),$item.AmountsFilled,$item.ZeroPriceAmounts,$item.MissingInputsLines)}
        else{Write-Host ($item.VoucherCode+'：'+$item.Error) -ForegroundColor Red}
    }
    Write-Host ('日志：'+$viewRoot)
    if(@($saved.Results|Where-Object {!$_.Success}).Count -gt 0){exit 1}
}catch{Write-Host $_.Exception.Message -ForegroundColor Red;exit 1}
