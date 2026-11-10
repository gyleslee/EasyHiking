param(
    [string]$SourceRoot = 'F:\Codex\Projects\2026-09-24\f-hiking-guide-excel-brouter-fathomdem\work\HikingGuide',
    [string]$PublishRoot = 'F:\Codex\Temp\HikingGuide-P8-publish',
    [string]$TargetRoot = 'F:\Hiking Guide',
    [string]$BackupRoot = 'F:\Codex\Temp\HikingGuide-P8-predeploy-20260928'
)

$ErrorActionPreference = 'Stop'
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$PublishRoot = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
$TargetRoot = [IO.Path]::GetFullPath($TargetRoot).TrimEnd('\')
$BackupRoot = [IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
$AppTarget = Join-Path $TargetRoot '易行徒步规划制作器'

foreach ($required in @($SourceRoot, $PublishRoot, $TargetRoot, $AppTarget)) {
    if (-not (Test-Path -LiteralPath $required -PathType Container)) {
        throw "目录不存在：$required"
    }
}
if (-not $TargetRoot.Equals('F:\Hiking Guide', [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝部署到非指定项目目录：$TargetRoot"
}
if (-not $BackupRoot.StartsWith('F:\Codex\Temp\HikingGuide-P8-predeploy-', [StringComparison]::OrdinalIgnoreCase)) {
    throw "备份目录不在预期范围：$BackupRoot"
}
if (Get-Process -Name '易行徒步规划制作器' -ErrorAction SilentlyContinue) {
    throw '软件仍在运行，请先关闭后部署。'
}

$sourceFiles = @(
    'README.md',
    'src\HikingGuide.Core\Elevation\ElevationModels.cs',
    'src\HikingGuide.Core\Elevation\StandardElevationAnalysisV1.cs',
    'src\HikingGuide.Core\Models\HikingProject.cs',
    'src\HikingGuide.Application\Projects\ProjectValidator.cs',
    'src\HikingGuide.Application\Maps\MapProtocol.cs',
    'src\HikingGuide.Infrastructure\HikingGuide.Infrastructure.csproj',
    'src\HikingGuide.Infrastructure\Elevation\DemModels.cs',
    'src\HikingGuide.Infrastructure\Elevation\DemGdalRuntime.cs',
    'src\HikingGuide.Infrastructure\Elevation\DemDatabaseScanner.cs',
    'src\HikingGuide.Infrastructure\Elevation\DemDatabaseCatalog.cs',
    'src\HikingGuide.Infrastructure\Elevation\DemElevationProvider.cs',
    'src\HikingGuide.Infrastructure\Elevation\ElevationAnalysisCoordinator.cs',
    'src\HikingGuide.Infrastructure\Projects\ProjectArchiveModels.cs',
    'src\HikingGuide.Infrastructure\Projects\ProjectFileService.cs',
    'src\HikingGuide.App\HikingGuide.App.csproj',
    'src\HikingGuide.App\App.xaml.cs',
    'src\HikingGuide.App\MainWindow.xaml',
    'src\HikingGuide.App\MainWindow.xaml.cs',
    'src\HikingGuide.App\Map\Web\map.js',
    'src\HikingGuide.App\ViewModels\MapViewModel.cs',
    'src\HikingGuide.App\ViewModels\MainViewModel.cs',
    'src\HikingGuide.App\DemDatabaseWindow.xaml',
    'src\HikingGuide.App\DemDatabaseWindow.xaml.cs',
    'src\HikingGuide.App\ElevationAnalysisControl.xaml',
    'src\HikingGuide.App\ElevationAnalysisControl.xaml.cs',
    'tests\HikingGuide.Core.Tests\ElevationAnalysisTests.cs',
    'tests\HikingGuide.Infrastructure.Tests\DemDatabaseTests.cs',
    'tests\HikingGuide.Infrastructure.Tests\ElevationPersistenceTests.cs',
    'tools\HikingGuide.P8Verifier\HikingGuide.P8Verifier.csproj',
    'tools\HikingGuide.P8Verifier\Program.cs',
    'tools\DeployP8.ps1',
    'docs\DEM数据库管理.md',
    'docs\高程分析.md',
    'docs\高程对比面板.md',
    'docs\P8阶段验证报告.md',
    'docs\数据模型.md',
    'docs\项目文件格式.md'
)

function Copy-BackedUpFile([string]$source, [string]$target, [string]$relative) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "源文件不存在：$source"
    }
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $backup = Join-Path $BackupRoot $relative
        $backupDir = Split-Path -Parent $backup
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $backup -Force
    }
    $targetDir = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
}

foreach ($relative in $sourceFiles) {
    Copy-BackedUpFile (Join-Path $SourceRoot $relative) (Join-Path $TargetRoot $relative) $relative
}

$published = Get-ChildItem -LiteralPath $PublishRoot -File -Recurse | Where-Object {
    $relative = $_.FullName.Substring($PublishRoot.Length + 1)
    -not ($relative.StartsWith('BRouter\', [StringComparison]::OrdinalIgnoreCase) -or
          $relative.StartsWith('logs\', [StringComparison]::OrdinalIgnoreCase))
}
foreach ($file in $published) {
    $relative = $file.FullName.Substring($PublishRoot.Length + 1)
    Copy-BackedUpFile $file.FullName (Join-Path $AppTarget $relative) (Join-Path '易行徒步规划制作器' $relative)
}

foreach ($name in @('HikingGuide-P8-publish-long.json', 'HikingGuide-P8-three-dem-persist.json', 'HikingGuide-P8-publish-long-真实长轨迹.png')) {
    $report = Join-Path 'F:\Codex\Temp' $name
    if (Test-Path -LiteralPath $report -PathType Leaf) {
        $reportTarget = Join-Path (Join-Path $TargetRoot 'P8') $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $reportTarget) -Force | Out-Null
        Copy-Item -LiteralPath $report -Destination $reportTarget -Force
    }
}

$keyFiles = @('易行徒步规划制作器.exe', 'HikingGuide.Core.dll', 'HikingGuide.Infrastructure.dll', 'ScottPlot.dll')
foreach ($name in $keyFiles) {
    $expected = (Get-FileHash -LiteralPath (Join-Path $PublishRoot $name) -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath (Join-Path $AppTarget $name) -Algorithm SHA256).Hash
    if ($expected -ne $actual) { throw "部署校验失败：$name" }
}

[pscustomobject]@{
    SourceFiles = $sourceFiles.Count
    PublishedFiles = @($published).Count
    BackupRoot = $BackupRoot
    TargetRoot = $TargetRoot
    BRouterRoutingDataPreserved = $true
} | ConvertTo-Json -Depth 3
