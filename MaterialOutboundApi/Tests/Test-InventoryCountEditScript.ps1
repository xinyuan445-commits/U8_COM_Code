param([switch]$Cancel)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$global:inventoryEditTestInputs = New-Object 'Collections.Generic.Queue[string]'
$global:inventoryEditTestCancel = [bool]$Cancel
if ($Cancel) {
    @('1','4','test','0','','cancel') | ForEach-Object { $global:inventoryEditTestInputs.Enqueue($_) }
} else {
    @('1,2','1','-1','4.5','2','NEWBATCH','3','1102','4','复盘','5','更正备注','0',
      '4','-','1','0','0','1','5','最后备注','0','','UPDATE') |
        ForEach-Object { $global:inventoryEditTestInputs.Enqueue($_) }
}
function global:Read-Host {
    param([string]$Prompt)
    if ($global:inventoryEditTestInputs.Count -eq 0) { throw "Unexpected prompt: $Prompt" }
    return $global:inventoryEditTestInputs.Dequeue()
}
$global:inventoryEditTestPosted = $false
function global:Invoke-RestMethod {
    param($Method, $Uri, $Headers, $TimeoutSec, $ContentType, $Body)
    if ($Method -eq 'Get') {
        return @'
{"data":{"isVerified":false,"voucherCode":"MOCK","warehouseCode":"W","maker":"Tester","lines":[
{"lineNo":1,"inventoryCode":"A","inventoryName":"Test","specification":"","batchCode":"B","positionName":"1101","positionCode":"408","bookQuantity":10,"actualQuantity":1,"differenceQuantity":-9,"unitName":"PC","reason":"old reason","remark":"old remark","lineVersion":"11:0001"},
{"lineNo":2,"inventoryCode":"B","inventoryName":"Test2","specification":"","batchCode":"B2","positionName":"1102","positionCode":"409","bookQuantity":2,"actualQuantity":2,"differenceQuantity":0,"unitName":"PC","reason":"old reason","remark":"","lineVersion":"12:0002"}]}}
'@ | ConvertFrom-Json
    }
    if ($global:inventoryEditTestCancel) { throw 'Cancellation must not submit' }
    $global:inventoryEditTestPosted = $true
    if (-not $Uri.EndsWith('/line-updates')) { throw 'Wrong route' }
    $request = [Text.Encoding]::UTF8.GetString($Body) | ConvertFrom-Json
    if (@($request.lines).Count -ne 2) { throw 'Wrong line count' }
    $line = $request.lines[0]
    if ($line.actualQuantity -ne 4.5 -or $line.batchCode -ne 'NEWBATCH' -or
        $line.positionName -ne '1102' -or $line.reason -ne '复盘' -or
        $line.remark -ne '最后备注' -or $line.expectedLineVersion -ne '11:0001' -or
        $line.expectedActualQuantity -ne 1 -or -not $line.updatePosition -or
        -not $line.updateBatchCode -or -not $line.updateReason -or -not $line.updateRemark) {
        throw 'Wrong payload or repeated editing lost changes'
    }
    if ($request.lines[1].actualQuantity -ne 0 -or $request.lines[1].reason -ne '' -or
        -not $request.lines[1].updateReason) { throw 'Clear/zero was lost' }
    return @{ data = @{ lines = @(
        @{lineNo=1; inventoryCode='A'; batchCode='NEWBATCH'; positionName='1102'; actualQuantity=4.5; reason='复盘'; remark='最后备注'},
        @{lineNo=2; inventoryCode='B'; batchCode='B2'; positionName='1102'; actualQuantity=0; reason=''; remark=''}
    ) } }
}
$testEnvironmentPath = Join-Path ([IO.Path]::GetTempPath()) `
    ("u8-com-edit-test-{0}.env" -f ([Guid]::NewGuid().ToString('N')))
[IO.File]::WriteAllText(
    $testEnvironmentPath,
    "MATERIAL_OUTBOUND_API_KEY=test-only-key`n" +
        "MATERIAL_OUTBOUND_API_BASE_URL=http://localhost:9879/api/v1`n",
    (New-Object Text.UTF8Encoding($false)))
try {
    & (Join-Path $PSScriptRoot '../Deployment/Edit-U8InventoryCount.ps1') `
        -SettingsPath '__mock_no_settings__' `
        -EnvironmentPath $testEnvironmentPath -VoucherCode 'MOCK'
}
finally {
    Remove-Item -LiteralPath $testEnvironmentPath -Force -ErrorAction SilentlyContinue
}
if ($Cancel) { Write-Host 'PASS: cancelled without submission.'; exit 0 }
if (-not $global:inventoryEditTestPosted) { throw 'Expected a confirmed submission' }
if ($global:inventoryEditTestInputs.Count -ne 0) { throw 'Unused inputs' }
Write-Host 'PASS: script field choices, repeated edits, multi-line, clear/zero and UTF-8 payload.'
