param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$workspaceDirectory = Split-Path -Parent $projectDirectory

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspaceDirectory "artifacts\MaterialOutboundApi"
}

$outputFullPath = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsFullPath = [IO.Path]::GetFullPath(
    (Join-Path $workspaceDirectory "artifacts"))
if (-not $outputFullPath.StartsWith(
        $artifactsFullPath + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must be inside the workspace artifacts directory."
}

$flatPublish = Join-Path $artifactsFullPath "_MaterialOutboundApi-flat"
foreach ($target in @($outputFullPath, $flatPublish)) {
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

dotnet publish (Join-Path $projectDirectory "MaterialOutboundApi.csproj") `
    -c $Configuration `
    -o $flatPublish
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$bin = Join-Path $outputFullPath "bin"
New-Item -ItemType Directory -Path $bin -Force | Out-Null
New-Item -ItemType Directory -Path (
    Join-Path $outputFullPath "App_Data\Logs") -Force | Out-Null

Get-ChildItem -LiteralPath $flatPublish -File |
    Where-Object {
        $_.Extension -in @(".dll", ".exe", ".pdb", ".xml") -and
        $_.Name -ne "MaterialOutboundConsole.exe.config"
    } |
    Copy-Item -Destination $bin -Force

Copy-Item (Join-Path $projectDirectory "Web.config") $outputFullPath -Force
Copy-Item (Join-Path $projectDirectory "Global.asax") $outputFullPath -Force
Copy-Item (Join-Path $projectDirectory "Create-U8InventoryCount.cmd") `
    $outputFullPath -Force
Copy-Item (Join-Path $projectDirectory "Edit-U8InventoryCount.cmd") `
    $outputFullPath -Force
Copy-Item (Join-Path $projectDirectory "Update-U8OtherVoucherPrices.cmd") `
    $outputFullPath -Force
Copy-Item (Join-Path $projectDirectory "Repair-U8OtherVoucherAmounts.cmd") `
    $outputFullPath -Force
Copy-Item (Join-Path $projectDirectory "Edit-U8OtherVoucherLinePrice.cmd") `
    $outputFullPath -Force
Copy-Item (Join-Path $projectDirectory "Import-U8InventoryCountExcel.cmd") `
    $outputFullPath -Force
Copy-Item (Join-Path $projectDirectory "Clear-U8OtherVoucherPriceMemos.cmd") `
    $outputFullPath -Force
Copy-Item (Join-Path $projectDirectory "README.md") `
    (Join-Path $outputFullPath "API-README.md") -Force
Copy-Item (Join-Path $workspaceDirectory "GeneralMaterialOutbound\README.md") `
    (Join-Path $outputFullPath "GENERAL-MATERIAL-OUTBOUND-API.md") -Force
Copy-Item (Join-Path $workspaceDirectory "GeneralInventoryCount\README.md") `
    (Join-Path $outputFullPath "GENERAL-INVENTORY-COUNT-API.md") -Force
Copy-Item (Join-Path $projectDirectory "Admin") `
    (Join-Path $outputFullPath "Admin") -Recurse -Force
Copy-Item (Join-Path $workspaceDirectory "MaterialOutboundConsole\XML") `
    (Join-Path $outputFullPath "XML") -Recurse -Force
Copy-Item (Join-Path $projectDirectory "ShapeConversionXml") `
    (Join-Path $outputFullPath "ShapeConversionXml") -Recurse -Force
Copy-Item (Join-Path $projectDirectory "Deployment") `
    (Join-Path $outputFullPath "Deployment") -Recurse -Force

# Windows PowerShell 5.1 treats UTF-8 files without a BOM as the local ANSI
# code page. Deployment scripts contain Chinese prompts, so normalize the
# packaged copies to UTF-8 BOM without changing the repository source files.
$utf8Bom = New-Object Text.UTF8Encoding($true)
Get-ChildItem -LiteralPath (Join-Path $outputFullPath "Deployment") `
    -File -Recurse | Where-Object { $_.Extension -in @('.ps1','.psm1') } |
    ForEach-Object {
        $content = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
        [IO.File]::WriteAllText($_.FullName, $content, $utf8Bom)
    }

Remove-Item -LiteralPath $flatPublish -Recurse -Force
Write-Host "IIS deployment package created: $outputFullPath"
