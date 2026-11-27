# 中文地图标签绘制

地图地名统一使用像素单位绘制。城市默认 em 为 14px，国家和海域按缩放级别使用 14、15、16px；输出仍为 256×256 透明 PNG。运行时本地标签服务和安装器缓存重绘编译、调用同一份 `src/shared/MapLabelRenderer.cs`。

## 绘制规则

- 使用 `GraphicsUnit.Pixel`，按 `GraphicsPath.AddString` 的实际字形路径计算范围，避免用 Point 字体测量后再按像素路径绘制。14px 是逻辑 em，并不表示每个汉字的可见高度恰好为 14 个像素。
- 城市使用微软雅黑常规字重，国家和海域保持粗体；正文为不透明白色，深色圆角描边宽 2px。路径抗锯齿使用 `SmoothingMode.AntiAlias`。
- 居中、边距与避碰均包含描边的实际范围。国家和海域优先，同类别保持 WWP 输入顺序；高缩放级别的离线缓存不再漏掉国家标签，与运行时规则一致。
- 过长名称只在轮廓超出可用瓦片时等比缩小，保留完整文字；描边仍为 2px。边缘标签向瓦片内部夹取，源 WWP 名称、坐标和 flags 不变。
- 空瓦片直接返回透明图片，不创建字体、画笔或字形资源。非空调用的绘制对象由本次调用独占并及时释放。

本次不改变地图瓦片传输线程、地形底图或最终 GPU 纹理尺寸，不包含双倍离屏采样。密集地区仍会跳过碰撞标签，超长名称的实际字号可能低于 14px，因此不能保证所有地名同时可见。

## 省州名、内置地名与正式瓦片共存

R88 词典的 `places` 提供省州名、标签锚点和起始缩放。安装器和本地标签服务器使用同一份数据。打包瓦片先保留原有中文和半透明笔画，再在空余位置绘制该缩放可见的省州名。若正式标签已经在另一张瓦片上标注同一行政区，按词典里的名称、经纬度和缩放识别这一项。相邻国家同名行政区分别核对。

布局先保留旧版 WWP 与上游 R88 原先实际可见的内置地名，以及 R88 已经显示的行政区。按原可见身份逐项核对，已由正式标签表示的名称由对应瓦片显示。最新正式地图的缓存和在线请求共用此规则，涵盖 DAT 收录的瓦片及其范围外的原有缓存。

先在原锚点绘字。遇到遮挡时，用 12 像素字形排版，较长的行政名称可在完整行政后缀前换行，并在同一张瓦片内寻找最近的空位。同距时优先保持原东西坐标。原先可见的标注若仍无空位，最后使用 11 像素字形和更窄的中文分行。离开锚点的标签用细引线指回地理位置，原图已有笔画和新增文字的描边在引线上方。安装器预填、缓存刷新和本地标签服务器采用相同排布。

`dict/placenames/packed_region_coverage.jsonl` 逐行记录 R88 身份、实际绘制该名称的区域和标签 ID、原文、正式译名和输出瓦片。地图资料或译名变化时，核对变动的行政区及其跨片位置，更新这些记录，然后使用对应正式构建报告生成索引。报告支持 JSON 或 gzip 压缩的 JSON。

```powershell
python scripts/verify_packed_regions.py --write --report build/map_tile_build_report.json
python scripts/verify_packed_regions.py
```

生成器校验报告的地图包指纹、实际绘制范围、审定译文、R88 词典中的完整身份及其可见缩放。运行时仅在 DAT 内容指纹相符时启用该索引。更换或移除 DAT 时，词典自己的显示范围仍然生效。发版门禁 `verify_all.py` 核对生成的 C# 索引与当前 DAT 和审定记录。

内置地名的逐缩放对照源在 `dict/placenames/packed_wwp_coverage.jsonl`。使用同一份正式报告运行 `python scripts/verify_packed_wwp.py --write --report build/map_tile_build_report.json` 可重建 WWP 索引。

## 验证

- 核心几何回归 22 项：实际中文字形大小、像素居中、四角描边边界、真实最长名、极端长名、碰撞、稳定顺序、缩放选择、透明空瓦片及运行时/共享 PNG 一致性。
- 缓存专项 55 项、真实 CLI 缓存集成 4 项：包含城市、国家、空瓦片及真实最长名的离线/共享 PNG 对照，保留已有的失败保存计数、完整备份发布和安全重试保护。
- 同一宿主环境可逐字节比较 PNG；跨进程 DPI 上下文可能改变 PNG 的 pHYs 元数据，判断绘制是否一致应比较解码后的 RGBA 像素。

复跑时先按既有 `scripts/build.sh` 编译引擎，再执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_map_label_renderer.ps1 -Engine build/CMOLocalizer.dll
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_refresh_placename_tiles.ps1
python -B scripts/test_placename_cache.py -v
python -B scripts/test_placenames.py -v
python -B scripts/test_verify_placenames.py -v
```

缓存专项与 CLI 集成应串行运行，避免临时构建的新 DLL 差集探测互相干扰。发布、回归及缓存刷新编译入口均包含新的共享源文件。

## 成本与应用

此前同机固定样本的历史离线测量中，渲染加 PNG 编码的 CPU 批均值中位数由 1.953 增至 2.372ms/张，约增加 21.4%。每组 11 批、每批 224 次；旧基准是重编的旧绘字逻辑，不是实际 E099 旧引擎的性能对照，也不是本次 PR 分支的新计时或游戏 FPS 测量。较大文字可能使密集视角显示更少地名，PNG 存储和生成 CPU 成本需一并考虑。

已有 14px 直接绘制版本曾从 Steam 启动验证；本 PR 的共享绘制实现与该版本相同。历史截图和性能数据只支持对应固定视角、机器和输入，不保证其他显示器、硬件或全部场景的效果。

应用字体修改需部署新版引擎并重绘 `WW/Cache/Stamen Terrain - Labels`，退出后重启游戏加载。安装器和地名刷新脚本使用相同的缓存绘制逻辑；只更新 WWP 或只替换引擎都可能留下旧缓存。修改前应保留当前引擎、配置和标签缓存备份，原英文 `.orig` 备份继续保留。仅运行 `apply_placenames.py` 不会替换游戏引擎。

## 已核验区域与跨区实例

有限区域包 `ChineseMapTiles.dat` 直接提供已逐项核验的中文PNG。原图、译名、符号、布局、字体和实际RGBA审批记录在 `dict/placenames/tile_regions.json`；任何变化须重新核验，不能用非空瓦片代替完整性审核。`scope.global_complete`保持false，包外仍有旧渲染器缺失。

CMOTIL01保持原有PNG格式；CMOTIL02追加经审核的旧WWP精确排除规则。只有同zoom、中文名、float32经纬度逐位及level全部匹配的legacy条目被过滤，同名异地与完整目录条目保持。原WWP记录不修改。运行时先加载WWP再刷新已有包外缓存；边界点按旧渲染器的双边包含规则处理，最多四格，不能仅刷新floor投影所在一格。只刷新已经存在的包外路径。

安装器先备份并准备包内PNG，再完成全部既有缓存重绘，最后发布完成指纹；任一重绘失败均不能发布新的完成标记。每文件保持原子替换，完整原 `.orig`不覆盖，重试按实际字节修复。安装器包外刷新仍保留一次写入、一次计数及显式更新时间戳的既有契约。

相邻区域可用 `shared_sources`引用同一原文字实例的唯一输出owner，避免同一标签在两区重复绘制。完整世界像素原框、原文、类别、资料URL和zoom须精确一致，owner必须通过审批且确有中文输出；引用只记本区原像素，不增加文字或输出计数。失效引用阻断整个构建。此机制不能豁免未知文字或替代人工逐项核对。

原版未命名的山峰只有高度和三角点时，peak feature显式标 `elevation_only:true`，original仅含原数字与m，译文必须是同数字加“米”且单行输出；不编造峰名、不重复添加高度、不用当前数据库改原图数字，也不能据此排除具名WWP。原图只有几何点且没有任何文字时，symbol可显式标 `unlabeled:true`，省略feature_id，并提供自身source_url与review_notes。此项只恢复原符号，不创建虚构原文或译文，不计标签数；仍须逐项源图与实际像素审核。两类标记都纳入区域指纹，旧审批不能复用。

部分官方冻结原图含有精确RGBA(1,1,1,1)的平坦背景。`source_backgrounds`只允许记录本区core瓦片的精确原图SHA、URL、该固定颜色及独立复核说明，且此颜色须占至少半张瓦片。构建时只在原像素归账中扣除已确认背景，不修改原图或中文输出，也不忽略其他低alpha像素。文字、符号和共享实例的原框须在扣除背景后仍包含真实墨迹；context瓦片、错SHA、其他颜色或稀疏像素不能获得此例外。背景证据属于区域指纹，变化后必须重新审批。
