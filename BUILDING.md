# 从源码编译

## 目录

- `src`：Windows 10/11 主程序、应用层、核心模型与计算、基础设施。
- `tests`：xUnit 测试及 Web 地图测试。
- `compat/windows7`：Windows 7 兼容分支，保留独立项目与依赖版本。
- `docs`：操作说明及截图。
- `packaging`：安装器源码及打包说明。
- `tools`：校准、验证和构建辅助工具。

## 环境准备

在 Windows 64 位开发电脑安装 .NET SDK 10.0.401（或 global.json 允许的补丁版本）、Git；开发 WPF 可使用支持该 SDK 的 Visual Studio 或命令行。运行地图需要 WebView2。首次还原需要访问 NuGet。

Windows 7 分支的目标框架为 .NET 6；可以在现代 Windows 开发电脑上编译，目标系统仍需单独验收。不要把在 Win11 上运行兼容分支测试等同于 Win7 实机测试。

```powershell
git clone https://github.com/gyleslee/EasyHiking.git
cd EasyHiking
dotnet restore HikingGuide.sln
dotnet build HikingGuide.sln -c Release --no-restore
dotnet test HikingGuide.sln -c Release --no-restore
```

V1.9.5 已固定 SQLite 原生库 3.53.3，安全修复及验证见 SECURITY.md。保留默认依赖审计；不要为通过构建而关闭审计或忽略新的 NU1903。若所在网络无法读取 NuGet 漏洞服务，NU1900 应作为网络问题单独处理，不能当作审计通过。

Windows7分支停留在V1.9.5，后续版本暂不更新支持；以下仅为历史源码的单独测试方式：

```powershell
dotnet test compat/windows7/HikingGuide.sln -c Release
```

## 发布程序

```powershell
# Windows 10/11，依赖目标电脑的 .NET 10 Desktop Runtime。
dotnet publish src/HikingGuide.App/HikingGuide.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/modern

# Windows 7 兼容分支，自包含 .NET 6。
dotnet publish compat/windows7/src/HikingGuide.App/HikingGuide.App.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=6.0.36 -o artifacts/windows7
```

Excel 模板随 Infrastructure 项目发布到 `Templates/攻略数据模板.xlsx`。不要修改模板结构来规避导出问题。

Windows 10/11 的正式安装载荷还需生成中文无控制台启动器：

```powershell
./packaging/Build-Launcher.ps1 -PayloadDirectory artifacts/modern -Version 1.9.6
```

该入口调用系统 x64 dotnet.exe 加载原程序集，不增加 GPU 禁用参数。命令行直接运行程序集也使用同一标准机制：

```powershell
dotnet exec artifacts/modern/易行徒步规划制作器.dll
```

## 路由与运行环境

仓库保留 BRouter 1.7.10 引擎、MIT 声明及既定徒步路由配置。Java 运行环境不提交到源码仓库。可将适用的 Windows x64 Java 运行环境放入发布目录的 `BRouter/runtime/java`，或将 `HIKINGGUIDE_BROUTER_JAVA_EXE` 指向实际 `java.exe`。现代版原发行载荷使用 Temurin 21；Win7 请使用经过目标系统验证的 Java 11 版本。外部分发时保留 Java 的许可和源码获取信息。

OSM 路网读取器源码在 `src/HikingGuide.App/RoadNetworkRuntime/OsmRoadReader.java`，随仓库提供对应 JAR。使用 JDK 可重建：

```powershell
$runtime = 'src/HikingGuide.App/BRouterRuntime'
New-Item -ItemType Directory -Force artifacts/java-classes
javac -encoding UTF-8 -cp "$runtime/brouter-1.7.10-all.jar" -d artifacts/java-classes src/HikingGuide.App/RoadNetworkRuntime/OsmRoadReader.java
jar --create --file src/HikingGuide.App/RoadNetworkRuntime/OsmRoadReader.jar -C artifacts/java-classes .
```

DEM、rd5 路由数据、个人图源配置及离线地图均需用户自行准备，不从开发者电脑复制。3D、DEM 派生图层和离线路网须在数据配置完成后使用。

## 文档和公开安装包

原生应用从程序目录 `docs/易行徒步规划制作器-操作说明.html` 打开说明。自行发布时把本仓库 `docs` 复制到发布目录。

GitHub Releases 的既有 T1.9.2 安装包按维护者要求原样提供，可能包含与源码内置示例不同的预置图源；这些个人预置图源列表不在源码中。重建通用软件不需要它们。源码及安装脚本不保证与历史安装包逐字节一致，但保留本版本应用代码、算法、模板及第三方组件版本。

## Windows11 ARM64安装包

本版ARM包仍发布win-x64程序，经Windows11系统模拟运行，共用.NET x64运行时；只将WebView2离线依赖改为ARM64。不能将RuntimeIdentifier改成win-arm64就宣称完成原生移植：GDAL、Java等原生依赖需要另行适配。详见packaging/README.md。
