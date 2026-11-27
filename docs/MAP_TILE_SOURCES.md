# 地图标签数据维护

地图源采用 `split-map-regions-v1`。`dict/placenames/tile_regions.json` 只保存公共设置和有序文件索引，不再内嵌全部区域。

| 路径 | 内容 |
|---|---|
| `dict/placenames/map_tiles/regions/<区域ID>.json` | 原名、中文、排版、符号、跨区关系和原图引用；每个数组元素一行，坐标保持行内 |
| `dict/placenames/map_tiles/tiles.jsonl` | 每个原图路径一条：坐标、SHA256、来源；区域只引用路径，特殊上下文元数据留在引用上 |
| `dict/placenames/map_tiles/sources.jsonl` | 每个来源 URL 一条，ID 为 URL 的 SHA256 前16位；记录中的 `@source/<ID>` 引用此表 |
| `dict/placenames/map_tiles/reviews.jsonl` | 每区一条既有审核结果，绑定完整展开输入指纹和实际 RGBA 指纹 |

译名仍含来源及对应原文记录的指纹。同名异地、同名不同缩放、不同原图印次不能按名称合并。公共原图字段发生冲突必须查清来源，不能以最后写入覆盖。

`@note/<类别>` 引用索引中的公共短说明。记录里的实体消歧说明、来源 URL、原图 SHA、几何数据及关联标识共同用于复核译名。

以 `evidence/parallel_map/` 和 `evidence/tile_recovery/` 开头的路径分别指向维护者证据库中的地图审核记录和瓦片恢复记录。公开清单保留证据文件名及已有的 SHA256，便于持有对应文件的复核者校验内容。公开来源 URL 可在 `sources.jsonl` 中查询。

## 编辑与构建

1. 修改对应区域文件；新增来源和原图时分别补公共表，复用已有条目。
2. 新区域追加到索引的 `region_files`，保留既有区域顺序。
3. 通过正常来源、译名和实际图像核验后更新审核表。拆分工具不产生审批，不会自动刷新指纹；数据修改后沿用旧指纹仍会失败。
4. 原构建命令的 `--manifest` 继续指向 `dict/placenames/tile_regions.json`。构建器自动展开引用，并在报告中列出全部 `manifest_inputs`。

Python 工具应使用 `map_tile_manifest.load_manifest(path)` 读取逻辑清单，使用 `manifest_files(path)` 取得完整输入闭包。不能只给索引文件做哈希，也不能直接用 `json.loads(index)['regions']` 读取全部区域。

旧分析工具暂需单文件时，生成临时兼容输入：

```sh
python -B scripts/map_tile_manifest.py expand \
  --manifest dict/placenames/tile_regions.json \
  --output build/map/tile_regions.expanded.json
```

输出必须是未存在的文件。展开结果是构建或分析产物，不得作为人工维护源重新提交。`split` 子命令可把已有完整逻辑清单拆到一个新位置，用于迁移或私有候选；它逐项保留原值，不会精简说明或替调用者审批。

## 重建与审核边界

源码重建包包括索引、所有区域文件、三张公共表、读取器、原图、字体和原有外部批准文件；可以搬迁后独立重建。既有单文件历史清单仍可读取，旧冻结包无需改写。

本次迁移只精简说明、共享重复数据和转换存储格式。名称、位置、字号、符号、原图、原顺序及既有 RGBA 指纹保留。说明参与过旧 whole 指纹，因此新的输入指纹与旧值不同；不能声称新清单与旧清单整对象相等。迁移前后绑定及重建证明保存在工作证据中。

引用 whole 或 symbol 指纹的外部批准必须显式做仅元数据的重绑定，不能假装旧 SHA 批准了新输入。重绑定不授予新增符号、改动几何或新增绘制的权限，原批准文件和历史成果包保持原样。

验证入口：`scripts/test_map_tile_manifest.py`、`scripts/test_build_map_tile_pack.py`、`scripts/verify_routing.py`。数据迁移还须完整重建并与既有 `.dat` 逐字节比较；不能以计数或非空图片替代。

## 尚未识别的原图像素

先补回已确认地名时，区域可在审核正文中填写 `source_unclassified`。其中 `pixels` 为未识别的正 alpha 像素数，`bbox` 为区域局部的半开边界，`mask_sha256` 为完整区域掩膜的 SHA-256。掩膜按行保存单字节值，剩余原图像素为 255，其余为 0。

构建器先核验原图文件哈希，并归账已确认的文字、共享来源、符号和背景。剩余掩膜须与审核声明完全一致。报告在 `source_alpha.unclassified` 单列这部分记录，已归账计数排除这些像素。瓦片仅绘制已批准的内容，待识别像素仍随原图保存。

经原图及同供应商完整标签确认为独立斜杠的标点，可以使用 `source_punctuation` 在原位置保留。区域清单记录原图边界与原始 RGBA 的 SHA-256。构建器核对相邻像素、连通性和目标占用，仅复制有墨迹的像素。标点有独立计数，地名及地物符号计数保持原值。

## 原有 WWP 地名补绘

安装器和运行时按原样读取 `Cities.wwp`。以下四条旧记录含拉丁字母或假名。两个入口使用同一份中文显示名，匹配条件为城市层、从零开始的序号、原名、两项 float32 坐标和 flags。用于地图包去重的键仍取原记录。

| Cities 序号 | 原文 | 中文显示 | 地点与依据 |
| --- | --- | --- | --- |
| 1203 | Tutamanda旅馆 | 途塔曼达旅馆 | 基多 Cristóbal Colón 与 Versalles 路口的旅馆。西语名见当地旅游业名录，项目参照同品牌另一家旅馆的公开中文用例音译。[基多旅游名录，第 139 页](https://www.dspace.uce.edu.ec/server/api/core/bitstreams/9a5ce140-669e-4ed5-94b6-999897f736ab/content#page=139) · [同品牌中文用例](https://www.a-hotel.com/zh/e-gua-duo-er/100164-ji-duo/359724-1-Tu-Ta-Man-Da-2Lu-Guan/) |
| 2419 | 调布ケ丘 | 调布丘 | 东京都调布市街区。当地电气通信大学产学官联合中心内的调布办公室在中文地址中使用“调布丘”。[调布办公室中文地址](https://www.campuscreate.com/wp-content/uploads/2024/12/2023_CC_globalA3_c1221.pdf) |
| 2679 | 古斯塔沃·A.马德罗 | 古斯塔沃·阿道夫·马德罗 | 塔毛利帕斯州冈萨雷斯市聚落，INEGI 编号 280120431。其 7 级瓦片已使用相同译名。地名中的 A. 展开为人名 Adolfo。[州政府聚落名册](https://tamaulipas.gob.mx/ceat/wp-content/uploads/sites/24/2019/09/80-aal-2018-tcnico-cuarto-modificatorio.pdf) · [墨西哥国家人类学与历史研究所](https://revistas.inah.gob.mx/index.php/antropologia/article/view/3033/0) |
| 2684 | 古斯塔沃·A·马德罗 | 古斯塔沃·阿道夫·马德罗 | 墨西哥城辖区，采用中国外交部使用的地名译写。[外交部领事司及驻墨使馆的中文用名](https://www.mfa.gov.cn/wjbzwfwpt/kzx/aqtx/zwqw/201912/t20191230_10404787.html) |

通过 `scripts/build.sh` 重建含内置地名补绘的地图时，设置 `CMO_MAP_CANONICAL_CAPTION_APPROVALS` 为本次审核的选择文件路径，`CMO_MAP_CANONICAL_CAPTION_APPROVALS_SHA256` 为该文件的 SHA-256。源码包校验使用同一对参数逐项核对区域、缩放级、条目身份与正文指纹。

私有源码重建包用于核验原图、字体和审批文件。正式绿化包只分发运行所需的地图文件，公开仓库提供拆分清单及构建脚本。

原图未印出的游戏原有地名可单独记录在区域的 `canonical_captions` 中。记录保留原始 WWP 文件 SHA、记录序号、中文、float32 坐标位、flags 和 level；`source_bbox`、`source_png`、`source_native` 均为 null。这类记录只补绘原有中文文字，不作为原图墨迹、海拔或原生符号入账。

原有 WWP 名称在两段中文之间含一个旧拉丁大写缩写（如 `·A.` 或 `·A·`）时，可在 caption 上单独填写 `display_translation`。其 `zh` 为完整的中文显示名，`source` 链接译名依据，`review_notes` 记录核对说明。`canonical_record.original_Chinese_name` 继续逐字匹配原 WWP，`layout.lines` 拼接后必须与 `display_translation.zh` 完全相同。外部选择文件的完整正文指纹同时绑定原记录和显示译文，原 WWP 和旧回退规则的身份字段保持原样。

构建时通过 `--canonical-caption-approvals` 和 `--canonical-caption-approvals-sha256` 显式传入选择文件。其区域、缩放、caption ID 和完整正文指纹必须精确覆盖本次输入。正式整图选择使用 `root-approved-canonical-WWP-caption-v1` 的 `full_current` 角色，并分别绑定模型、实际审核清单、审核结果及独立目标渲染签证。构建器检查引用形状与原始 WWP 成员，调用方须先核验这四项证据的实际内容；单独提供引用不能代替来源与图像审核。

完整原字、越界、遮挡、接缝、旧区域和原符号保留仍需实际核验。正文或布局变化会使旧选择失效，须重新选择与审核。源码重建包保存选择文件、原 WWP 和完整拆分清单，使补绘结果能够独立重建。测试用的 `TEST_ONLY` 及私有有限预览的 `named_private` 角色不能代替正式整图审批。

## 跨区圆点的原图归账

相邻区域已经完整绘制一个矢量圆点时，本区可在 `shared_sources` 仅归账原图中跨过边界的圆点像素。此变体填写 `id`、`owner_region`、`owner_symbol_id`、`original_bbox`、`owner_symbol_record_sha256`、`source_rgba_sha256` 和 `review_notes`。`original_bbox` 使用本区局部坐标，覆盖圆点的完整原框，允许边界框跨瓦片。逐字节核对双方原图裁片后，填入完整 RGBA 裁片的 SHA-256。`owner_symbol_record_sha256` 绑定圆点的完整源记录。

归属区域必须在同一缩放级别通过来源及渲染审核，并实际完整绘制该圆点。可引用直接关联城市的圆点，也可引用标为 `unlabeled: true` 的独立圆点。记录只填写上述字段，本区通过这条记录取得原图覆盖，标签和符号计数均由实际绘制的内容决定。

## 原生三角符号像素保留

符号默认沿用现有矢量绘制；显式 `render_mode: "vector"` 与默认行为相同。需要逐字节保留实见山峰三角形时，可单独使用 `render_mode: "source_rgba"`，并给出整个原框 RGBA 字节的 `source_rgba_sha256`、严格布尔值 `source_symbol_only: true` 和非空 `review_notes`。只有直接通过 `feature_id` 关联本区 `peak` 的 `triangle` 可使用此模式；城市、圆点、`shared_source_id` 或 `unlabeled` 均不接受。未知绘制模式会阻断输出。

`original_bbox` 必须是完整落在输出区域内的四个整数，宽高各不超过 32 像素。`x/y` 必须精确等于原框中心，不接受 `size`、`outline_width`、缩放或偏移。构建器通过 `native_source_frame` 读取已声明的完整原框：原图路径必须是 `z/x/y.png`，来源 URL 必须是相同 XYZ 的官方 Labels 地址，PNG 与来源 SHA 必须实际通过；跨瓦片原框的每一张原图都必须提供。构建入口使用实际含这些 canonical 原图路径的 `original_root`。

普通文字、补绘文字和矢量符号先完成，原生裁片最后以整数原位置、不带 mask 的 RGBA paste 写入。整个目标原框内只要已有任意正 alpha（包括 1），就拒绝覆盖；裁片在全部绘制结束后再次核对完整 RGBA SHA。因此 alpha 为零但 RGB 非零的像素也必须保留。报告中的 `native_mode` 和 `source_rgba_sha256` 记录该模式和原框指纹；未准备或校验失败的裁片不会退回矢量绘制。

`source_symbol_only` 是需要独立原图观察支持的资格声明，不是机器识别结果。代码验证来源、几何、像素指纹和不覆盖已有像素，不能自动判定裁片是否含拉丁字或混合文字。独立审核须实际查看紧文字支持与完整裁片，确认纯三角形并保留小图证；文字框包含符号的复合框不能仅因矩形相交就判定污染。声明、原框和所有像素资格随现有区域输入指纹及 Source／render 审核绑定；原图归账用的 `owner_native_source_refs` 不授予这项绘制资格。
