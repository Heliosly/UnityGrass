# Unity 6 batchmode 冒烟测试。运行前关闭当前项目的 Unity 编辑器，避免项目锁。
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$unityExe = "C:\Program Files\Unity\Hub\Editor\6000.3.23f1\Editor\Unity.exe"
$logPath = Join-Path $projectRoot "Tools\smoke_test_unity.log"
$reportPath = Join-Path $projectRoot "Assets\TerrainGrassDemo\SmokeTestReport.txt"

if (-not (Test-Path -LiteralPath $unityExe)) { Write-Error "找不到 Unity 6000.3.23f1: $unityExe" }
Remove-Item -LiteralPath $reportPath -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $logPath -ErrorAction SilentlyContinue

$arguments = @(
    "-batchmode", "-projectPath", $projectRoot,
    "-executeMethod", "TerrainGrassDemo.EditorTools.PlayModeSmokeTest.Run",
    "-force-d3d11", "-logFile", $logPath
)
$process = Start-Process -FilePath $unityExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
$deadline = (Get-Date).AddMinutes(6)
while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $reportPath)) { Start-Sleep -Seconds 3 }

if (Test-Path -LiteralPath $reportPath) {
    Get-Content -LiteralPath $reportPath
} else {
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 40 }
    throw "冒烟测试超时，未生成报告。"
}
if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
