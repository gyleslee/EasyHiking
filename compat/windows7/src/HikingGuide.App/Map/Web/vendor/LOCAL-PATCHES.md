# MapLibre 6.11.1 本地修补：虚线图集按需扩容

上游文件src/render/line_atlas.ts及src/data/program_configuration.ts：图集固定512行，满时addDash返回null，setConstantDashPositions未判断空值。完整道路主题随缩放累计所需行数超过512，导致黑色虚线缺失及3D帧绘制中断。

保留原6.11.1库及MIT许可证，只改构建文件maplibre-gl.mjs中的LineAtlas：溢出前扩展为2的幂，复制旧像素，保持旧纹理行索引，下一次bind删除旧GPU纹理并重新上传；增长上限取实际WebGL最大纹理尺寸，首次bind前采用保守2048。原始256像素宽度、SDF绘制、主题样式、虚线规则均保持。不能只调大height而不重新分配data或GPU纹理。

tests/web/dash-atlas.test.mjs提取实际发布类，遍历完整主题及缩放表达式，验证扩容、旧行及GPU纹理重建。升级SDK需重新核对源码和测试，不盲目套用压缩文件匹配。