# 安装器源码

`Common.iss` 与 `TargetSystem.iss`、`UserDataPolicy.iss` 是现有 V1.9.6 安装器的程序安装、系统判断和同盘用户数据管理逻辑。现代入口为Windows10-11.iss和Windows11-ARM64.iss；Windows7.iss仅保留历史源码，后续不再更新生成Windows7包。

不包含运行环境安装程序、开发者个人 Defaults/settings.json 或任何 API Key。公开源码保留应用内置的公开 OSM 示例；历史安装包由维护者按原文件另行上传，公开重建包不需要其个人图源配置。

## 准备与编译

1. 安装 Inno Setup 6.7.3，保留其许可。
2. 按 BUILDING.md 发布现代程序到 `PayloadModern`，普通x64与ARM包共用这一载荷；PayloadWin7只供历史重建。将合适的 Java 运行环境、必要的许可及相应源码获取信息分别放到载荷中；Win7 还需私有 WebView2 109。
3. 在本目录创建 `build`，放入 Inno Setup 对应的 `ChineseSimplified.isl`。
4. 将微软官方运行环境安装程序准备在自己指定的目录，生成 `build/Prerequisites.iss`。文件中分别定义 `PrereqRoot`、`DotNetSha256`、`WebViewSha256`、`ArmWebViewFile`、`ArmWebViewSha256`；后两者须为实际文件SHA-256。现代普通包需要windowsdesktop-runtime-10-win-x64.exe和MicrosoftEdgeWebView2RuntimeInstallerX64.exe；ARM包共用.NET x64，只嵌入ArmWebViewFile所指的MicrosoftEdgeWebView2RuntimeInstallerARM64.exe，ArmWebViewSha256为其SHA256，Win7需要 `VC_redist-2019-x64.exe`。应验证官方下载文件的签名及哈希。
5. 创建 `build/EasyHiking.Installation.ini`：

```ini
[Installation]
AppId=EasyHiking-Installed-0EE343F3-212F-4B44-ABBF-7FF0B80AB4E7
PackageVersion=1.9.6
DataMode=InstallDriveCurrentUser
```

6. 现代载荷先运行 `./packaging/Build-Launcher.ps1 -PayloadDirectory "你的载荷父目录\PayloadModern" -Version 1.9.6`，Win7 不替换启动入口。

7. 将操作说明放在 `ManualBase` 指定的目录，编译：

```powershell
iscc /DProductVersion=1.9.6 /DPayloadBase="你的载荷父目录" /DManualBase="你的源码目录\docs" packaging/Windows10-11.iss
iscc /DProductVersion=1.9.6 /DPayloadBase="你的载荷父目录" /DManualBase="你的源码目录\docs" packaging/Windows11-ARM64.iss
```

安装包在 packaging/output 生成。源码仓库不提交这些载荷和安装包二进制文件。Win10 1803 的已验证启动修复必须保留；不要把默认MinVersion恢复为10.0.17763，具体许可由TargetSystem.iss判断。

新用户默认值可在你自己的载荷 `Defaults/settings.json` 中设置。不要把个人settings.json直接复制到公开源码。原安装包本批默认OSM Germany、DEM等高线、30%阴影和OSM路网开启，地形夸张1.5；相应DEM和rd5仍须使用者自行设置。已有用户的设置不强制重置。

普通包架构为x64os，ARM包为arm64且最低Windows11 build22000。两包使用同一中文x64启动入口；ARM包不是原生ARM应用。源码不提交运行环境二进制或私人默认配置。
