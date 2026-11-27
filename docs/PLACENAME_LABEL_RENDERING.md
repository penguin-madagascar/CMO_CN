# 中文地图标签绘制

原游戏的 `Cities.wwp` 提供城市，`Synoptic.wwp` 提供国家、海域等宏观地名。作者 R88 词典顶层的 `places` 另提供省州中文、锚点与起始缩放。本次审核增补的地名、缩放排版和符号置于同一份 `CMOLocalizer.json` 的 `reviewed_map`。

## 一张瓦片如何生成

本地地图标签服务按请求的 `z/x/y` 生成 256×256 的透明 PNG。引擎与安装器的缓存重绘共用 `MapLabelIndex.Load(gameDirectory, dictionaryPath)` 和 `MapLabelRenderer.RenderTileBytes`。同一级地名有独立经纬度与排版偏移。中文、描边或地物符号越过瓦片边界时，相邻片的对应位置也须绘出。

原城市、宏观地名与省州成员均有原来源身份。增补数据按缩放标明哪些标签代表哪个原成员。同一实体的原名、译名和位置均需有依据，单凭名称相同不会建立关联。不同地点同名、同一地点在不同缩放的重复印次、文字与地图符号各有独立记录。

已有游戏缓存由安装器的刷新路径重绘。运行时首次浏览某 XYZ 时由本地标签服务响应。维护者可用原版服务器的同坐标标签图核对字形与地理位置。用户使用的目录、已登记旧资产的迁移与卸载由安装流程管理。原版 `.orig` 是恢复原缓存的依据。

## 核验显示

读入 JSON、启动端口或得到非透明 PNG 只能分别证明对应环节。核对某个地名时要比较核准中文、缩放、实际出现位置、相邻片接缝及相伴符号。核对旧数据时检查原本可见的 WWP/省州成员、同缩放的去重以及同名异地。HTTP 返回的图像与隔离缓存应对同一 XYZ 的同一渲染版本作比较。

在 Windows 上可用 .NET Framework 4.x 复跑实际 GDI+ 渲染。第一条快速检查代表地点，第二条另外从 Git 读取作者版本，重放同缩放已显示的 WWP 与 R88 地名，并与仓库内冻结的承接记录相核对。

```powershell
python -B scripts/test_map_label_history.py
python -B scripts/test_map_label_history.py --audit
```

完整审计需要包含作者上游提交的 Git 历史及 Windows 微软雅黑字体。对照输入在 `src/tests/fixtures/map_label_history.tsv.gz`，脚本开头列有测量所用常规和粗体字库的 SHA-256。运行在临时目录进行，可从公开仓库复跑。

维护源和审核 ID 的回查见[地图数据维护](MAP_TILE_SOURCES.md)。已核原图范围和待核记录见[地图地名审核范围](GLOBAL_MAP_LABELS.md)。
