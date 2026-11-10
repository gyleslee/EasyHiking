# 安全说明

请不要在公开 Issue 中提交密码、Token、私有图源请求头、完整个人配置、收藏数据库或未脱敏的项目。崩溃转储也可能含内存中的个人信息。若没有私密报告渠道，请先通过 Issue 请求私密联系方法，不公开个人信息或利用细节。

## V1.9.5：CVE-2025-6965 修复

SQLite 官方将该问题的修复界限定为 3.50.2。V1.9.5 的现代版和兼容版均直接固定 `SQLitePCLRaw.lib.e_sqlite3 3.53.3`，实际查询加载引擎为 SQLite 3.53.3；GDAL 的独立 `sqlite3.dll` 为 3.53.4。没有仅靠关闭审计或隐藏 NU1903 来处理警告。

保留 Microsoft.Data.Sqlite 托管接口及数据库结构，补充实际原生引擎版本、中文 FTS5、事务回滚与完整性测试。现有收藏库的隔离副本检查通过；原库及图片不迁移、不清理。Win7 分支的新 DLL API 门槛已检查，但没有新的 Win7 实机验收。

| 发布版本 | 本项风险状态 |
|---|---|
| V1.9.5 | 已替换受影响原生引擎，并在两套桌面构建实际查询版本验证。 |
| GitHub T1.9.2 及旧安装包 | 保留原有受影响依赖，未追溯替换；建议升级。 |

来源：[GitHub Advisory GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)、[SQLite 官方漏洞记录](https://sqlite.org/cves.html)、[固定的原生包](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/3.53.3)。

这项修复不等于对软件及所有第三方组件作出无漏洞保证。NuGet 审计保持开启；将来新增的依赖警告应继续评估。Windows 7 所需 .NET 6 和私有 WebView2 109 已结束厂商支持，兼容包仍有相应维护限制。
