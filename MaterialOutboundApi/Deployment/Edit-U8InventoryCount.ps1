param(
    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",
    [string]$EnvironmentPath = "",
    [string]$ApiBaseUrl = "http://localhost:9879/api/v1",
    [ValidateSet("008", "998", "UFDATA_008_2021", "UFDATA_998_2021")]
    [string]$Database = "008",
    [string]$VoucherCode = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Unprotect-Text {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    Add-Type -AssemblyName System.Security
    $entropy = [Text.Encoding]::UTF8.GetBytes("MaterialOutboundApi.Settings.v1")
    $protected = [Convert]::FromBase64String($Value)
    $plain = [Security.Cryptography.ProtectedData]::Unprotect(
        $protected,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Text.Encoding]::UTF8.GetString($plain)
}

function Read-DotEnv {
    param([string]$Path)
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $text = ([string]$line).Trim()
        if (-not $text -or $text.StartsWith("#") -or
            -not $text.Contains("=")) {
            continue
        }
        $parts = $text.Split(@("="), 2, [StringSplitOptions]::None)
        $name = $parts[0].Trim()
        $value = $parts[1].Trim()
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$name] = $value
    }
    return $values
}

function Get-ApiErrorText {
    param([Management.Automation.ErrorRecord]$ErrorRecord)
    if ($null -ne $ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
        try {
            $detail = $ErrorRecord.ErrorDetails.Message | ConvertFrom-Json
            if ($detail.message) { return [string]$detail.message }
        } catch { }
    }
    $exception = $ErrorRecord.Exception
    try {
        $response = $exception.Response
        if ($null -ne $response) {
            $body = ""
            if ($null -ne $response.PSObject.Properties["Content"] -and
                $null -ne $response.Content) {
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
            elseif ($null -ne $response.PSObject.Methods["GetResponseStream"]) {
                $stream = $response.GetResponseStream()
                if ($null -ne $stream) {
                    $reader = New-Object IO.StreamReader $stream
                    try { $body = $reader.ReadToEnd() }
                    finally { $reader.Dispose() }
                }
            }
            if (-not [string]::IsNullOrWhiteSpace($body)) {
                try {
                    $payload = $body | ConvertFrom-Json
                    if (-not [string]::IsNullOrWhiteSpace(
                            [string]$payload.message)) {
                        return [string]$payload.message
                    }
                }
                catch {
                    # Preserve a non-JSON response below.
                }
                return $body.Trim()
            }
        }
    }
    catch {
        # Fall through to the exception message.
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$exception.Message)) {
        return [string]$exception.Message
    }
    return "接口没有返回错误详情，请查看服务器 API 日志。"
}

function Invoke-ApiGet {
    param([string]$Uri, [hashtable]$Headers)
    try {
        return Invoke-RestMethod -Method Get -Uri $Uri -Headers $Headers `
            -TimeoutSec 120
    }
    catch {
        throw "接口调用失败：$(Get-ApiErrorText $_)"
    }
}

function Invoke-ApiPost {
    param([string]$Uri, [hashtable]$Headers, [object]$Body)
    $json = $Body | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    try {
        return Invoke-RestMethod -Method Post -Uri $Uri -Headers $Headers `
            -ContentType "application/json; charset=utf-8" -Body $bytes `
            -TimeoutSec 300
    }
    catch {
        throw "接口调用失败：$(Get-ApiErrorText $_)"
    }
}

function Read-DecimalOrSkip {
    param([string]$Prompt)
    while ($true) {
        $text = ([string](Read-Host $Prompt)).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        [decimal]$value = 0
        if ([decimal]::TryParse(
                $text,
                [Globalization.NumberStyles]::AllowDecimalPoint,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$value) -and $value -ge 0) {
            return $value
        }
        Write-Host "请输入大于或等于 0 的数字，或直接回车跳过。" `
            -ForegroundColor Yellow
    }
}

try {
    if (Test-Path -LiteralPath $SettingsPath -PathType Leaf) {
        $stored = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
        $apiKey = Unprotect-Text ([string]$stored.ApiKeyProtected)
    }
    else {
        if ([string]::IsNullOrWhiteSpace($EnvironmentPath)) {
            $workspaceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
            $EnvironmentPath = Join-Path $workspaceRoot ".env.local"
        }
        if (-not (Test-Path -LiteralPath $EnvironmentPath -PathType Leaf)) {
            throw "既找不到 IIS 配置文件，也找不到仓库根目录 .env.local。"
        }
        $environment = Read-DotEnv $EnvironmentPath
        $apiKey = [string]$environment["MATERIAL_OUTBOUND_API_KEY"]
        $configuredApiBaseUrl = [string]$environment[
            "MATERIAL_OUTBOUND_API_BASE_URL"]
        if ($configuredApiBaseUrl) {
            $ApiBaseUrl = $configuredApiBaseUrl.TrimEnd('/')
        }
    }
    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        throw "没有找到盘点 API Key 配置。"
    }

    if ($Database -eq "008") { $Database = "UFDATA_008_2021" }
    elseif ($Database -eq "998") { $Database = "UFDATA_998_2021" }
    while ([string]::IsNullOrWhiteSpace($VoucherCode)) {
        $VoucherCode = ([string](Read-Host "请输入盘点单号")).Trim()
    }
    $VoucherCode = $VoucherCode.Trim()
    $headers = @{
        "X-API-Key" = $apiKey
        "X-U8-Database" = $Database
    }
    $escapedCode = [Uri]::EscapeDataString($VoucherCode)
    $voucherUrl = "$ApiBaseUrl/general-inventory-count-orders/$escapedCode"
    $voucher = Invoke-ApiGet $voucherUrl $headers
    if ($voucher.data.isVerified) {
        throw "盘点单已经审核，不能修改：$VoucherCode"
    }

    Write-Host ""
    Write-Host ("账套：{0}" -f $Database) -ForegroundColor Cyan
    Write-Host ("盘点单：{0}" -f $voucher.data.voucherCode)
    Write-Host ("仓库：{0}；制单人：{1}；明细：{2} 行" -f `
        $voucher.data.warehouseCode,
        $voucher.data.maker,
        @($voucher.data.lines).Count)

    $changes = @{}
    while ($true) {
        Write-Host ""
        $inputText = ([string](Read-Host `
            "请输入行号，多个用逗号分隔，直接回车结束")).Trim()
        if ([string]::IsNullOrWhiteSpace($inputText)) { break }
        $parts = @([regex]::Split($inputText, '[,，;；\s]+') |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        foreach ($part in $parts) {
            [int]$lineNo = 0
            if (-not [int]::TryParse($part, [ref]$lineNo) -or $lineNo -le 0) {
                Write-Host "行号无效：$part" -ForegroundColor Yellow
                continue
            }
            $found = @($voucher.data.lines | Where-Object {
                [int]$_.lineNo -eq $lineNo
            })
            if ($found.Count -ne 1) {
                Write-Host "盘点单中没有行号 $lineNo。" -ForegroundColor Yellow
                continue
            }
            $line = $found[0]
            if (-not $line.PSObject.Properties['lineVersion']) {
                throw "服务器接口版本过旧，请先部署 1.4.8 或更新版本。"
            }
            Write-Host ""
            Write-Host ("行号：{0}" -f $line.lineNo) -ForegroundColor Cyan
            Write-Host ("存货：{0} {1}" -f `
                $line.inventoryCode, $line.inventoryName)
            if (-not [string]::IsNullOrWhiteSpace([string]$line.specification)) {
                Write-Host ("规格：{0}" -f $line.specification)
            }
            Write-Host ("批号：{0}；货位：{1} {2}" -f `
                $line.batchCode, $line.positionName, $line.positionCode)
            Write-Host ("账面数量：{0:N6}；当前盘点数量：{1:N6}；差异：{2:N6} {3}" -f `
                [decimal]$line.bookQuantity,
                [decimal]$line.actualQuantity,
                [decimal]$line.differenceQuantity,
                $line.unitName)
            Write-Host ("盘点原因：{0}；行备注：{1}" -f $line.reason, $line.remark)
            if (-not $changes.ContainsKey($lineNo)) {
                $changes[$lineNo] = @{
                    Payload = [ordered]@{
                        lineNo = $lineNo
                        actualQuantity = [decimal]$line.actualQuantity
                        expectedActualQuantity = [decimal]$line.actualQuantity
                        expectedLineVersion = [string]$line.lineVersion
                    }
                    Display = @{}
                }
            }
            $change = $changes[$lineNo]
            while ($true) {
                Write-Host "1=盘点数量  2=批号  3=货位名称  4=盘点原因  5=行备注  8=撤销本行  0=下一行"
                $choice = ([string](Read-Host "请选择修改项（回车进入下一行）")).Trim()
                if ($choice -eq '' -or $choice -eq '0') { break }
                if ($choice -eq '8') {
                    $changes.Remove($lineNo)
                    Write-Host "已撤销本行待修改内容。" -ForegroundColor Yellow
                    break
                }
                if ($choice -eq '1') {
                    $value = Read-DecimalOrSkip "新的盘点数量（回车不改）"
                    if ($null -eq $value) { continue }
                    $change.Payload.actualQuantity = [decimal]$value
                    if ($value -eq [decimal]$line.actualQuantity) {
                        $change.Display.Remove('数量')
                    } else {
                        $change.Display['数量'] = "{0:N6} → {1:N6}" -f [decimal]$line.actualQuantity, $value
                    }
                } elseif ($choice -in @('2', '3', '4', '5')) {
                    $field = @{ '2'='batchCode'; '3'='positionName'; '4'='reason'; '5'='remark' }[$choice]
                    $label = @{ '2'='批号'; '3'='货位名称'; '4'='盘点原因'; '5'='行备注' }[$choice]
                    $flag = @{ '2'='updateBatchCode'; '3'='updatePosition'; '4'='updateReason'; '5'='updateRemark' }[$choice]
                    $limit = @{ '2'=60; '3'=100; '4'=120; '5'=255 }[$choice]
                    $prompt = "新的$label（回车不改"
                    if ($choice -ne '3') { $prompt += '，输入 - 清空' }
                    $value = ([string](Read-Host ($prompt + '）'))).Trim()
                    if ($value -eq '') { continue }
                    if ($choice -ne '3' -and $value -eq '-') { $value = '' }
                    if ($value.Length -gt $limit) {
                        Write-Host "最多输入 $limit 个字符。" -ForegroundColor Yellow
                        continue
                    }
                    $oldValue = [string]$line.$field
                    if ($value -eq $oldValue) {
                        $change.Payload.Remove($field)
                        $change.Payload.Remove($flag)
                        $change.Display.Remove($label)
                    } else {
                        $change.Payload[$field] = $value
                        $change.Payload[$flag] = $true
                        $change.Display[$label] = "$oldValue → $value"
                    }
                } else {
                    Write-Host "请选择 1～5、8 或 0。" -ForegroundColor Yellow
                    continue
                }
                foreach ($label in @('数量','批号','货位名称','盘点原因','行备注')) {
                    if ($change.Display.ContainsKey($label)) {
                        Write-Host ("待保存：{0} {1}" -f $label, $change.Display[$label]) -ForegroundColor Green
                    }
                }
            }
            if ($changes.ContainsKey($lineNo) -and $changes[$lineNo].Display.Count -eq 0) {
                $changes.Remove($lineNo)
            }
        }
    }

    if ($changes.Count -eq 0) {
        Write-Host "没有需要修改的行。" -ForegroundColor Yellow
        exit 0
    }
    $lineNumbers = @($changes.Keys | Sort-Object)
    Write-Host ""
    Write-Host "待修改内容：" -ForegroundColor Cyan
    $hasIdentityChange = $false
    foreach ($lineNo in $lineNumbers) {
        Write-Host "第 $lineNo 行："
        foreach ($label in @('数量','批号','货位名称','盘点原因','行备注')) {
            if ($changes[$lineNo].Display.ContainsKey($label)) {
                Write-Host ("  {0}：{1}" -f $label, $changes[$lineNo].Display[$label])
                if ($label -in @('批号','货位名称')) { $hasIdentityChange = $true }
            }
        }
    }
    if ($hasIdentityChange) {
        Write-Host "批号、货位修改仅改单据，保留原账面数量；请核对账实关系。" -ForegroundColor Yellow
    }

    $confirmation = ([string](Read-Host `
        "确认修改未审核盘点单？请输入 UPDATE 继续")).Trim()
    if (-not $confirmation.Equals(
            "UPDATE",
            [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "已取消，没有修改盘点单。" -ForegroundColor Yellow
        exit 0
    }

    $body = [ordered]@{
        lines = @($lineNumbers | ForEach-Object { $changes[$_].Payload })
    }
    $result = Invoke-ApiPost `
        "$voucherUrl/line-updates" $headers $body
    Write-Host ""
    Write-Host "盘点明细修改完成：" -ForegroundColor Green
    foreach ($line in @($result.data.lines)) {
        Write-Host ("第 {0} 行：{1}；批号 {2}；货位 {3}；盘点数量 {4:N6}" -f `
            $line.lineNo, $line.inventoryCode, $line.batchCode, $line.positionName, [decimal]$line.actualQuantity)
        Write-Host ("  原因：{0}；备注：{1}" -f $line.reason, $line.remark)
    }
}
catch {
    Write-Host ""
    Write-Host ("操作失败：{0}" -f $_.Exception.Message) `
        -ForegroundColor Red
    exit 1
}
