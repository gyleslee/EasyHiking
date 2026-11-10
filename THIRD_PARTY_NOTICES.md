# 第三方组件与数据说明

项目自身代码：Copyright (c) 2026 Gyles Lee，GPL-3.0-only。GPL 全文见 LICENSE。下列第三方内容保留原许可证，不因纳入仓库而改为项目的版权。

| 组件 | 用途与许可说明 |
|---|---|
| BRouter 1.7.10 | 本地路由及 rd5 解码，MIT；全文随引擎保留在 BRouterRuntime/BRouter-LICENSE.txt。上游：https://github.com/abrensch/brouter |
| MapLibre GL JS 6.11.1 | 地图渲染，BSD-3-Clause；全文见 Map/Web/vendor/MAPLIBRE-LICENSE.txt。本地虚线图集修补说明见同目录 LOCAL-PATCHES.md。上游：https://github.com/maplibre/maplibre-gl-js |
| coordtransform | 坐标转换公式，MIT；全文见 ThirdPartyNotices/coordtransform-LICENSE.txt。 |
| ScottPlot / ScottPlot.WPF | 高程图表，MIT；精确版本以对应 csproj / NuGet 元数据为准。 |
| NetTopologySuite | 几何运算，BSD-3-Clause；保留 NuGet 包许可。 |
| Serilog / Serilog.Sinks.File | 日志，Apache-2.0；保留 NuGet 包许可。 |
| Microsoft.Data.Sqlite / SQLitePCLRaw / SQLite | 收藏点等持久化；分别适用其 NuGet 包及 SQLite 上游许可；V1.9.5 固定 SQLitePCLRaw 原生库 3.53.3；安全修复和旧版风险见 SECURITY.md。 |
| MaxRev.Gdal / GDAL 及原生依赖 | DEM 读取，封装与原生库许可分别适用；MaxRev 封装为 MIT，GDAL 含多项第三方依赖，分发须保留包内许可。 |
| Microsoft WebView2 | SDK 与运行时适用微软随组件提供的许可；不包含在项目 GPL 授权范围。 |
| .NET | .NET 项目及桌面运行库适用其官方许可；安装包环境组件单独分发，不作为项目自有代码。 |
| Java 运行环境 | 源码仓库不包含 JRE；原安装包含适用的 OpenJDK 运行环境及许可。需按相应发行商许可保留法律声明与对应源码获取途径。 |

NuGet 依赖和传递依赖以各分支项目引用及还原得到的元数据为准。上表是导航，不替代完整许可证。分发修改版时应重新检查依赖变化及原生组件许可。

OSM 道路默认样式由维护者提供的 Locus-V5-outdoor 主题道路规则转换；本仓库不含整套主题压缩包或主题图标，只含应用所用的道路规则数据及匹配逻辑。Locus、Mapsforge 与地图服务商的名称仅用于说明兼容性和来源，不表示官方合作或背书；本项目不能代替原素材权利人授予第三方素材的权利。

## 地图和高程数据

源码保留内置公开 OSM 示例，不提交维护者个人图源列表、Headers 或 Key。地图数据、服务器使用、浏览缓存、区域离线下载与离线包再分发是不同事项，分别遵守服务方条件；设置署名不等同于获得离线下载许可。

- OpenStreetMap 署名与数据许可：https://www.openstreetmap.org/copyright
- 官方瓦片使用政策：https://operations.osmfoundation.org/policies/tiles/
- 德国风格瓦片说明：https://openstreetmap.de/germanstyle/
- OSM France 服务说明：https://www.openstreetmap.fr/fonds-de-carte/

DEM、BRouter rd5 和 PMTiles 文件不随源码提供。操作说明中的地图截图用于展示操作，请保留截图中的来源署名；不授予提取地图内容再分发的许可。
