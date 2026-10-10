$ErrorActionPreference = "Stop"

$root = "D:\MupiMaterialOutboundV2Deploy"
$stagingPath = (
    Get-Content -LiteralPath (Join-Path $root "staging-path.txt") -Raw
).Trim()
if (-not $stagingPath.StartsWith(
        $root + "\package-",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected IIS staging path: $stagingPath"
}
& (Join-Path $root "Deploy-IisPackage.ps1") -StagingPath $stagingPath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
