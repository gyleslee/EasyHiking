param(
    [string]$SourceRoot = 'F:\Codex\Projects\2026-09-24\f-hiking-guide-excel-brouter-fathomdem\work\HikingGuide',
    [string]$PublishRoot = 'F:\Codex\Temp\HikingGuide-P81-publish',
    [string]$TargetRoot = 'F:\Hiking Guide',
    [string]$BackupRoot = 'F:\Codex\Temp\HikingGuide-P81-predeploy-20260929'
)

$ErrorActionPreference = 'Stop'
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$PublishRoot = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
$TargetRoot = [IO.Path]::GetFullPath($TargetRoot).TrimEnd('\')
$BackupRoot = [IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
$AppTarget = Join-Path $TargetRoot '易行徒步规划制作器'
if (-not $TargetRoot.Equals('F:\Hiking Guide', [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝部署到非指定项目目录：$TargetRoot"
}
if (-not $BackupRoot.StartsWith('F:\Codex\Temp\HikingGuide-P81-predeploy-',
    [StringComparison]::OrdinalIgnoreCase)) { throw "备份目录不在预期范围：$BackupRoot" }
foreach ($required in @($SourceRoot, $PublishRoot, $TargetRoot, $AppTarget)) {
    if (-not (Test-Path -LiteralPath $required -PathType Container)) { throw "目录不存在：$required" }
}
if (Get-Process -Name '易行徒步规划制作器' -ErrorAction SilentlyContinue) {
    throw '软件仍在运行，请先关闭后部署。'
}

$sourceFiles = @(
    'src\HikingGuide.Core\Elevation\ElevationModels.cs',
    'src\HikingGuide.Core\Elevation\ElevationChartViewport.cs',
    'src\HikingGuide.Core\Elevation\ElevationChartRuns.cs',
    'src\HikingGuide.Application\Elevation\ElevationChartStyle.cs',
    'src\HikingGuide.Infrastructure\Elevation\ElevationAnalysisCoordinator.cs',
    'src\HikingGuide.App\MainWindow.xaml',
    'src\HikingGuide.App\MainWindow.xaml.cs',
    'src\HikingGuide.App\ElevationAnalysisControl.xaml',
    'src\HikingGuide.App\ElevationAnalysisControl.xaml.cs',
    'src\HikingGuide.App\App.xaml.cs',
    'src\HikingGuide.App\ViewModels\MainViewModel.cs',
    'tests\HikingGuide.Core.Tests\ElevationChartViewportTests.cs',
    'tests\HikingGuide.Application.Tests\ElevationChartStyleTests.cs',
    'tests\HikingGuide.Infrastructure.Tests\ElevationPersistenceTests.cs',
    'tools\HikingGuide.P8Verifier\Program.cs',
    'tools\DeployP81.ps1',
    'docs\高程对比面板.md',
    'docs\高程分析.md',
    'docs\P8-1阶段验证报告.md'
)
foreach ($relative in $sourceFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot $relative) -PathType Leaf)) {
        throw "部署源文件不存在：$relative"
    }
}
$published = @(Get-ChildItem -LiteralPath $PublishRoot -File -Recurse | Where-Object {
    $relative = $_.FullName.Substring($PublishRoot.Length + 1)
    -not ($relative.StartsWith('BRouter\', [StringComparison]::OrdinalIgnoreCase) -or
          $relative.StartsWith('logs\', [StringComparison]::OrdinalIgnoreCase))
})
if ($published.Count -lt 200) { throw '发布文件数量异常，拒绝覆盖正式程序。' }

function Copy-BackedUpFile([string]$source, [string]$target, [string]$relative) {
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $backup = Join-Path $BackupRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $backup -Force
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
        throw "部署文件校验失败：$relative"
    }
}

foreach ($relative in $sourceFiles) {
    Copy-BackedUpFile (Join-Path $SourceRoot $relative) (Join-Path $TargetRoot $relative) $relative
}
foreach ($file in $published) {
    $relative = $file.FullName.Substring($PublishRoot.Length + 1)
    Copy-BackedUpFile $file.FullName (Join-Path $AppTarget $relative) (Join-Path '易行徒步规划制作器' $relative)
}

$reportNames = @(
    'HikingGuide-P81-long-final.json',
    'HikingGuide-P81-release-smoke.json',
    'HikingGuide-P81-release-smoke-真实长轨迹.png',
    'HikingGuide-P81-release-smoke-P81双图悬停.png'
)
foreach ($name in $reportNames) {
    $source = Join-Path 'F:\Codex\Temp' $name
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        $target = Join-Path (Join-Path $TargetRoot 'P8-1') $name
        Copy-BackedUpFile $source $target (Join-Path 'P8-1' $name)
    }
}
[pscustomobject]@{
    SourceFiles = $sourceFiles.Count
    PublishedFiles = $published.Count
    BackupRoot = $BackupRoot
    TargetRoot = $TargetRoot
    BRouterFilesCopied = 0
} | ConvertTo-Json -Depth 3
