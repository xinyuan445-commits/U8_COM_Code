param(
    [string]$ExcelPath='',
    [ValidateSet('008','998')][string]$Database='008',
    [string]$DepartmentCode='',
    [string]$VoucherDate='',
    [string]$SettingsPath='D:\MaterialOutboundApi\App_Data\settings.json',
    [string]$ApiBaseUrl='http://localhost:9879/api/v1',
    [string]$ImportBatch='',
    [string]$LogRoot='',
    [switch]$DryRun
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Console]::InputEncoding=[Text.Encoding]::UTF8
[Console]::OutputEncoding=[Text.Encoding]::UTF8
Import-Module (Join-Path $PSScriptRoot 'InventoryCountExcel.psm1') -Force -DisableNameChecking

function Unprotect-ImportText([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    Add-Type -AssemblyName System.Security
    $bytes=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($Value),[Text.Encoding]::UTF8.GetBytes('MaterialOutboundApi.Settings.v1'),[Security.Cryptography.DataProtectionScope]::LocalMachine)
    try { return [Text.Encoding]::UTF8.GetString($bytes) } finally { [Array]::Clear($bytes,0,$bytes.Length) }
}
function Read-ImportEnv([string]$Path) {
    $values=@{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $text=$line.Trim(); if (!$text -or $text.StartsWith('#') -or !$text.Contains('=')) { continue }
        $parts=$text.Split(@('='),2,[StringSplitOptions]::None); $value=$parts[1].Trim()
        if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) { $value=$value.Substring(1,$value.Length-2) }
        $values[$parts[0].Trim()]=$value
    }
    return $values
}
function Get-ImportPlanHash($Requests) {
    return Get-CountHash ((@($Requests | ForEach-Object { [ordered]@{RequestNo=$_.RequestNo;BodyJson=$_.BodyJson;LineCount=$_.LineCount;SourceRows=$_.SourceRows} }) | ConvertTo-Json -Depth 25 -Compress))
}

$connection=$null; $builder=$null; $lockStream=$null; $sqlPassword=$null; $apiKey=$null
try {
    if (!$ExcelPath) { $ExcelPath=[string](Read-Host 'Excel 文件路径（可拖入文件）') }
    $ExcelPath=$ExcelPath.Trim().Trim('"')
    $ExcelPath=(Resolve-Path -LiteralPath $ExcelPath).Path
    $rows=@(Read-InventoryCountExcel $ExcelPath)
    $explicitDate=$PSBoundParameters.ContainsKey('VoucherDate')
    $explicitDepartment=$PSBoundParameters.ContainsKey('DepartmentCode')
    if ($ImportBatch -and $ImportBatch -notmatch '\A[A-Za-z0-9_-]{1,40}\z') { throw '导入批次只允许字母、数字、下划线和短横线，最多40位。' }
    if (Test-Path -LiteralPath $SettingsPath -PathType Leaf) {
        $stored=Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $sqlServer=[string]$stored.Public.SqlServer; $sqlUser=[string]$stored.Public.SqlUser
        $sqlPassword=Unprotect-ImportText ([string]$stored.SqlPasswordProtected)
        $apiKey=Unprotect-ImportText ([string]$stored.ApiKeyProtected)
    } else {
        $workspaceRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $environmentPath=Join-Path $workspaceRoot '.env.local'
        if (!(Test-Path -LiteralPath $environmentPath -PathType Leaf)) { throw '没有找到 IIS 配置或本地连接配置。' }
        $environment=Read-ImportEnv $environmentPath
        $sqlServer=[string]$environment['DB_SERVER']; $sqlPort=[string]$environment['DB_PORT']
        if ($sqlPort -and $sqlPort -ne '1433' -and !$sqlServer.Contains(',')) { $sqlServer+=','+$sqlPort }
        $sqlUser=[string]$environment['DB_USER']; $sqlPassword=[string]$environment['DB_PASSWORD']; $apiKey=[string]$environment['MATERIAL_OUTBOUND_API_KEY']
        if (!$PSBoundParameters.ContainsKey('ApiBaseUrl') -and $environment['MATERIAL_OUTBOUND_API_BASE_URL']) { $ApiBaseUrl=[string]$environment['MATERIAL_OUTBOUND_API_BASE_URL'] }
    }
    if (!$sqlServer -or !$sqlUser -or !$sqlPassword -or !$apiKey) { throw 'SQL 或 API 连接配置不完整。' }
    $ApiBaseUrl=$ApiBaseUrl.TrimEnd('/')
    $apiUri=$null
    if (![Uri]::TryCreate($ApiBaseUrl,[UriKind]::Absolute,[ref]$apiUri) -or $apiUri.Scheme -notin @('http','https') -or $apiUri.UserInfo -or $apiUri.Query -or $apiUri.Fragment) { throw 'API 地址格式不正确。' }
    $databaseName='UFDATA_'+$Database+'_2021'
    $fileHash=(Get-FileHash -LiteralPath $ExcelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $importId=Get-CountHash (@($fileHash,$databaseName,$ApiBaseUrl.ToLowerInvariant(),$ImportBatch) | ConvertTo-Json -Compress)
    if (!$LogRoot) {
        $LogRoot=if (Test-Path -LiteralPath $SettingsPath -PathType Leaf) { Join-Path (Split-Path -Parent $SettingsPath) 'InventoryExcelImports' } else { Join-Path $env:LOCALAPPDATA 'MaterialOutboundApi\InventoryExcelImports' }
    }
    $jobRoot=Join-Path $LogRoot $importId
    [void](New-Item -ItemType Directory -Path $jobRoot -Force)
    # File lock covers the entire preview/confirmation/save session on this host.
    try { $lockStream=New-Object IO.FileStream (Join-Path $jobRoot 'import.lock'),([IO.FileMode]::OpenOrCreate),([IO.FileAccess]::ReadWrite),([IO.FileShare]::None) }
    catch { throw '同一文件正在导入，请先关闭另一处导入窗口。' }
    $statePath=Join-Path $jobRoot 'state.json'
    if (Test-Path -LiteralPath $statePath) {
        $state=Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($state.Version -ne 1 -or $state.ImportId -cne $importId -or $state.FileHash -cne $fileHash -or $state.Database -cne $databaseName -or $state.ApiBaseUrl -cne $ApiBaseUrl -or $state.PlanHash -cne (Get-ImportPlanHash $state.Requests)) { throw '导入记录不一致，已停止，请保留日志核对。' }
        if (($explicitDate -and $VoucherDate -cne $state.VoucherDate) -or ($explicitDepartment -and $DepartmentCode -cne $state.DepartmentCode)) { throw '同一导入记录的日期或默认部门不同，已停止。' }
        $VoucherDate=$state.VoucherDate; $DepartmentCode=$state.DepartmentCode
        if (@($state.Requests | Where-Object { $_.Status -notin @('Ready','Created') }).Count -gt 0) { throw "有单据结果待确认，请先在 U8 核对。日志：$statePath。不要更改文件或批次后重导。" }
    } else {
        if (!$VoucherDate) { $VoucherDate=(Get-Date).ToString('yyyy-MM-dd') }
        $date=[datetime]::MinValue
        if (![datetime]::TryParseExact($VoucherDate,'yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$date)) { throw '日期须为 yyyy-MM-dd。' }
        if (@($rows | Where-Object { !$_.Department }).Count -gt 0 -and !$DepartmentCode) { $DepartmentCode=([string](Read-Host '默认部门编码')).Trim() }
        $builder=New-Object Data.SqlClient.SqlConnectionStringBuilder
        $builder['Data Source']=$sqlServer; $builder['Initial Catalog']=$databaseName; $builder['User ID']=$sqlUser; $builder['Password']=$sqlPassword
        $builder['Encrypt']=$false; $builder['TrustServerCertificate']=$true; $builder['Connect Timeout']=15
        $connection=New-Object Data.SqlClient.SqlConnection $builder.ConnectionString; $connection.Open()
        $resolved=@(Resolve-InventoryCountRows $rows $connection)
        $requests=@(New-InventoryCountRequests $resolved $DepartmentCode $VoucherDate $importId)
        $connection.Dispose(); $connection=$null; $builder.Clear()
        $state=[pscustomobject]@{Version=1;ImportId=$importId;FileHash=$fileHash;Database=$databaseName;ApiBaseUrl=$ApiBaseUrl;VoucherDate=$VoucherDate;DepartmentCode=$DepartmentCode;PlanHash=(Get-ImportPlanHash $requests);Requests=$requests}
        Write-CountJson $state $statePath
    }
    $pending=@($state.Requests | Where-Object Status -eq 'Ready')
    Write-Host ('账套：{0}；日期：{1}；共 {2} 行，{3} 张盘点单，已成功 {4} 张' -f $Database,$VoucherDate,$rows.Count,@($state.Requests).Count,(@($state.Requests).Count-$pending.Count))
    foreach ($request in @($state.Requests)) { Write-Host ('分单 {0}；仓库 {1}；部门 {2}；{3} 行；{4}' -f $request.Group,$request.Warehouse,$request.Department,$request.LineCount,$(if ($request.Status -eq 'Created') { $request.VoucherCode } else { '待导入' })) }
    Write-Host ('导入记录：'+$statePath)
    if ($pending.Count -eq 0) { Write-Host '此文件已全部创建，不重复生成。'; return }
    $headers=@{'X-API-Key'=$apiKey;'X-U8-Database'=$databaseName}
    $number=0
    foreach ($request in $pending) {
        $number++; Write-Host ('正在校验第 {0}/{1} 张……' -f $number,$pending.Count)
        $validation=Invoke-CountApi ($ApiBaseUrl+'/general-inventory-count-orders/validate') $headers $request.BodyJson
        if (!$validation.valid) { throw '校验未通过，未创建本次待导入单据。' }
        foreach ($warning in @($validation.warnings)) { if ($warning) { Write-Host $warning -ForegroundColor Yellow } }
    }
    if ($DryRun) { Write-Host '校验通过，只预览，未创建盘点单。'; return }
    if (([string](Read-Host '确认创建未审核盘点单？输入 CREATE 继续')).Trim().ToUpperInvariant() -ne 'CREATE') { Write-Host '已取消，未创建。'; return }
    Invoke-CountImportSave $state $statePath $ApiBaseUrl $headers
    Write-Host '导入完成，均未审核。'
} catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }
finally {
    if ($null -ne $connection) { $connection.Dispose() }
    if ($null -ne $builder) { $builder.Clear() }
    if ($null -ne $lockStream) { $lockStream.Dispose() }
    $sqlPassword=$null; $apiKey=$null
}
