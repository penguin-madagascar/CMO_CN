# 路由表（Routing Table）

> **这份表是给 agent 和人共用的地图。** 目的是让任何一次改动都能在 30 秒内找到
> "该改哪个文件"，以及"改完会影响谁"。
>
> **铁律：新增任何文件，必须在本文档登记它的职责，否则门禁 `verify_routing` 会直接红。**
> 这不是形式主义——没有登记的脚本，半年后没人知道它还有没有用、能不能删。

验证命令：`python scripts/verify_routing.py`

---

## 0. 先看这里：三条最容易踩的坑

1. **只改 `dict/src/*.json`，永远不要直接改 `dict/CMOLocalizer.json`。**
   后者是 `build_dict.py` 的产物，手改会在下次构建时被覆盖（单一事实源铁律）。
2. **`--static --installer` 与 `--live` 必须分两次跑**，installer 用例会 `kill` 游戏进程。
3. **发版动作（`package.sh` / `publish_release.py` / 打 tag）必须获得用户明确授权。**
   已发布资产只增不改。

---

## 1. 系统总览：数据是怎么流动的

```
            dict/src/*.json  (人工维护的翻译源，唯一事实源)
                    │
                    ▼  build_dict.py  (确定性构建，字节可复现)
            dict/CMOLocalizer.json  (产物：exact + words + offsets + 注入池)
                    │
                    ▼  build.sh  (csc 4.8 编译，C# 5)
        build/CMOLocalizer.dll   ←─ src/engine/CMOLocalizer.cs + Reflect.cs
                    │
                    ├── deploy.sh ──→ 游戏目录（含 Command.exe.config 注入 appDomainManager）
                    │
                    └── package.sh ──→ CMO_Chinese_Pack_<ver>.zip
                                       CMO_Chinese_Setup_v<ver>.exe  ←─ src/installer/
```

**运行时**：`LocalizerDomainManager` 由 CLR 加载 → 挂 WPF 钩子 + 翻译回调 +
池注入 → 读 `dict/CMOLocalizer.json` → 改动界面文本。

---

## 2. 顶层锚点文件

| 路径 | 职责 | 谁会读它 |
|---|---|---|
| `VERSION` | 唯一版本号（如 `4.2.2`）。**发版只改这里** | `build.sh`/`package.sh`/`verify_all.gate_version`/`publish_release.py` |
| `GAME_BUILD` | 适配的游戏内部版本（`1900.23`）。游戏更新时才改 | `verify_all.gate_version`、`L-build-adaptation` |
| `STABLE_VERSION` | 稳定版版本号（README 稳定版通道的事实源） | `sync_readme_downloads.py` |
| `README.md` | 用户主文档 + 下载区（下载区由脚本生成，勿手改） | `package.sh`（改名成 `汉化说明.md` 入包）、`sync_readme_downloads.py` |
| `AGENTS.md` | **agent 强制约定**：先读路由表、新文件必须登记、数据源铁律、编码约定、发版需授权 | agent 进入仓库时读；与 `docs/ROUTING.md` 互为入口 |

---

## 3. 构建 / 部署 / 发布

| 路径 | 职责 | 何时改它 |
|---|---|---|
| `scripts/build.sh` | 编译引擎 DLL、更新器、共享测试、安装器；顺带跑 `build_dict.py` | 增删编译目标、改编译参数 |
| `scripts/build.ps1` | **仅引擎构建**（不碰游戏、不写安装包）：自动收集 `src/engine/*.cs` + `src/shared/MapLabelRenderer.cs`，并把 `WebViewTranslation.js` 作为嵌入资源编进 DLL。新增引擎源文件无需改脚本 |
| `scripts/deploy.sh` | 把 `build/` 产物装进游戏目录 + 写 `Command.exe.config` | 部署清单变了 |
| `scripts/package.sh` | 打绿色包 zip（含 `--no-report` 防覆盖 live 报告） | 包内文件清单变了 |
| `scripts/publish_release.py` | 建 GitHub Release + 上传 4 个资产。**`--verify-only` 是纯只读校验** | 发布流程变了 |
| `scripts/release_check.py` | 发版统一入库检查（18 项；`payload-binding` 核验 exe 内嵌 dll/json/地名集与绿色包内容哈希）。未发布前最后一道闸 | 新增发版前置条件 |
| `scripts/sync_readme_downloads.py` | 生成 README 下载区；`--check` 模式供门禁 | 下载表格式变了 |
| `scripts/verify_release.py` | 词典产物级发布门禁（条目数/结构） | 词典契约变了 |
| `scripts/verify_no_secrets.py` | 密钥门禁：禁止明文凭据入库 | 凭据规则变了 |
| `scripts/hooks/pre-commit` | git 钩子，调用上面的密钥门禁 | 很少改 |

---

## 4. 引擎（运行时汉化核心）

| 路径 | 职责 |
|---|---|
| `src/engine/CMOLocalizer.cs` | **整个汉化引擎**（约 7600 行，C# 5）。一轮改动的主战场，见下方内部地图 |
| `src/engine/Reflect.cs` | 反射辅助（私有字段/方法访问），引擎的地基 |
| `src/engine/WeaponsTextTranslation.cs` | 武器行绑定投影用的 `IMultiValueConverter`/`IValueConverter`：在绑定求值链上产出中文，`ConvertBack` 明确拒绝（只做显示投影） |
| `src/engine/ComboBoxTranslation.cs` | 下拉项增量翻译：动态回填排队，保留业务键/选中值/原生类型 token |
| `src/engine/DarkTreeTranslation.cs` | 深色树分片遍历：核对实时集合归属，分片避免长阻塞 |
| `src/engine/UiLifecycle.cs` | UI 生命周期挂钩（控件创建/重挂载），替代纯周期扫描来发现新控件 |
| `src/engine/UiIdleContinuation.cs` | 空闲续做：`Application.Idle` + 1ms 兜底 Timer 的单次续接器（只在 UI 线程创建/取消） |
| `src/engine/UiTranslationQueue.cs` | 有界增量翻译队列：按 UI 线程派发，执行前复核控件归属与输入保护 |
| `src/engine/UiPerfMetrics.cs` | 实际 UI 回调耗时与待执行计量；投递、执行、完成分别计数，供性能探针识别排队与超帧 |
| `src/engine/WpfTranslationQueue.cs` | 每 Dispatcher 一份有界合并队列，以节点/Inline 游标分片；换根或卸载取消旧工作，避免全树异线程积压 |
| `src/engine/WebViewTranslation.cs` | WebView2 译文注入宿主：注册文档脚本、限制来源/nonce/容量、拒绝旧回包与编辑区写入；含页→宿主通道的**按文档**探帧看门狗（1.6s 无回执就改宿主轮询 `take()`，R84/R85）与只读探针 `WEBVIEW_DIAG` |
| `src/engine/WebViewTranslation.js` | 上面宿主注入页面的脚本。**以嵌入资源 `CMOLocalizer.WebViewTranslation.js` 打进 DLL**（`scripts/build.ps1` 负责 `-resource:`），改名或移动会运行期抛 `Missing WebViewTranslation.js embedded resource` |
| `src/engine/WpfValueTranslation.cs` | WPF 值翻译（`Content`/`Text`/绑定安全写回）、武器子树的只读绑定/Inline/时序诊断，以及**挂在稳定 ItemsControl 容器上的武器行绑定投影**（R68）；从 `CMOLocalizer.cs` 抽出的 partial 分片 |
### `CMOLocalizer.cs` 内部地图（按子系统找，不要按行号找）

| 子系统 | 关键字（grep 这些） | 干什么 |
|---|---|---|
| 域管理器入口 | `LocalizerDomainManager`、`CustomAppDomainManager` | CLR 加载入口，挂载一切 |
| 翻译主流程 | `DoTranslate`、`TranslateString`、`TranslateBracketSegments` | 文本 → 译文的核心管道 |
| 中英混排修复 | `TryFixMixedBeforeChineseGuard`、`FixMixedEnglishPrefix`、`TranslateMixedValueTail` | R42/R45：`Inherited, 中文`、`状态: Unassigned` 这类 |
| 组合翻译 | `TryComposeTranslate`、`TranslateComposedName`、`TranslateNumberWithUnit` | R44：结构交给代码、词汇交给 `dict/src/terms.json` |
| WPF 钩子 | `HookWpf*`、`SetTextKeepBinding`、`SetContentKeepBinding` | 挂钩控件属性，改文本但保留绑定 |
| 重绘节拍 | `StartWpfRepaintTick`、`_repaintPeriod`、`_idleTicks`、`TranslateWpfElementHostsNow` | R46/R47：Timer 驱动的补扫（**不是** `CompositionTarget.Rendering`，那个在静止面板永不触发） |
| 写入门禁 | `AllowWrite`、`WRITE_BUCKET_*`、`hookedWpfElements` | R43：令牌桶熔断，防中英拉锯 |
| 池注入 | `POOL_DUMP`、`poolDisplay`、`inject` | 把中文直接写进游戏的字符串池 |
| 地图标签 | `TileClient`、`MapLabel` | 地图瓦片/标签服务（与 `src/shared/MapLabelRenderer.cs` 配合） |
| 诊断探针 | `XLATE`、`XLATE_BATCH`、`WPF_DUMP`、`WPF_TEXTLOG`、`CTRL_TREE`、`TRACE_*`、`FLICKER_*` | 实机验证用命令通道（见 §9） |

---

## 5. 共享层

| 路径 | 职责 |
|---|---|
| `src/shared/CMOShared.cs` | 引擎/安装器/更新器共用的工具函数（版本比较、路径、JSON 等） |
| `src/shared/CMOSharedTest.cs` | 上面的自测入口 |
| `src/shared/MapLabelRenderer.cs` | 地图中文标签渲染、独立全球目录读取与内容指纹校验；显式缩放/优先级与旧 WWP 像素契约，配地图测试 |

---

## 6. 安装器 / 更新器

| 路径 | 职责 |
|---|---|
| `src/installer/CMO_Chinese_Setup.cs` | 安装器（约 2000 行，**CRLF**）：安装→清单→设置保留→回退 |
| `src/updater/CMO_Updater.cs` | 更新助手（约 1500 行）：`--check` 检测新版本、提示升级/回退；安装对话框读 `CMOLocalizer_Status.txt` 的 `build_check`，构建号与本包不匹配时红字示警（R87） |

两者都依赖 `src/shared/CMOShared.cs`。改动后必须跑 installer 门禁（见 §8）。

---

## 7. 词典数据（`dict/`）

### 7.1 唯一事实源：`dict/src/`

`build_dict.py` 会 `glob("dict/src/*.json")` 全部读取，所以**放进去就是生效的**。

| 路径 | 职责 |
|---|---|
| `dict/src/terms.json` | 组合翻译术语表（127+ 条），镜像实现在 `scripts/compose_terms.py` |
| `dict/src/force_offsets.json` | 按池索引强制改写的条目（**谨慎**：形状铁律见 §11） |
| `dict/src/pool_display_inject.json` | 池级注入表（6000+ 条），形状由 `scripts/inject_filter.py` 判定 |
| `dict/src/blacklist.json` | 绝不允许翻译的串（引擎标识符、代码片段） |
| `dict/src/identifiers.json` | 引擎标识符清单（图层名、缓存键） |
| `dict/src/labels.json` | 界面标签 |
| `dict/src/menu_terms.json` | 菜单术语 |
| `dict/src/campaign_ui.json` | 战役界面串 |
| `dict/src/scenario_titles.json` | 想定标题 |
| `dict/src/scenario_settings.json` | 想定设置项 |
| `dict/src/ui_cleanup_terms.json` | 界面清理类替换 |
| `dict/src/term_replacements.json` | 术语级替换规则 |
| `dict/src/legacy_fixes.json` | 历史遗留修复 |
| `dict/src/legacy_preserved.json` | 旧产物独有 exact/安全 offsets 的显式源，最低优先级保全；构建不再读取输出作为底座 |
| `dict/src/legacy_garbage_cleaned.json` | 早期"按词拼接"垃圾串的人工清洗结果 |
| `dict/src/sentence_fragment_fixes.json` | 句子碎片修复 |
| `dict/src/live_audit_fixes.json` | 实机审计发现缺口的补齐 |
| `dict/src/live_message.json`、`dict/src/live_side_p1.json`、`dict/src/live_side_p2.json` | 实机消息/简报补译 |
| `dict/src/tutorial_gap_fixes.json`、`dict/src/tutorial_prose_fixes.json` | 教程缺口与散文修复 |
| `dict/quickbattle_html.json` | **快速战斗表单页译文源**（20 个 `QuickBattle/<条目>/Form.html` 的显示文本，键为页面原文片段）。**刻意不放进 `dict/src/`**：`build_dict.py` 会 glob `dict/src/*.json` 全部并入 `exact`，长句进去会污染界面翻译。由 `scripts/quickbattle_html.py` 的 `work`/`import` 维护，`build` 阶段消费 |
| `dict/src/db_*.json` | 数据库汉化（详见 §7.4） |
| `dict/src/*_message.json`、`dict/src/*_side.json` | 各战役简报正文（Chains of War / Desert Storm / Falklands / Kashmir Fire / Northern Inferno / Red Tide / Shifting Sands / The Silent Service / Tides of War / Fail Safe 等） |
| `dict/src/chunk0*.json` | 早期批量翻译的分片（历史批次，勿新增） |
| `dict/src/air_ops_terms.json` | 空中作战术语 |
| `dict/src/dlc_briefing_fixes.json` | DLC 简报修复 |
| `dict/briefing_source_paragraphs.json` | 简报原文清单（英文段落 = 运行时整段键），由 `audit_briefing_paragraphs.py --export` 生成，供 R82 门禁消费 |
| `dict/src/dlc_briefing_paragraphs.json` | DLC 简报与战役介绍/结局的**整段**译文（R81/R83）：键 = 实机 `InnerText`/文本节点原文，由 `build_dict` 简报批次（`allow_sensitive=True`）并入 `exact`；整段替换通道 ⓪ 与 WebView2 桥消费。新增时键必须逐字符照抄 |
| `dict/src/hotkeys_ui.json` | **快捷键窗口正文整段译文**（R91，6 段）：键 = `DarkRichTextBox` 的完整英文文本（制表符/补空格原样），值 = 中文整段。只进 `exact`、不进 `offsets`（池里没有这些原文）；构建走独立循环，**不做**简报批次的散文清洗（多空格合并会把两列排版挤歪）。改快捷键文案时同步 `dict/hotkeys/hotkeys_source.json` |
| `dict/src/live_message.json`、`dict/src/live_side_p1.json`、`dict/src/live_side_p2.json` | 实机审计补译（消息正文 / 简报正文分片） |
| `dict/src/showcase_message.json`、`dict/src/showcase_side.json` | Showcase 战役简报正文 |
| `dict/src/standalone_scenarios_message.json`、`dict/src/standalone_scenarios_side_p1.json`、`dict/src/standalone_scenarios_side_p2.json`、`dict/src/standalone_scenarios_side_p3_c1.json`、`dict/src/standalone_scenarios_side_p3_c2.json`、`dict/src/standalone_scenarios_side_p3_c3.json`、`dict/src/standalone_scenarios_side_p4_c1.json`、`dict/src/standalone_scenarios_side_p4_c2.json`、`dict/src/standalone_scenarios_side_p4_c3.json` | 独立想定简报正文（消息 + 按章节切分的 side 分片） |
| `dict/src/chains_of_war_message.json` | Chains of War 简报正文 |
| `dict/src/desert_storm_message_p1.json`、`dict/src/desert_storm_message_p2.json`、`dict/src/desert_storm_side.json` | Desert Storm 简报正文 |
| `dict/src/falklands_message.json`、`dict/src/falklands_side.json` | Falklands 简报正文 |
| `dict/src/kashmir_fire_message.json`、`dict/src/kashmir_fire_side.json` | Kashmir Fire 简报正文 |
| `dict/src/northern_inferno_message.json`、`dict/src/northern_inferno_side.json` | Northern Inferno 简报正文 |
| `dict/src/red_tide_message.json`、`dict/src/red_tide_side.json` | Red Tide 简报正文 |
| `dict/src/shifting_sands_message.json`、`dict/src/shifting_sands_side.json` | Shifting Sands 简报正文 |
| `dict/src/the_silent_service_message.json`、`dict/src/the_silent_service_side.json` | The Silent Service 简报正文 |
| `dict/src/tides_of_war_message.json` | Tides of War 简报正文 |
| `dict/src/tutorial_gap_fixes.json`、`dict/src/tutorial_prose_fixes.json` | 教程缺口 / 散文修复 |

### 7.2 产物

| 路径 | 职责 |
|---|---|
| `dict/CMOLocalizer.json` | **构建产物**（纯 LF）。引擎运行时读它。**禁止手改** |
| `dict/strings_pool.json` | 游戏字符串池的抽取结果（`triage.py` 产出，翻译工作队列的来源） |
| `dict/scenario_meta.json` | 想定元数据（标题/设置/战役名）。由 `scenario_meta.py` 生成，键已排序保证稳定 |
| `dict/ui_terms.json` | 界面术语表 |
| `dict/glossary.json` | 术语对照（人工裁定） |
| `dict/cmo_master_dictionary.json` | 早期主词库（历史，保留参考） |
| `dict/all_scen_items.json`、`dict/extracted_campaigns_scens.json`、`dict/scen_tree_dict.json` | 想定/战役结构抽取结果 |
| `dict/briefing_dict.json` | 简报词典 |
| `dict/brief_work.json`、`dict/brief_translations.json`、`dict/brief_after.json` | 简报翻译工作队列/结果中间态 |
| `dict/placenames/cities.json` | **城市地名审核源**（4,287 条，按 WWP 顺序）。逐项含原名/坐标/中文/来源/说明，是 Cities.wwp 的唯一事实源 |
| `dict/placenames/synoptic.json` | **区域标签审核源**（321 条）。同上结构，对应 Synoptic.wwp |
| `dict/placenames/metadata.json` | 地名审核的固定元数据指纹。`verify_all` / `verify_placenames.py` / `package.sh` 都用它判断「记录集合是否被改过」（改 flags、换顺序、容差内挪坐标都会失败） |
| `dict/placenames/global_reviews.jsonl` | 独立全球地图目录的人工审核新增译名，绑定源记录指纹；由 global_map_catalog 的 --reviews 读取，不修改原 WWP 数量与元数据 |
| `dict/placenames/regions.json` | **省/州级标签审核源**（3,245 条，R88）：中文取 Natural Earth `name_zh`，锚点取同记录的 label 点，`min_zoom` 沿用上游建议值。是词典 `places` 分区的唯一事实源（不进 WWP：WWP 无缩放字段，且两套集有指纹门禁） |
| `dict/hotkeys/hotkeys_source.json` | **快捷键窗口英文原文采集**（R91，6 段控件全文本）：由实机 `CTRL_TEXT 快捷键` 导出，是 `verify_hotkeys_ui.py` 的对照底座。游戏更新改了这些文案时重新采集 |
| `dict/all_scen_items.json` | 从所有想定抽出的可译条目快照 |
| `dict/extracted_campaigns_scens.json` | 战役内想定清单抽取结果 |
| `dict/scen_tree_dict.json` | 想定树结构词典（加载对话框用） |
| `dict/brief_work.json`、`dict/brief_translations.json`、`dict/brief_after.json` | 简报翻译工作队列 / 译文中转 / 合并结果 |
| `dict/chunks/*`、`dict/brief_src/*`、`dict/brief_chunks/*` | 批量翻译的分片输入/输出（历史批次；`chunk0*.json` / `brief0*.json`） |

### 7.3 数据库汉化来源

| 路径 | 职责 |
|---|---|
| `dict/src/db_enums.json`、`dict/src/db_enums_complete.json`、`dict/src/db_enums_extra.json` | 数据库枚举值（国家/阵营等） |
| `dict/src/db_enum_displays.json` | 枚举显示名 |
| `dict/src/db_record_names.json` | 装备/设施记录名 |
| `dict/src/db_descriptions.json` | 数据库查看器描述正文（**最大的单个源**） |
| `dict/src/db_platforms_facilities.json` | 四类高原生平台/设施名 |
| `dict/src/db_comments.json` | 数据库列表**行尾 Comments** 的分段词表（R72）。落进词典的独立 `comments` 分区，**绝不并进 `exact`**：`Tank`(油箱)/`Mount`(挂架)/`Group`(编组) 与界面同名词条含义不同，合并会译坏界面 |
| `dict/src/db_viewer_html_terms.json` | 数据库查看器 HTML 模板术语 |
| → 目录 | `assets/db_templates/*.html` 是 8 个 HTML 模板（见 §10） |

---

## 8. 门禁与验证（改完必须跑对应项）

### 8.1 总入口

| 路径 | 职责 |
|---|---|
| `scripts/verify_all.py` | 统一验收：`gate_version`（五处版本 + 构建号）、`gate_scenario_meta`、`gate_readme_downloads` 等 |
| `scripts/verify_regressions.py` | 回归测试（台账 `BUG_REGISTRY.md` 的可执行版本）。`--static` / `--static --installer` / `--live` |
| `scripts/verify_routing.py` | **本文件的门禁**：每个受控文件都必须在本表登记 |

### 8.2 专项门禁

| 路径 | 职责 |
|---|---|
| `scripts/verify_engine_parse.py` | 按引擎的方式解析 `dict/CMOLocalizer.json`（防"字典合法但引擎读不了"） |
| `scripts/verify_terminology.py` | 术语一致性 |
| `scripts/verify_menu.py` | 菜单串全译 + 输出术语表 |
| `scripts/verify_placenames.py` | 已评审地名检查（可证明只发布批准过的名字） |
| `scripts/build_regions_placenames.py` | 从 Natural Earth `ne_10m_admin_1_states_provinces.dbf` 生成 `dict/placenames/regions.json`（省/州层，R88）；`--check` 校验提交版与 DBF 重建一致。不联网：DBF 路径由参数传入 |
| `scripts/verify_regions_placenames.py` | **省/州标签覆盖门禁**（R88）：源条数下限、中文/坐标/min_zoom 合法性、去重，并校对词典 `places` 与源一一对应；`verify_all` 的 placenames 门禁调用它 |
| `scripts/verify_hotkeys_ui.py` | **快捷键窗口正文门禁**（R91）：源/译文行数与键列（含制表符）逐字节对齐、说明含中文且无残留虚词、词典产物与源一致；`verify_all` 的 hotkeys 门禁调用它 |
| `scripts/verify_live_test.py` | 实机测试产物/截图/证据完整性 |
| `scripts/compose_terms.py` | 与引擎组合规则**一一对应**的镜像实现；`--check` 审计半译残留 |
| `scripts/inject_filter.py` | 池级注入形状安全判定（**单一事实源**，构建与门禁共用） |
| `scripts/identifiers.py` | 清除绝不该翻译的引擎标识符 |
| `scripts/fix_static_html_charset.py` | **R78 简报 HTML 编码声明**：给「含中文但没声明 charset」的页面补 `<meta charset>`（不补则 mshtml 按 ANSI 解码、整片乱码），`--check` 供门禁，`--apply` 就地修复已安装文件 |

### 8.3 审计 / 统计工具（只读）

| 路径 | 职责 |
|---|---|
| `scripts/audit_coverage.py` | 词典覆盖率审计 |
| `scripts/audit_briefing_paragraphs.py` | 简报**整段**覆盖门禁（R82）：`--check` 校验 `dict/briefing_source_paragraphs.json` 每段都能命中整段中文键；`--export DIR` 从 `BRIEFING_RAW_DUMP` 抓的原文重建清单 |
| `scripts/audit_campaign_text.py` | **DLC 战役汉化盘点**：逐战役（`*.campaign` **递归**，含嵌套教程战役，R85）统计「内嵌 <ScenDescription> 散文未译段」「`*.campaign` 的 <Description>/<EndingText> 内联散文未译段」与「[LOADDOC] 外部 HTML 的英文正文段」，区分元数据行与 LOADDOC 指令，`--json` 出机读报告。改战役译文后用它复核 |
| `scripts/db_coverage_audit.py` | 数据库汉化覆盖审计（按缺口分类） |
| `scripts/db_prose_gaps.py` | 数据库描述散文缺口审计 |
| `scripts/db_prose_inventory.py` | 数据库描述散文存量盘点 |
| `scripts/db_text_inventory.py` | 数据库文本面盘点 |
| `scripts/db_find_text.py` | 定位某个英文串在 DB 的哪张表/哪一列 |
| `scripts/db_desc_work.py` | 抽取"还需要翻译"的描述行 |
| `scripts/triage.py` | 把字符串池分成翻译工作桶 |
| `scripts/brief_chunks.py` | 把简报工作清单切成均衡分片 |
| `scripts/make_chunks.py` | 把 `dict/worklist.json` 切成有序分片 |
| `scripts/batch_translate_chunks.py` | 并发批量翻译分片 |

### 8.4 实机驱动

| 路径 | 职责 |
|---|---|
| `scripts/live_harness.py` | **实机驱动核心**：启停游戏、发引擎命令、像素采样（`pixel_change_count`） |
| `scripts/live_load_tutorial.py` | 加载想定/教程并驱动入口对话框 |
| `scripts/live_sweep_tutorials.py` | 全教程实机巡检 |
| `scripts/verify_all_tutorials.py` | 教程综合验证harness |
| `scripts/test_subwindows.py` | 打开/截图/检查 7 个核心子窗体 |
| `scripts/capture_all_menus.py` | 抓取 10 个顶级菜单截图 |
| `scripts/test_installer_ui.py` | 安装器界面实机验证 |
| `scripts/test_updater_live.py` | 更新助手实机验证（`--check` 四条路径） |
| `scripts/perf_probe.py` | 卡顿探针（R49）：补扫频率 + 静止界面翻译事件 |
| `scripts/perf_probe_large.py` | **大想定**卡顿探针（R53）：渲染帧/补扫频率比值、僵尸 WPF 窗口、视觉树节点规模；`--load "Blue Dawn"` 可直接加载指定想定 |
| `scripts/live_load_blue_dawn.py` | 一键进入战争链战役第一关 Blue Dawn（战役关卡，不在想定列表，必须走 战役→系列战役→开始想定→选阵营 这条链） |
| `scripts/zombie_window_watch.py` | 观测「想定特性与拟真度选项」僵尸 WPF 窗口是否持续增长（区分真实泄漏与一次性残留） |

### 8.5 词典构建辅助

| 路径 | 职责 |
|---|---|
| `scripts/build_dict.py` | **核心构建**：`dict/src/*.json` → `dict/CMOLocalizer.json`（确定性、字节可复现） |
| `scripts/build_db_enums.py` | 生成 `db_enums.json`（扫所有 `Enum*` 表） |
| `scripts/build_db_enums_complete.py` | 国家/阵营官方规范译名对照 |
| `scripts/generate_db_platforms_facilities.py` | 生成四类平台/设施名 |
| `scripts/db_desc_merge.py` | 把译好的描述行合回 `dict/src/` |
| `scripts/clean_legacy_garbage.py` | 清洗 242 条早期"按词拼接"垃圾串 |
| `scripts/fix_sentences.py` | 修复被整句误译的简报碎片 |
| `scripts/briefing_html.py` | 保结构的 HTML 简报翻译 harness |
| `scripts/localize_html_files.py` | 翻译磁盘上的教程/战役 HTML |

### 8.6 地名 / 地图

| 路径 | 职责 |
|---|---|
| `scripts/wwp.py` | **World Wind Placename (.wwp) 读写库**（地图标签数据格式） |
| `scripts/apply_placenames.py` | 把译文写回游戏的 `.wwp` |
| `scripts/placename_translation.py` | 坐标感知的译名映射（原版/本地化一对） |
| `scripts/build_cities_placenames.py` | 离线构建译好的 Cities.wwp（优先用已评审坐标） |
| `scripts/test_global_placenames.py` | 全量地名审核的一致性测试（4,287+321 逐点唯一命中、译名一致） |
| `scripts/global_map_catalog.py` | 独立全球地图目录的来源/逐项中文审计及确定性二进制编译；缺译或来源/显示未验证时阻断输出 |
| `scripts/test_global_map_catalog.py` | 全球地图目录门禁负例、集合分母、全中文、确定性和旧产物保留回归 |
| `dict/placenames/tile_regions.json` | 拆分地图源的公共设置与有序区域索引；具体内容在 map_tiles/regions，非全球完整目录 |
| `dict/placenames/map_tiles/regions/*.json` | 按区域维护的原名、中文、布局、符号及原图引用；地图增量编辑对应区域，不重建巨型源文件 |
| `docs/MAP_LABEL_RESTORATIONS.csv` | 127 处实测缺失标注的原名、中文、缩放、原始位置、正式区域和标签 ID。 |
| `docs/MAP_LABEL_REVIEW_QUEUE.csv` | 97 张待继续核对的原图坐标，以及其尚待归属的原图像素数。 |
| `dict/placenames/packed_region_coverage.jsonl` | 记录正式地图已绘出的省级行政区及缩放，供 R88 同实体去重。 |
| `scripts/verify_packed_regions.py` | 核对正式省名的实际承载瓦片、源身份和 DAT 指纹，生成共享渲染器的行政区索引。 |
| `scripts/test_verify_packed_regions.py` | 验证行政区替代关系与同一实体在不同缩放的边界。 |
| `dict/placenames/packed_wwp_coverage.jsonl` | 经审核的普通原图地名与内置 WWP 同实体关系，按原记录及缩放保留。 |
| `scripts/verify_packed_wwp.py` | 核对实际地图、内置 WWP 身份与替代来源，并生成共享渲染器的查找表。 |
| `scripts/test_verify_packed_wwp.py` | 验证内置地名去重只作用于已绘位置和审核记录。 |
| `dict/placenames/map_tiles/regions/CN-FujianQingyuanWaitunQinyangZhouning-z9-425-215.json` | 补回原版地图 z9 的庆元县、外屯、芹洋、周宁等。 |
| `dict/placenames/map_tiles/regions/CN-GongchengPingleChaodongSanjiang-z9-413-219.json` | 补回原版地图 z9 的栗木、兴坪、葡萄、恭城等。 |
| `dict/placenames/map_tiles/regions/CN-TaianFujia-z10-860-382.json` | 补回原版地图 z10 的西佛、台安、达牛、富家。 |
| `dict/placenames/map_tiles/regions/CN-GaotuoWenxiang-z10-860-383.json` | 补回原版地图 z10 的高坨、温香。 |
| `dict/placenames/map_tiles/regions/CN-WujiaDeshengXintai-z10-859-382.json` | 补回原版地图 z10 的吴家、德生、新台。 |
| `dict/placenames/map_tiles/regions/CN-JianpingNiangniangmiao-z9-426-191.json` | 补回原版地图 z9 的杨树湾、台子、水泉、建平县等。 |
| `dict/placenames/map_tiles/regions/CN-FuxinQinghemenHeishan-z9-428-190-2x1.json` | 补回原版地图 z9 的化石戈、佛寺、长皋、清河门等。 |
| `dict/placenames/map_tiles/regions/CN-HeishanLijiaLiujia-z10-859-381.json` | 补回原版地图 z10 的黑山、励家、柳家。 |
| `dict/placenames/map_tiles/regions/CN-ShenmuWanzhen-z9-413-196.json` | 补回原版地图 z9 的马镇、贺家川、花石崖、上高寨等。 |
| `dict/placenames/map_tiles/regions/CN-DianzegouWubu-z9-413-198.json` | 补回原版地图 z9 的螅镇、义合、吴堡、石盘等。 |
| `dict/placenames/map_tiles/regions/CN-XinhuaAnhuaPingkou-z9-414-214.json` | 补回原版地图 z9 的乐安、平口、白溪、田坪等。 |
| `dict/placenames/map_tiles/regions/CN-ZhangwuPaozi-z9-430-189.json` | 补回原版地图 z9 的大德、卧牛石、包家屯、彰武等。 |
| `dict/placenames/map_tiles/regions/CN-JiangxiGuantianAntangZhiyangTaihe-z10-838-432.json` | 补回原版地图 z10 的官田、安塘、指阳、泰和。 |
| `dict/placenames/map_tiles/regions/CN-GongzhulingTiande-z9-433-187.json` | 补回原版地图 z9 的金山、万发、公主岭、靠山等。 |
| `dict/placenames/map_tiles/regions/CN-ShuangyangYitongYumin-z9-434-187.json` | 补回原版地图 z9 的双阳、二道、伊通、足民。 |
| `dict/placenames/map_tiles/regions/CN-WenzuBoshanMiaoshan-z10-846-400.json` | 补回原版地图 z10 的文祖、垛庄、雪野、张庄等。 |
| `dict/placenames/map_tiles/regions/CN-YinanZhuanbuHuyangTanyi-z10-848-403.json` | 补回原版地图 z10 的岸堤、沂南、双堠、张庄等。 |
| `dict/placenames/map_tiles/regions/CN-TanchengCaozhuang-z10-848-406.json` | 补回原版地图 z10 的曹庄、郯城。 |
| `dict/placenames/map_tiles/regions/CN-PanjinPanshanShaling-z10-859-383.json` | 补回原版地图 z10 的盘山县、双台子、盘锦、沙岭等。 |
| `dict/placenames/map_tiles/regions/CN-AnshanDatunLiuhao-z10-861-383.json` | 补回原版地图 z10 的柳壕、鞍山、大屯、耿庄。 |
| `dict/placenames/map_tiles/regions/MX-JaliscoMichoacanUruapan-z7-27-56.json` | 补回原版地图 z7 的阿瓜斯卡连特斯、拉戈斯德莫雷诺、瓜达拉哈拉、萨莫拉-德伊达尔戈等。 |
| `dict/placenames/map_tiles/regions/MX-GuerreroAcapulco-z7-28-57.json` | 补回原版地图 z7 的阿卡普尔科。 |
| `dict/placenames/map_tiles/regions/US-AlexandriaRichmond-z7-36-49.json` | 补回原版地图 z7 的亚历山德里亚、弗雷德里克斯堡、夏洛茨维尔、里士满。 |
| `dict/placenames/map_tiles/regions/CN-ChangshaXiangxiang-z9-416-214.json` | 补回原版地图 z9 的宁乡、长沙、坝塘、雨花等。 |
| `dict/placenames/map_tiles/regions/CN-JinchengYangcheng-z9-416-201.json` | 补回原版地图 z9 的高平、陵川、胡底、郑村等。 |
| `dict/placenames/map_tiles/regions/CN-LiaozhongJiangtunChangtan-z10-860-381-2x1.json` | 补回原版地图 z10 的姜屯、大兴、桓洞、辽中等。 |
| `dict/placenames/map_tiles/regions/CN-WentangHongjiang-z10-837-430.json` | 补回原版地图 z10 的温汤、洪江。 |
| `dict/placenames/map_tiles/regions/CN-ChangchunXiyingcheng-z9-434-186.json` | 补回原版地图 z9 的龙嘉、长春新区、西营城、长春等。 |
| `dict/placenames/map_tiles/regions/CN-JilinCityZuojiayilaxi-z9-435-186.json` | 补回原版地图 z9 的土们岭、左家、吉林、一拉溪等。 |
| `dict/placenames/map_tiles/regions/BR-ParanaSantaCatarinaParaguay-z6-21-36-3x1.json` | 补回原版地图 z6 的菲拉德尔菲亚、康塞普西翁、亚松森、福莫萨等。 |
| `dict/placenames/map_tiles/regions/CN-DonganLinglingShuangpai-z9-414-217.json` | 补回原版地图 z9 的东安、横塘、零陵、珠山等。 |
| `dict/placenames/map_tiles/regions/CN-LiaoyuanXianDongliao-z10-867-376-2x1.json` | 补回原版地图 z10 的灯塔、东辽、平岗、西安等。 |
| `dict/placenames/map_tiles/regions/CN-JianpingLuofugouXiaotang-z9-426-190.json` | 补回原版地图 z9 的罗福沟、建平镇、杨树岭、青松岭等。 |
| `dict/placenames/map_tiles/regions/CN-YishuiMengyinDuozhuang-z9-424-201.json` | 补回原版地图 z9 的高桥、夏蔚、沂水、沂南等。 |
| `dict/placenames/map_tiles/regions/US-HuntingtonBristolKnoxvilleAsheville-z7-34-49-1x2.json` | 补回原版地图 z7 的亨廷顿、布里斯托尔、诺克斯维尔、阿什维尔等。 |
| `dict/placenames/map_tiles/regions/CN-TielingCountyKaiyuan-z9-432-189.json` | 补回原版地图 z9 的铁岭、铁岭县、靠山、大甸子。 |
| `dict/placenames/map_tiles/regions/CN-ShifuBashenhanGezidongLeijiadian-z10-853-384.json` | 补回原版地图 z10 的石佛、巴什罕、鸽子洞、雷家店。 |
| `dict/placenames/map_tiles/regions/CN-WangqiSongjiangChangshan-z9-436-187.json` | 补回原版地图 z9 的旺起、松江、常山。 |
| `dict/placenames/map_tiles/regions/US-NorthCarolinaGoldsboroGreenvilleJacksonville-z7-36-50.json` | 补回原版地图 z7 的格林维尔、戈尔兹伯勒、杰克逊维尔。 |
| `dict/placenames/map_tiles/regions/CN-WujingSitou-z10-848-400.json` | 补回原版地图 z10 的五井、寺头。 |
| `dict/placenames/map_tiles/regions/CN-SongxiDongping-z10-849-430.json` | 补回原版地图 z10 的濠村、松溪、漳墩、东平。 |
| `dict/placenames/map_tiles/regions/TR-BatmanHasankeyf-z9-314-197.json` | 补回原版地图 z9 的哈兹罗、科兹卢克、锡尔万、巴特曼等。 |
| `dict/placenames/map_tiles/regions/TR-DargecitGuclukonakIdil-z9-315-198.json` | 补回原版地图 z9 的达尔盖奇特、居奇吕科纳克、伊迪尔。 |
| `dict/placenames/map_tiles/regions/Asia-NyingchiDibrugarh-z7-97-53.json` | 补回原版地图 z7 的林芝、阿鲁纳恰尔邦、迪布鲁加尔。 |
| `dict/placenames/map_tiles/regions/CN-XintaiYedian-z9-423-201.json` | 补回原版地图 z9 的羊流、天宝、新泰、野店等。 |
| `dict/placenames/map_tiles/regions/CN-TanchengGangtou-z9-424-203.json` | 补回原版地图 z9 的兰陵、郯城、东海、新沂等。 |
| `dict/placenames/map_tiles/regions/India-MangaluruKarnataka-z7-90-59.json` | 补回原版地图 z7 的门格洛尔、科泽科德、杜姆古尔、班加罗尔等。 |
| `dict/placenames/map_tiles/regions/India-PonnaniPreserved-z7-90-60.json` | 补回原版地图 z7 的跨片文字与符号衔接。 |
| `dict/placenames/map_tiles/regions/MY-TaipingIpoh-z8-199-124.json` | 补回原版地图 z8 的太平、怡保。 |
| `dict/placenames/map_tiles/regions/CN-DaningJiGuantou-z9-413-200.json` | 补回原版地图 z9 的大宁、山中、阁楼、文城等。 |
| `dict/placenames/map_tiles/regions/US-DelawareVirginiaBeach-z7-37-49.json` | 补回原版地图 z7 的特拉华州、弗吉尼亚海滩。 |
| `dict/placenames/map_tiles/regions/US-RoanokeDanvilleCharlotte-z7-35-49-1x2.json` | 补回原版地图 z7 的西弗吉尼亚州、贝克利、罗阿诺克、弗吉尼亚州等。 |
| `dict/placenames/map_tiles/regions/TR-KocaeliSakaryaDuzce-z9-298-192-3x1.json` | 补回原版地图 z9 的科贾埃利、萨潘贾、费里兹利、阿达帕扎勒等。 |
| `dict/placenames/map_tiles/regions/UA-ChernivtsiKitsmanHlyboka-z9-292-177.json` | 补回原版地图 z9 的基茨曼、切尔诺夫策、斯托罗日涅茨、赫利博卡等。 |
| `dict/placenames/map_tiles/regions/BYRU-KastsyukovichySurazh-z9-301-166.json` | 补回原版地图 z9 的科斯秋科维奇、苏拉日。 |
| `dict/placenames/map_tiles/regions/TR-SirnakHakkariCilo-z9-316-198-3x1.json` | 补回原版地图 z9 的舍尔纳克、吉兹雷、乌卢代雷、锡洛皮等。 |
| `dict/placenames/map_tiles/regions/BG-SamokovPazardzhik-z9-289-189-2x1.json` | 补回原版地图 z9 的萨帕雷瓦巴尼亚、萨莫科夫、莫明普罗霍德、帕纳久里什泰等。 |
| `dict/placenames/map_tiles/regions/UA-RakhivKosivVerkhovyna-z9-290-177-2x1.json` | 补回原版地图 z9 的乌斯季乔尔纳、杜博韦、亚西尼亚、沃罗赫塔等。 |
| `dict/placenames/map_tiles/regions/CN-DaxinXialeiShuolongJinlong-z10-815-445.json` | 补回原版地图 z10 的下雷、硕龙、金龙。 |
| `dict/placenames/map_tiles/regions/CN-YongxinAonanJinggangshan-z10-837-432.json` | 补回原版地图 z10 的永新县、坳南、井冈山市。 |
| `dict/placenames/map_tiles/regions/CN-WuzhouYunanLuoding-z9-414-221-1x2.json` | 补回原版地图 z9 的梧州、渔涝、罗董、郁南等。 |
| `dict/placenames/map_tiles/regions/CN-Longan-Nanning-Luoxu-z9-409-222.json` | 补回原版地图 z9 的锣圩、隆安、丁当、武鸣等。 |
| `dict/placenames/map_tiles/regions/CN-YanjinFengzhuang-z9-418-202.json` | 补回原版地图 z9 的丰庄、新乡、樊相、长垣等。 |
| `dict/placenames/map_tiles/regions/CN-ZhaopingMengshanYantang-z9-413-220.json` | 补回原版地图 z9 的荔浦、龙怀、阳安、蒙山等。 |
| `dict/placenames/map_tiles/regions/CN-GuanyangXinxuDaoJiangyong-z9-414-218.json` | 补回原版地图 z9 的桥头、江村、新圩、灌阳等。 |
| `dict/placenames/map_tiles/regions/TH-Central-NakhonSawanUthai-z9-397-233.json` | 补回原版地图 z9 的拉尧、兰萨、塔贪、那空沙旺等。 |
| `dict/placenames/map_tiles/regions/TH-Central-Kanchanaburi-z9-397-235.json` | 补回原版地图 z9 的农布、劳宽、农里、爱侣湾等。 |
| `dict/placenames/map_tiles/regions/TH-Central-ChaiNatAngThong-z9-398-234.json` | 补回原版地图 z9 的猜纳、冲凯、波南丹、汉卡等。 |
| `dict/placenames/map_tiles/regions/CNVN-Napo-PacMiau-TinhTuc-z9-406-222.json` | 补回原版地图 z9 的百省、百合、吞盘、坡苗等。 |
| `dict/placenames/map_tiles/regions/CNVN-Jingxi-CaoBang-Baowei-z9-407-222.json` | 补回原版地图 z9 的靖西、同德、龙邦、高平等。 |
| `dict/placenames/map_tiles/regions/CN-Pubei-Bobai-Fumian-z10-823-446.json` | 补回原版地图 z10 的福绵、六硍、福旺、永安等。 |
| `dict/placenames/map_tiles/regions/ML-MarkaTemou-z10-494-472.json` | 补回原版地图 z10 的泰穆、马尔卡拉。 |
| `dict/placenames/map_tiles/regions/BF-Bangasse-z10-504-474.json` | 补回原版地图 z10 的班加塞。 |
| `dict/placenames/map_tiles/regions/LY-AlUrqub-z9-273-208.json` | 补回原版地图 z9 的阿尔乌尔库卜。 |
| `dict/placenames/map_tiles/regions/CDUG-KyavinyongeRubirizi-z9-298-256.json` | 补回原版地图 z9 的基亚维尼翁盖、鲁比里济。 |
| `dict/placenames/map_tiles/regions/UG-SembabuleRakai-z9-300-256.json` | 补回原版地图 z9 的森巴布雷、布科曼辛比、良通德、恩代巴等。 |
| `dict/placenames/map_tiles/regions/CD-BukamaKolweziLikasi-z7-73-67.json` | 补回原版地图 z7 的布卡马、科卢韦齐、利卡西。 |
| `dict/placenames/map_tiles/regions/TR-BilecikIznikOsmaneli-z9-298-193.json` | 补回原版地图 z9 的伊兹尼克、奥斯曼埃利、耶尼谢希尔、比莱吉克等。 |
| `dict/placenames/map_tiles/regions/TR-KutahyaDomanicInonu-z9-298-194.json` | 补回原版地图 z9 的多马尼奇、伊诺努、屈塔希亚。 |
| `dict/placenames/map_tiles/regions/TR-KahramanmarasPazarcik-z9-308-198.json` | 补回原版地图 z9 的卡赫拉曼马拉什、帕扎尔哲克。 |
| `dict/placenames/map_tiles/regions/TR-ElmaliCavdirKorkuteli-z9-298-199.json` | 补回原版地图 z9 的恰夫德尔、科尔库泰利、埃尔马勒。 |
| `dict/placenames/map_tiles/regions/SO-QardhoGaroowe-z7-81-60-1x2.json` | 补回原版地图 z7 的卡尔多、加洛威。 |
| `dict/placenames/map_tiles/regions/PH9-SouthBoholCamiguin-z9-432-242-2x1.json` | 补回原版地图 z9 的塔比拉兰、瓦伦西亚、哈格纳、金杜尔曼等。 |
| `dict/placenames/map_tiles/regions/PH9-DinagatSurigao-z9-434-241.json` | 补回原版地图 z9 的洛雷托、巴西利萨、圣里卡多、苏里高市。 |
| `dict/placenames/map_tiles/regions/CN-HezhouFuchuanZhongshanPinggui-z10-828-439-2x2.json` | 补回原版地图 z10 的富川、莲山、柳家、红花等。 |
| `dict/placenames/map_tiles/regions/CN-TengPingnan-z9-413-221.json` | 补回原版地图 z9 的平福、岭脚、平南、濛江等。 |
| `dict/placenames/map_tiles/regions/CN-SandaoErlongshanJianan-z10-867-375.json` | 补回原版地图 z10 的三道、二龙山水库、建安。 |
| `dict/placenames/map_tiles/regions/CN-ChangtuQinghe-z9-432-188.json` | 补回原版地图 z9 的东嘎、昌图、城东、八宝等。 |
| `dict/placenames/map_tiles/regions/CN-FurongXinlitunFangshan-z10-859-380.json` | 补回原版地图 z10 的新立屯、富荣、芳山。 |
| `dict/placenames/map_tiles/regions/CN-ShenyangSujiatun-z9-431-190.json` | 补回原版地图 z9 的沈北、沈阳、苏家屯、王纲。 |
| `dict/placenames/map_tiles/regions/US-KentuckyLouisvilleLexingtonBowlingGreen-z7-33-49.json` | 补回原版地图 z7 的路易维尔、列克星敦、肯塔基州、鲍灵格林。 |
| `dict/placenames/map_tiles/regions/GL-Upernavik-z5-11-6.json` | 补回原版地图 z5 的乌佩纳维克。 |
| `dict/placenames/map_tiles/regions/US-Texas-ShermanDallas-z8-59-102-1x2.json` | 补回原版地图 z8 的谢尔曼、达拉斯。 |
| `dict/placenames/map_tiles/regions/US-TennesseeNashvilleChattanoogaHuntsville-z7-33-50.json` | 补回原版地图 z7 的纳什维尔、田纳西州、查塔努加、亨茨维尔。 |
| `dict/placenames/map_tiles/regions/DZ-AkbouGuendouzBordjZemmoura-z9-262-200.json` | 补回原版地图 z9 的阿克布、根杜兹、布尔吉泽穆拉。 |
| `dict/placenames/map_tiles/regions/CN-WeichangLaowopu-z9-422-190.json` | 补回原版地图 z9 的老窝铺、牌楼、大头山、南山嘴等。 |
| `dict/placenames/map_tiles/regions/CN-ShenmuDachanghan-z9-413-195.json` | 补回原版地图 z9 的大昌汗、庙沟门、孙家岔、孤山等。 |
| `dict/placenames/map_tiles/regions/CN-KangbaoHongqi-z9-419-190.json` | 补回原版地图 z9 的红旗、屯垦、康保、闫油坊等。 |
| `dict/placenames/map_tiles/regions/CN-ShengliYangjuegouGuzhangzi-z10-853-383.json` | 补回原版地图 z10 的胜利、羊角沟、谷杖子。 |
| `dict/placenames/map_tiles/regions/CN-DongkouLaotian-z9-413-215.json` | 补回原版地图 z9 的鸭田、接龙、铁坡、七江等。 |
| `dict/placenames/map_tiles/regions/VN-LangChanh-PhongSon-KimTan-z10-811-453.json` | 补回原版地图 z10 的丰山、郎正、玉乐、金新。 |
| `dict/placenames/map_tiles/regions/CN-Longzhou-Xiadong-Zhubu-z10-815-446.json` | 补回原版地图 z10 的上龙、下冻、龙州、逐卜。 |
| `dict/placenames/map_tiles/regions/CN-LedongCounty-z10-822-457.json` | 补回原版地图 z10 的乐东黎族自治县。 |
| `dict/placenames/map_tiles/regions/CN-GuangningJiangtunWeizheng-z10-831-442-2x1.json` | 补回原版地图 z10 的赤坑、坑口、广宁、江屯等。 |
| `dict/placenames/map_tiles/regions/CN-XinyuShanggao-z9-419-214.json` | 补回原版地图 z9 的上高、建山、洞村、新余等。 |
| `dict/placenames/map_tiles/regions/CN-FengningCaoyuanWanshengyong-z9-421-190.json` | 补回原版地图 z9 的草原、万胜永。 |
| `dict/placenames/map_tiles/regions/CN-HongqiRaoyanghe-z10-860-380.json` | 补回原版地图 z10 的红旗、绕阳河。 |
| `dict/placenames/map_tiles/regions/CN-RaopingHuanggang-z10-844-442.json` | 补回原版地图 z10 的饶平。 |
| `dict/placenames/map_tiles/regions/CN-ZoupingZhangqiu-z10-846-399.json` | 补回原版地图 z10 的邹平、章丘。 |
| `dict/placenames/map_tiles/regions/US-Utah-EphraimCastleDale-z9-97-195-2x1.json` | 补回原版地图 z9 的伊弗雷姆、卡斯尔代尔。 |
| `dict/placenames/map_tiles/regions/CN-Pengfang-z10-837-431.json` | 补回原版地图 z10 的彭坊。 |
| `dict/placenames/map_tiles/regions/CN-FenyiXinyu-z10-838-429.json` | 补回原版地图 z10 的分宜、新余。 |
| `dict/placenames/map_tiles/regions/CN-LinyiLanshan-z10-848-405.json` | 补回原版地图 z10 的临沂、兰山、罗庄。 |
| `dict/placenames/map_tiles/regions/CN-QingyuanZhenghe-z10-850-430.json` | 补回原版地图 z10 的庆元县、岭腰、铁山。 |
| `dict/placenames/map_tiles/regions/CN-ShouningTaishun-z10-851-430.json` | 补回原版地图 z10 的贤良、岭头、寿宁、下党等。 |
| `dict/placenames/map_tiles/regions/CN-LiaoyangCityDengtaShilihe-z10-862-382.json` | 补回原版地图 z10 的十里河、灯塔、辽阳。 |
| `dict/placenames/map_tiles/regions/BY-CherykawSlawharad-z9-300-165.json` | 补回原版地图 z9 的切里科夫、斯拉夫哥罗德。 |
| `dict/placenames/map_tiles/regions/PL-KonskieKielcePinczow-z9-285-171-1x2.json` | 补回原版地图 z9 的孔斯凯、斯通波尔库夫、斯卡日斯科卡缅纳、博曾滕等。 |
| `dict/placenames/map_tiles/regions/CN-Hengzhou-Guigang-z9-411-222.json` | 补回原版地图 z9 的中华、覃塘、贵港、陶圩等。 |
| `dict/placenames/map_tiles/regions/CN-HebiLaodian-z9-418-201.json` | 补回原版地图 z9 的鹤山、山城、鹤壁、淇县等。 |
| `dict/placenames/map_tiles/regions/CN-WenzuBoshanLaiwu-z9-423-200.json` | 补回原版地图 z9 的文祖、源泉、博山镇、莱芜等。 |
| `dict/placenames/map_tiles/regions/CN-HeishanXinmin-z9-430-190.json` | 补回原版地图 z9 的英城子、梁山、新民、兴隆等。 |
| `dict/placenames/map_tiles/regions/CN-LinquYiyuanZhangzhuang-z9-424-200.json` | 补回原版地图 z9 的张庄、临朐、寺头、沂源等。 |
| `dict/placenames/map_tiles/regions/CN-LiaoyuanDongliaoJianan-z9-433-188.json` | 补回原版地图 z9 的建安、柏榆、辽源、莲花等。 |
| `dict/placenames/map_tiles/regions/CN-LiaoyuanDengtaDongfeng-z9-434-188.json` | 补回原版地图 z9 的灯塔、朝阳山、二龙山、东丰等。 |
| `dict/placenames/map_tiles/regions/CN-LiaoyangXiaotunGongchangling-z9-431-191.json` | 补回原版地图 z9 的辽阳、小屯、弓长岭。 |
| `dict/placenames/map_tiles/regions/CN-NanfengBaishe-z9-421-215.json` | 补回原版地图 z9 的南城、宜黄、南源、南丰等。 |
| `dict/placenames/map_tiles/regions/CN-YunfuGenghe-z9-415-222.json` | 补回原版地图 z9 的德庆、云浮、南盛、更合等。 |
| `dict/placenames/map_tiles/regions/CN-ChangzhiLiangma-z9-416-200.json` | 补回原版地图 z9 的襄垣、杨安、侯堡、屯留等。 |
| `dict/placenames/map_tiles/regions/CN-Liaoning-BeipiaoQuanjuyong-z9-427-190.json` | 补回原版地图 z9 的金厂沟梁、泉巨永、龙潭、北票等。 |
| `dict/placenames/map_tiles/regions/CN-Liaoning-Panshan-z9-429-191.json` | 补回原版地图 z9 的桑林、甜水、盘山、沙岭等。 |
| `dict/placenames/map_tiles/regions/CN-LinyiShuanghouLizhuang-z9-424-202.json` | 补回原版地图 z9 的双堠、青驼、李官、道口等。 |
| `dict/placenames/map_tiles/regions/FJ2-XiamenQuanzhou-z10-848-439-2x1.json` | 补回原版地图 z10 的翔安区、东石、晋江、石狮等。 |
| `dict/placenames/map_tiles/regions/CN-WeihaiWendengBoyu-z10-859-397.json` | 补回原版地图 z10 的泊于、温泉、苘山、文登。 |
| `dict/placenames/map_tiles/regions/CN-Lingshan-Pubei-Guandong-Shahe-z9-411-223.json` | 补回原版地图 z9 的灵山、浦北、北通、南乡等。 |
| `dict/placenames/map_tiles/regions/CN-PingxiangFanglouEast-z9-418-215.json` | 补回原版地图 z9 的芦溪、浒坑、坊楼、彭坊。 |
| `dict/placenames/map_tiles/regions/CN-DabuQingxi-z10-843-440.json` | 补回原版地图 z10 的青溪、茶阳、松口、大埔。 |
| `dict/placenames/map_tiles/regions/CN-ZhaoanChendai-z10-845-442.json` | 补回原版地图 z10 的红星、建设、陈岱、诏安等。 |
| `dict/placenames/map_tiles/regions/CN-GuangningLuogang-z9-415-221.json` | 补回原版地图 z9 的南丰、螺岗、诗洞、水南等。 |
| `dict/placenames/map_tiles/regions/VN-HoiXuan-PhongSon-NgocLac-z9-405-226.json` | 补回原版地图 z9 的回春、山闾、玉乐、丰山。 |
| `dict/placenames/map_tiles/regions/CNVN-DongKhe-Shanglong-Pingxiang-z9-407-223.json` | 补回原版地图 z9 的东溪、昂莫、凭祥、上龙。 |
| `dict/placenames/map_tiles/regions/CN-Tiandeng-Daxin-Shuolong-z9-408-222.json` | 补回原版地图 z9 的天等、福隆、大新、恩城等。 |
| `dict/placenames/map_tiles/regions/CN-JianAnfuGuantian-z9-419-215.json` | 补回原版地图 z9 的钤山、砚溪、安福县、北源等。 |
| `dict/placenames/map_tiles/regions/CN-Liaoning-JinzhouLinghaiYanjia-z9-428-191.json` | 补回原版地图 z9 的兴隆、大业、凌海、石新等。 |
| `dict/placenames/map_tiles/regions/CN-GaozhouShenzhenYangchunWest-z9-414-223.json` | 补回原版地图 z9 的合水、马贵、深镇、河口。 |
| `dict/placenames/map_tiles/regions/CN-GuangxiZhongshanHezhou-z9-414-220.json` | 补回原版地图 z9 的钟山、贺州、同古、贺街等。 |
| `dict/placenames/map_tiles/regions/MX-MatehualaCiudadVictoria-z7-28-55.json` | 补回原版地图 z7 的马特瓦拉、维多利亚城。 |
| `dict/placenames/map_tiles/regions/CN-YichunZhutan-z9-418-214.json` | 补回原版地图 z9 的小河、万载、田心、株潭等。 |
| `dict/placenames/map_tiles/regions/CN-YulinXingye-z9-412-222.json` | 补回原版地图 z9 的大圩、木根、木格、兴业等。 |
| `dict/placenames/map_tiles/regions/CN-GuYuanTaibusDatan-z9-420-190.json` | 补回原版地图 z9 的太仆寺旗、永丰、黑城子、大北沟等。 |
| `dict/placenames/map_tiles/regions/CN-ChengdePingquan-z9-424-191.json` | 补回原版地图 z9 的黑里河、磴上、柳溪、王土房等。 |
| `dict/placenames/map_tiles/regions/CN-ZhengheJianouEast-z10-849-431-2x1.json` | 补回原版地图 z10 的政和、外屯、东游、水源等。 |
| `dict/placenames/map_tiles/regions/CN-LiuyangGuanqiaoShangli-z9-417-214.json` | 补回原版地图 z9 的洞阳、浏阳、柏加、官桥等。 |
| `dict/placenames/map_tiles/regions/CN-ZhuchengWulianJuxian-z9-425-201.json` | 补回原版地图 z9 的东莞、诸城、五莲、莒县等。 |
| `dict/placenames/map_tiles/regions/UA9-Perehinske-Kolomyia-Zastavna-z9-290-176-3x1.json` | 补回原版地图 z9 的佩雷欣斯凯、博霍罗德恰尼、索洛特温、纳德维尔纳等。 |
| `dict/placenames/map_tiles/regions/UA-MD9-Dunaivtsi-Sokyriany-Briceni-z9-294-176-2x2.json` | 补回原版地图 z9 的杜纳伊夫齐、新乌希察、科派霍罗德、穆罗瓦尼库里利夫齐等。 |
| `dict/placenames/map_tiles/regions/CN-LiaoyangHongwei-z10-862-383.json` | 补回原版地图 z10 的小屯、宏伟、辽阳县、立山。 |
| `dict/placenames/map_tiles/regions/Pacific-FijiLabasa-z6-63-34-1x2.json` | 补回原版地图 z6 的拉巴萨、斐济。 |
| `dict/placenames/map_tiles/regions/CN-BaodeQiaotouLugu-z9-414-195.json` | 补回原版地图 z9 的鹿固、土沟、府谷、桥头等。 |
| `dict/placenames/map_tiles/regions/JP9-KansaiOsakaNaraMie-z9-448-203.json` | 补回原版地图 z9 的宝冢、枚方、神户、大阪市等。 |
| `dict/placenames/map_tiles/regions/CN-EnpingYangchun-z9-415-223.json` | 补回原版地图 z9 的河朗、里洞、合水、阳春等。 |
| `dict/placenames/map_tiles/regions/CN-PuyangNeihuang-z9-419-201.json` | 补回原版地图 z9 的内黄、清丰、王庄、濮阳市等。 |
| `dict/placenames/map_tiles/regions/RU-KhantyMansiyskOkrugUgra-z6-44-17-1x2.json` | 补回原版地图 z6 的汉特-曼西自治区-尤格拉。 |
| `dict/placenames/map_tiles/regions/MX-GuanajuatoMexicoCityPachucaTuxpam-z7-28-56-2x1.json` | 补回原版地图 z7 的圣路易斯波托西、瓜纳华托、萨拉曼卡、克雷塔罗等。 |
| `dict/placenames/map_tiles/regions/PH9-NegrosSouthernSeam-z9-430-241.json` | 补回原版地图 z9 的拉卡洛塔、卡瓦延、卡班卡兰、坎多尼等。 |
| `dict/placenames/map_tiles/regions/PH9-CebuBoholSouthLeyte-z9-432-241.json` | 补回原版地图 z9 的宿务市、伊纳班加、卡门、隆等。 |
| `dict/placenames/map_tiles/regions/BR-GoiasBahiaMinas-z6-23-34-2x2.json` | 补回原版地图 z6 的戈亚斯州、巴西利亚、巴雷拉斯、巴伊亚州等。 |
| `dict/placenames/map_tiles/regions/BR-NovaVicosa-z6-25-35.json` | 补回原版地图 z6 的新维索萨。 |
| `dict/placenames/map_tiles/regions/UA9-Volnovakha-Vuhledar-Ilovaisk-z9-308-178-3x1.json` | 补回原版地图 z9 的大诺沃西尔卡、武赫莱达尔、拉里内、多库恰耶夫斯克等。 |
| `dict/placenames/map_tiles/regions/UA9-Polohy-Mariupol-Novoazovsk-z9-307-179-4x1.json` | 补回原版地图 z9 的波洛吉、切尔尼希夫卡、安德里伊夫卡、卡缅卡等。 |
| `dict/placenames/map_tiles/regions/BYRU-KarmaKrasnapollyeKrasnayaGora-z9-299-166-2x1.json` | 补回原版地图 z9 的卡尔马、克拉斯纳波利耶、克拉斯纳亚戈拉。 |
| `dict/placenames/map_tiles/regions/CN-ShaoyangLengshuijiang-z9-414-215.json` | 补回原版地图 z9 的冷水江、涟源、滩头、邵阳等。 |
| `dict/placenames/map_tiles/regions/PL-OstrowiecTarnobrzegDebica-z9-286-171-1x3.json` | 补回原版地图 z9 的伊乌扎、利普斯科、斯塔拉霍维采、奥斯特罗维茨希维托克日斯基等。 |
| `dict/placenames/map_tiles/regions/RU-TalovayaButurlinovkaUryupinsk-z9-313-171-3x1.json` | 补回原版地图 z9 的塔洛瓦亚、布图尔利诺夫卡、新霍皮奥尔斯基、乌留平斯克。 |
| `dict/placenames/map_tiles/regions/PH9-SamarLeyte-z9-433-239.json` | 补回原版地图 z9 的卡特巴洛甘、达拉姆、圣丽塔、塔克洛班等。 |
| `dict/placenames/map_tiles/regions/PH9-SamarSeaNaval-z9-432-239.json` | 补回原版地图 z9 的萨马海、纳瓦尔、达安班塔延、比利亚尔瓦。 |
| `dict/placenames/map_tiles/regions/Iran-Qazvin-Karaj-Tehran-Semnan-z8-163-100-4x1.json` | 补回原版地图 z8 的加兹温、4176米、4190米、4072米等。 |
| `dict/placenames/map_tiles/regions/Iran-Rasht-z8-163-99.json` | 补回原版地图 z8 的拉什特。 |
| `dict/placenames/map_tiles/regions/PH9-IloiloNorthNegros-z9-430-240.json` | 补回原版地图 z9 的马阿辛、波托坦、伊洛伊洛、恩里克·马加洛纳等。 |
| `dict/placenames/map_tiles/regions/TH-LopBuri-Saraburi-z9-399-234.json` | 补回原版地图 z9 的沙博、南那莱、科三隆、塔銮等。 |
| `dict/placenames/map_tiles/regions/TH-Bangkok-SamutPrakan-z9-398-236.json` | 补回原版地图 z9 的暖武里、曼谷、北榄、洪信等。 |
| `dict/placenames/map_tiles/regions/CN-WuzhishanMaoyang-z10-823-457.json` | 补回原版地图 z10 的毛阳、万冲、毛道、五指山市。 |
| `dict/placenames/map_tiles/regions/CN-LingshuiHaitang-z10-824-458.json` | 补回原版地图 z10 的群英、陵水黎族自治县、海棠区、英州。 |
| `dict/placenames/map_tiles/regions/CN-CenxiRong-z9-413-222.json` | 补回原版地图 z9 的六陈、三堡、岑溪、容县等。 |
| `dict/placenames/map_tiles/regions/CN-WeichangShanwan-z9-423-190.json` | 补回原版地图 z9 的围场、腰站、下伙房、山湾。 |
| `dict/placenames/map_tiles/regions/CN-QingyuanDagujia-z9-433-189.json` | 补回原版地图 z9 的八棵树、李家台、大孤家、夏家堡等。 |
| `dict/placenames/map_tiles/regions/CN-ShaoyangYongzhou-z9-414-216.json` | 补回原版地图 z9 的邵阳县、大盛、巡田、靖位等。 |
| `dict/placenames/map_tiles/regions/CN-PanshiNorthYantongshan-z9-435-187.json` | 补回原版地图 z9 的金家、黄榆、烟筒山、吉昌等。 |
| `dict/placenames/map_tiles/regions/CN-FuchuanJianghua-z9-414-219.json` | 补回原版地图 z9 的江华、桃川、城北、富川等。 |
| `dict/placenames/map_tiles/regions/CN-AmurHegangFujin-z7-110-44-2x1.json` | 补回原版地图 z7 的犹太自治州、比罗比詹、鹤岗、富锦。 |
| `dict/placenames/map_tiles/regions/MEAL-UlcinjVelipojeLezhe-z9-283-190.json` | 补回原版地图 z9 的乌尔齐尼（乌尔钦）、韦利波耶、莱什。 |
| `dict/placenames/map_tiles/regions/ALKSMK-PukePrizrenPeshkopiTetovo-z9-284-189-2x2.json` | 补回原版地图 z9 的巴伊拉姆库里、贾科维察、拉霍韦茨、什特尔普采等。 |
| `dict/placenames/map_tiles/regions/PL-KrasnikSandomierzNisko-z9-287-171-1x2.json` | 补回原版地图 z9 的安诺波尔、乌任杜夫、克拉希尼克、扎克利库夫等。 |
| `dict/placenames/map_tiles/regions/PL-KrosnoJasloSanok-z9-286-174-2x1.json` | 补回原版地图 z9 的亚斯沃、克罗斯诺、戈尔利采、杜克拉等。 |
| `dict/placenames/map_tiles/regions/RU-LiskiBobrov-z9-312-171.json` | 补回原版地图 z9 的利斯基、博布罗夫。 |
| `dict/placenames/map_tiles/regions/RU-PodgorenskiyPavlovsk-z9-312-172-2x1.json` | 补回原版地图 z9 的波德戈连斯基、巴甫洛夫斯克。 |
| `dict/placenames/map_tiles/regions/RU-Volga-ChistopolNizhnekamskChelny-z8-163-80.json` | 补回原版地图 z8 的奇斯托波尔、下卡姆斯克、卡马河畔切尔尼。 |
| `dict/placenames/map_tiles/regions/TH-Phuket-ThungSong-Trang-z8-197-122.json` | 补回原版地图 z8 的普吉、通宋、董里。 |
| `dict/placenames/map_tiles/regions/TH-MY-Songkhla-HatYai-Kedah-z8-199-122.json` | 补回原版地图 z8 的宋卡、合艾、亚罗士打、双溪大年。 |
| `dict/placenames/map_tiles/regions/VN-BacKan-ChoRa-YenLac-z9-406-223.json` | 补回原版地图 z9 的博布、佐拉、云松、燕乐等。 |
| `dict/placenames/map_tiles/regions/JP9-KiiTanabeKushimotoSeam-z9-448-205.json` | 补回原版地图 z9 的跨片文字与符号衔接。 |
| `dict/placenames/map_tiles/regions/JP9-KiiShinguKushimoto-z9-449-204.json` | 补回原版地图 z9 的尾鹫、熊野、新宫、串本。 |
| `dict/placenames/map_tiles/regions/PH9-CebuCamotes-z9-432-240.json` | 补回原版地图 z9 的博戈、帕隆蓬、图布兰、卡门等。 |
| `dict/placenames/map_tiles/regions/PH9-EastSamar-z9-434-240.json` | 补回原版地图 z9 的马拉布特、萨尔塞多、吉万。 |
| `dict/placenames/map_tiles/regions/PH9-NorthSamarPalapag-z9-433-237.json` | 补回原版地图 z9 的帕拉帕格、卡塔曼、拉斯纳瓦斯、卡尔巴约格等。 |
| `dict/placenames/map_tiles/regions/MX-PueblaVeracruzOaxacaOmetepec-z7-29-57-1x2.json` | 补回原版地图 z7 的普埃布拉、韦拉克鲁斯、科尔多瓦、提瓦坎等。 |
| `dict/placenames/map_tiles/regions/US-Appalachia-PittsburghBaltimore-z7-35-48-2x1.json` | 补回原版地图 z7 的坎顿、匹兹堡、惠灵、摩根敦等。 |
| `dict/placenames/map_tiles/regions/DE-RostockBerlinMagdeburgLeipzigDresden-z7-68-41.json` | 补回原版地图 z7 的罗斯托克、柏林、马格德堡、莱比锡等。 |
| `dict/placenames/map_tiles/regions/DEPL-CottbusZielonaGora-z7-69-42.json` | 补回原版地图 z7 的科特布斯（乔谢布兹）、绿山城。 |
| `dict/placenames/map_tiles/regions/UA-RU9-Donetsk-Luhansk-Rostov-z9-308-177.json` | 补回原版地图 z9 的梅若瓦、乌达奇内、波克罗夫斯克、托列茨克等。 |
| `dict/placenames/map_tiles/regions/BR-RO-BO-Riberalta-z6-20-33.json` | 补回原版地图 z6 的韦柳港、里韦拉尔塔、朗多尼亚州。 |
| `dict/placenames/map_tiles/regions/BR-NortheastStatesRecife-z6-25-33.json` | 补回原版地图 z6 的伊科、帕拉伊巴州、伯南布哥州、累西腓等。 |
| `dict/placenames/map_tiles/regions/BR-MatoGrossoCuiaba-z6-22-34.json` | 补回原版地图 z6 的马托格罗索州、库亚巴。 |
| `dict/placenames/map_tiles/regions/PE-BO-CL-LaPazBeni-z6-19-34-2x2.json` | 补回原版地图 z6 的马尔多纳多港、库斯科、阿雷基帕、拉巴斯等。 |
| `dict/placenames/map_tiles/regions/Pacific-TongaNiue-z6-0-35-2x1.json` | 补回原版地图 z6 的汤加、努库阿洛法、纽埃。 |
| `dict/placenames/map_tiles/regions/Kuwait-AlJahra-Ahmadi-z8-161-106-2x1.json` | 补回原版地图 z8 的杰赫拉、艾哈迈迪。 |
| `dict/placenames/map_tiles/regions/India-Odisha-Bhubaneswar-Puri-Brahmapur-z7-94-56-1x2.json` | 补回原版地图 z7 的布巴内什瓦尔、布里、布拉赫马普尔。 |
| `dict/placenames/map_tiles/regions/Turkey-Marmara-Pendik-Bursa-Tavsanli-z9-297-192-1x3.json` | 补回原版地图 z9 的彭迪克、迪洛瓦瑟、奇纳尔哲克、亚洛瓦等。 |
| `dict/placenames/map_tiles/regions/Iraq-Zakho-Duhok-Amedi-z9-316-199-2x1.json` | 补回原版地图 z9 的扎胡、新巴扎兰、祖马尔、巴蒂法等。 |
| `dict/placenames/map_tiles/regions/India-Assam-Meghalaya-Nagaland-Manipur-z7-96-54-2x1.json` | 补回原版地图 z7 的古瓦哈提、梅加拉亚邦、锡尔赫特专区、伊塔那加等。 |
| `dict/placenames/map_tiles/regions/India-Goa-Karnataka-AndhraPradesh-z7-90-58-3x1.json` | 补回原版地图 z7 的果阿邦、贝尔高姆、胡布利、卡纳塔克邦等。 |
| `dict/placenames/map_tiles/regions/JP9-KyotoLakeBiwa-z9-448-202.json` | 补回原版地图 z9 的福知山、丹波篠山、龟冈、米原等。 |
| `dict/placenames/map_tiles/regions/TH-Phaisali-z9-399-233.json` | 补回原版地图 z9 的廊磨、萨莫托、派沙利、普堆。 |
| `dict/placenames/map_tiles/regions/BJ8-DjougouParakouBenin-z8-129-121.json` | 补回原版地图 z8 的朱古、帕拉库。 |
| `dict/placenames/map_tiles/regions/UA9-Zaporizhzhia-Marhanets-Huliaipole-z9-305-178-3x1.json` | 补回原版地图 z9 的托马基夫卡、马尔哈涅茨、扎波罗热、库舒胡姆等。 |
| `dict/placenames/map_tiles/regions/UA9-Pershotravensk-Vasylkivka-Chaplyne-z9-307-177.json` | 补回原版地图 z9 的佩尔绍特拉文斯克、瓦西尔基夫卡、恰普利内。 |
| `dict/placenames/map_tiles/regions/UA-RU9-Markivka-Bilovodsk-Chertkovo-z9-312-174-2x2.json` | 补回原版地图 z9 的马尔基夫卡、坎捷米罗夫卡、切尔特科沃、比洛沃茨克等。 |
| `dict/placenames/map_tiles/regions/RU9-Tarasovsky-Glubokiy-z9-313-176.json` | 补回原版地图 z9 的塔拉索夫斯基、格卢博基。 |
| `dict/placenames/map_tiles/regions/MKBulgaria-ShtipBlagoevgradRazlogDevin-z9-287-190-4x1.json` | 补回原版地图 z9 的普罗比什蒂普、斯韦蒂尼科莱、什蒂普、拉多维什等。 |
| `dict/placenames/map_tiles/regions/SS9-Cueibet-z9-297-245.json` | 补回原版地图 z9 的奎贝特。 |
| `dict/placenames/map_tiles/regions/LY9-RahibatZararah-z9-272-208.json` | 补回原版地图 z9 的扎拉拉、拉希巴特。 |
| `dict/placenames/map_tiles/regions/DZ9-MsilaHammamDhalaa-z9-262-201.json` | 补回原版地图 z9 的布阿拉里季堡、哈马姆达拉阿、姆西拉。 |
| `dict/placenames/map_tiles/regions/CD8-MikenoGomaBukavu-z8-148-128.json` | 补回原版地图 z8 的米凯诺山、戈马、布卡武。 |
| `dict/placenames/map_tiles/regions/SO7-Galkayo-z7-80-61.json` | 补回原版地图 z7 的加勒卡约。 |
| `dict/placenames/map_tiles/regions/CD7-Kamina-z7-72-67.json` | 补回原版地图 z7 的卡米纳。 |
| `dict/placenames/map_tiles/regions/CA-BaffinBay-z5-9-6.json` | 补回原版地图 z5 的巴芬湾。 |
| `dict/placenames/map_tiles/regions/US-Sierra-BearCreekSpireTomHumphreysDarwinCloudripper-z10-174-397-1x2.json` | 补回原版地图 z10 的熊溪尖峰、汤姆山、汉弗莱斯山、达尔文山等。 |
| `dict/placenames/map_tiles/regions/US-GulfCoast-HattiesburgBiloxiMobilePensacolaDothan-z7-32-52-2x1.json` | 补回原版地图 z7 的哈蒂斯堡、比洛克西、莫比尔、彭萨科拉等。 |
| `dict/placenames/map_tiles/regions/US-PaducahEvansvilleClarksvilleJackson-z7-32-49-1x2.json` | 补回原版地图 z7 的埃文斯维尔、帕迪尤卡、克拉克斯维尔、杰克逊。 |
| `dict/placenames/map_tiles/regions/US-Texas-Denton-z8-58-102.json` | 补回原版地图 z8 的登顿。 |
| `dict/placenames/map_tiles/regions/UA9-Enerhodar-Vasylivka-Tokmak-z9-305-179-2x1.json` | 补回原版地图 z9 的埃涅尔戈达尔、第聂伯罗鲁德内、瓦西里夫卡、米哈伊利夫卡等。 |
| `dict/placenames/map_tiles/regions/PH9-SorsogonBiri-z9-432-237.json` | 补回原版地图 z9 的拉普拉普、索索贡市、布兰、伊罗辛等。 |
| `dict/placenames/map_tiles/regions/BF10-TougouriTaparkoBonsiega-z10-510-473.json` | 补回原版地图 z10 的图古里、塔帕尔科、邦谢加。 |
| `dict/placenames/map_tiles/regions/ML10-MadinaNionoDoura-z10-494-470.json` | 补回原版地图 z10 的马迪纳于佩5、尼奥诺索库拉、杜拉。 |
| `dict/placenames/map_tiles/regions/BF10-OuahigouyaKoumbri-z10-504-472.json` | 补回原版地图 z10 的尼尼吉、库姆布里、尤、纳米西吉马等。 |
| `dict/placenames/map_tiles/regions/US-Utah-NephiLevanScipioGunnisonHoldenFillmore-z9-96-194-1x2.json` | 补回原版地图 z9 的尼法、利万、西皮奥、冈尼森等。 |
| `dict/placenames/map_tiles/regions/CN-FushunJiubing-z9-432-190.json` | 补回原版地图 z9 的会元、章党、抚顺、救兵等。 |
| `dict/placenames/map_tiles/regions/CN-ShuozhouSharedPoint-z8-207-97.json` | 补回原版地图 z8 的跨片文字与符号衔接。 |
| `dict/placenames/map_tiles/regions/CN-LinfenHongtong-z9-414-200.json` | 补回原版地图 z9 的蒲县、洪洞、吴村、临汾。 |
| `dict/placenames/map_tiles/regions/VN-HungHoa-HoaBinh-z9-405-225.json` | 补回原版地图 z9 的兴化、和平市、枚州、满德。 |
| `dict/placenames/map_tiles/regions/LA-Kasi-Phonhong-z9-401-228.json` | 补回原版地图 z9 的卡西、万荣、东村、欣合等。 |
| `dict/placenames/map_tiles/regions/CN-KelanXingxianDashetou-z9-414-196.json` | 补回原版地图 z9 的岢岚、兴县、大蛇头。 |
| `dict/placenames/map_tiles/regions/CN-LvliangZhongyang-z9-414-198.json` | 补回原版地图 z9 的吕梁、信义、中阳、杜村。 |
| `dict/placenames/map_tiles/regions/CaspianSea-z8-163-95.json` | 补回原版地图 z8 的里海。 |
| `dict/placenames/map_tiles/regions/Turkey-Eregli-Ulukisla-Pozanti-z9-304-198-2x1.json` | 补回原版地图 z9 的埃雷利、哈尔卡珀纳尔、乌卢克什拉、波赞特等。 |
| `dict/placenames/map_tiles/regions/Turkey-Hilvan-Sanliurfa-Akcakale-z9-311-198-1x2.json` | 补回原版地图 z9 的希尔万、尚勒乌尔法、阿克恰卡莱。 |
| `dict/placenames/map_tiles/regions/Turkey-Nurdagi-Islahiye-Musabeyli-Kilis-z9-308-199.json` | 补回原版地图 z9 的努尔达厄、伊斯拉希耶、穆萨贝伊利、基利斯。 |
| `dict/placenames/map_tiles/regions/HURO-NyiregyhazaDebrecenOradeaArad-z8-143-89-1x2.json` | 补回原版地图 z8 的尼赖吉哈佐、德布勒森、奥拉迪亚、阿拉德。 |
| `dict/placenames/map_tiles/regions/IT-Sicily-NicosiaCapoOrlandoTaormina-z9-276-197-2x1.json` | 补回原版地图 z9 的卡波多尔兰多、圣阿加塔迪米利泰洛、尼科西亚、巴尔切洛纳波佐迪戈托等。 |
| `dict/placenames/map_tiles/regions/BY-BudaKashalyovaChacherskVetka-z9-299-167-2x1.json` | 补回原版地图 z9 的恰切尔斯克、布达卡沙廖瓦、巴尔沙维克、韦特卡。 |
| `dict/placenames/map_tiles/regions/BGTR-AsenovgradKardzhaliHaskovoEdirne-z9-291-190-3x1.json` | 补回原版地图 z9 的阿塞诺夫格勒、拉基、切佩拉雷、斯莫梁等。 |
| `dict/placenames/map_tiles/regions/VN-DongNai-BaRia-z10-816-480.json` | 补回原版地图 z10 的永安、油曳、隆庆、德才等。 |
| `dict/placenames/map_tiles/regions/VN-CanThanh-VungTau-z10-816-482.json` | 补回原版地图 z10 的芹盛、头顿。 |
| `dict/placenames/map_tiles/regions/UA9-Nikopol-Pokrov-Kamianka-z9-304-178-1x2.json` | 补回原版地图 z9 的波克罗夫、尼科波尔、卡缅卡-第聂伯罗夫斯卡、新沃龙佐夫卡等。 |
| `dict/placenames/map_tiles/regions/PH9-AntiqueIloilo-z9-429-240.json` | 补回原版地图 z9 的帕特农贡、圣何塞、米亚高、托比亚斯·福尼尔。 |
| `dict/placenames/map_tiles/regions/TH-LA-Paklay-PakChom-z9-400-229.json` | 补回原版地图 z9 的巴莱、巴宗。 |
| `dict/placenames/map_tiles/regions/CN-Jiangxi-Qianshan-Liangshan-Shanzhuang-Futian-z10-838-430.json` | 补回原版地图 z10 的钤山、良山、山庄、阜田。 |
| `dict/placenames/map_tiles/regions/VN-LacTanh-LaGi-z10-818-480.json` | 补回原版地图 z10 的乐性、新明、顺南、新义等。 |
| `dict/placenames/map_tiles/regions/BF-OuahigouyaGourcy-z9-252-236.json` | 补回原版地图 z9 的尼尼吉、瓦希古亚、基恩巴拉、古尔西等。 |
| `dict/placenames/map_tiles/regions/SO7-Bosaso-z7-81-59.json` | 补回原版地图 z7 的博萨索。 |
| `dict/placenames/map_tiles/regions/Niger-Diffa-z8-136-118-2x1.json` | 补回原版地图 z8 的迪法。 |
| `dict/placenames/map_tiles/regions/Cuba-HavanaArtemisaGuinesMatanzasColon-z8-69-111.json` | 补回原版地图 z8 的哈瓦那、阿特米萨、圭内斯、马坦萨斯等。 |
| `dict/placenames/map_tiles/regions/UA9-Ustynivka-KryvyiRih-z9-302-178-2x1.json` | 补回原版地图 z9 的乌斯蒂尼夫卡、卡赞卡、新布赫、克里沃罗格等。 |
| `dict/placenames/map_tiles/regions/TH-LA-Mekong-PakKhat-z9-402-229.json` | 补回原版地图 z9 的隆灿、巴卡、海。 |
| `dict/placenames/map_tiles/regions/TH-LA-Mekong-Borikham-Sakon-z9-403-229.json` | 补回原版地图 z9 的波里坎、北汕、巴卡汀、索披赛等。 |
| `dict/placenames/map_tiles/regions/VN-Hanoi-ThaiNguyen-NinhBinh-z9-406-224.json` | 补回原版地图 z9 的同心、亭奇、雄山、太原等。 |
| `dict/placenames/map_tiles/regions/TH-Phetchabun-z9-399-232.json` | 补回原版地图 z9 的塔蓬、碧差汶、泰东、东奎等。 |
| `dict/placenames/map_tiles/regions/UA9-Piatykhatky-ZhovtiVody-z9-302-177-3x1.json` | 补回原版地图 z9 的诺沃霍罗德卡、多林斯卡、若夫季沃德、皮亚季哈特基等。 |
| `dict/placenames/map_tiles/regions/EA-Victoria-MasakaBukobaBiharamulo-z8-150-128.json` | 补回原版地图 z8 的马萨卡、布科巴、比哈拉穆洛。 |
| `dict/placenames/map_tiles/regions/MR-GuerouKiffa-z9-239-231.json` | 补回原版地图 z9 的盖鲁、基法。 |
| `dict/placenames/map_tiles/regions/G150-CONTEXT-Ndalya-z9-298-254.json` | 补回原版地图 z9 的恩达利亚。 |
| `dict/placenames/map_tiles/regions/G150-CONTEXT-WestCompound-z9-298-255.json` | 补回原版地图 z9 的诺比利、卡塞塞、布韦拉、玛格丽塔峰。 |
| `dict/placenames/map_tiles/regions/G150-CONTEXT-EastNine-z9-299-254.json` | 补回原版地图 z9 的基扬瓦利、恩托罗科、卡加迪、伊班达等。 |
| `dict/placenames/map_tiles/regions/Caribbean-SVG-z9-168-237-2-1.json` | 补回原版地图 z9 的金斯敦、伊丽莎白港、多弗斯、阿什顿。 |
| `dict/placenames/map_tiles/regions/BR6-AraguaiaPiaui-z6-23-33.json` | 补回原版地图 z6 的阿拉瓜亚河、纳雄耐尔港、皮奥伊州、皮库斯等。 |
| `dict/placenames/map_tiles/regions/CN-Jiangxi-Anfu-Zhujiang-Tongping-JianCounty-z10-838-431.json` | 补回原版地图 z10 的安福县、竹江、桐坪、吉安县。 |
| `dict/placenames/map_tiles/regions/CN-InnerMongoliaShanxi-JungarPianguan-z9-414-194.json` | 补回原版地图 z9 的准格尔、窑沟、水泉、偏关等。 |
| `dict/placenames/map_tiles/regions/Utah-LoganRichmondBrigham-z9-96-190.json` | 补回原版地图 z9 的里士满、特里蒙顿、洛根、海勒姆等。 |
| `dict/placenames/map_tiles/regions/Utah-NorthOgdenOgden-z9-96-191.json` | 补回原版地图 z9 的北奥格登、奥格登。 |
| `dict/placenames/map_tiles/regions/Tunisia-Bizerte-z9-269-198.json` | 补回原版地图 z9 的比塞大、埃尔阿利亚。 |
| `dict/placenames/map_tiles/regions/Tunisia-Tunis-z9-270-199.json` | 补回原版地图 z9 的卡拉特安达卢斯、阿里亚纳、迦太基、突尼斯。 |
| `dict/placenames/map_tiles/regions/TH-LA-Mekong-Loei-z9-400-230.json` | 补回原版地图 z9 的沙那坎、根陶、塔利、黎等。 |
| `dict/placenames/map_tiles/regions/TH-LA-Mekong-Vientiane-Udon-z9-401-230.json` | 补回原版地图 z9 的那銮、万象、塔博、南颂等。 |
| `dict/placenames/map_tiles/regions/US-Oklahoma-EnidStillwaterOklahomaCityNormanLawton-z8-58-100.json` | 补回原版地图 z8 的伊尼德、斯蒂尔沃特、奥克拉荷马市、诺曼等。 |
| `dict/placenames/map_tiles/regions/US-Oklahoma-Tulsa-z8-59-100.json` | 补回原版地图 z8 的图尔萨。 |
| `dict/placenames/map_tiles/regions/NW-Luzon-z10-854-463.json` | 补回原版地图 z10 的塔古丁、卢纳、巴老安、巴克诺坦。 |
| `dict/placenames/map_tiles/regions/NW-Luzon-z10-854-464.json` | 补回原版地图 z10 的圣费尔南多、包昂、阿古、卡潘甘等。 |
| `dict/placenames/map_tiles/regions/DZ-Cherchell-z9-259-199.json` | 补回原版地图 z9 的舍尔沙勒。 |
| `dict/placenames/map_tiles/regions/DZ-BlidaMedeaCommunes-z9-259-200.json` | 补回原版地图 z9 的哈朱特、卜利达、海米斯米利亚纳、詹德尔等。 |
| `dict/placenames/map_tiles/regions/Turkey-Fethiye-z9-297-199.json` | 补回原版地图 z9 的恰梅利、阿尔滕亚伊拉、赛迪凯梅尔、费特希耶。 |
| `dict/placenames/map_tiles/regions/TH-KamphaengPhet-Phichit-z9-397-232.json` | 补回原版地图 z9 的兰甲伯、甘烹碧、赛岸、空坤等。 |
| `dict/placenames/map_tiles/regions/CN-Liaoning-z10-862-384.json` | 补回原版地图 z10 的八会、隆昌。 |
| `dict/placenames/map_tiles/regions/CN-Liaoning-z10-862-385.json` | 补回原版地图 z10 的牧牛、三家子、偏岭。 |
| `dict/placenames/map_tiles/regions/CN-Liaoning-z10-862-387.json` | 补回原版地图 z10 的龙潭、新甸、花院。 |
| `dict/placenames/map_tiles/regions/Turkey-Istanbul-Cekmekoy-z9-297-191.json` | 补回原版地图 z9 的切克梅柯伊。 |
| `dict/placenames/map_tiles/regions/Bangladesh-Tripura-Mizoram-Monywa-z7-96-55-2x1.json` | 补回原版地图 z7 的达卡、特里普拉邦、艾藻尔、库米拉等。 |
| `dict/placenames/map_tiles/regions/TH-LA-Thongmixai-NamPat-z9-399-229.json` | 补回原版地图 z9 的通米赛、纳诺、班科、法塔等。 |
| `dict/placenames/map_tiles/regions/JP9-FukuiNagahama-z9-449-201.json` | 补回原版地图 z9 的越前、池田、敦贺、长滨。 |
| `dict/placenames/map_tiles/regions/CF-AkpiaMboloAlindao-z9-286-248.json` | 补回原版地图 z9 的姆博洛、阿克皮亚、阿林道。 |
| `dict/placenames/map_tiles/regions/SS-AbyeiAbiemnhomMayom-z9-296-242.json` | 补回原版地图 z9 的阿卜耶伊、阿比恩诺姆、马约姆。 |
| `dict/placenames/map_tiles/regions/Pangasinan-z10-852-464.json` | 补回原版地图 z10 的博利瑙。 |
| `dict/placenames/map_tiles/regions/Pangasinan-z10-853-465.json` | 补回原版地图 z10 的安达、阿拉米诺斯、林加延、达古潘等。 |
| `dict/placenames/map_tiles/regions/UA8-Mariupol-z8-154-89.json` | 补回原版地图 z8 的马里乌波尔。 |
| `dict/placenames/map_tiles/regions/UA8-Melitopol-Berdiansk-Azov-z8-153-90-2x1.json` | 补回原版地图 z8 的梅利托波尔、别尔江斯克、亚速海。 |
| `dict/placenames/map_tiles/regions/CN-Shanxi-GuxianLan-z9-414-197.json` | 补回原版地图 z9 的固贤、岚县、王狮、积翠等。 |
| `dict/placenames/map_tiles/regions/UA9-Kamianske-Dnipro-z9-305-176-1x2.json` | 补回原版地图 z9 的彼得里基夫卡、奥布希夫卡、卡姆扬斯凯、第聂伯罗等。 |
| `dict/placenames/map_tiles/regions/RU9-Oryol-Mtsensk-z9-307-166-2x1.json` | 补回原版地图 z9 的奥廖尔、姆岑斯克。 |
| `dict/placenames/map_tiles/regions/RU9-Zalegoshch-Pokrovskoye-z9-308-167.json` | 补回原版地图 z9 的扎列戈希、波克罗夫斯科耶。 |
| `dict/placenames/map_tiles/regions/VE8-CalabozoZaraza-z8-80-121.json` | 补回原版地图 z8 的卡拉沃索、丘瓜拉马斯、萨拉萨。 |
| `dict/placenames/map_tiles/regions/LY-RasAjdir-Zulten-AlAssa-z9-272-205.json` | 补回原版地图 z9 的拉斯杰迪尔、祖尔滕、阿萨。 |
| `dict/placenames/map_tiles/regions/BY-Zhlobin-Svietlahorsk-z9-298-167.json` | 补回原版地图 z9 的日洛宾、斯特雷申、斯韦特洛戈尔斯克、萨斯诺维博尔。 |
| `dict/placenames/map_tiles/regions/ML-OuolonSanTominian-z10-498-473.json` | 补回原版地图 z10 的乌奥隆、桑、托米尼昂。 |
| `dict/placenames/map_tiles/regions/US-Utah-SaltLakeBountifulSouthJordanTooele-z9-96-192.json` | 补回原版地图 z9 的邦蒂富尔、盐湖城、南乔丹、图埃勒。 |
| `dict/placenames/map_tiles/regions/Kattegat-Denmark-Sweden-z7-67-39-2x1.json` | 补回原版地图 z7 的奥尔堡、奥胡斯、卡特加特海峡、哈姆斯塔德等。 |
| `dict/placenames/map_tiles/regions/LA-VN-Xaichamphone-HaTinh-z9-405-229.json` | 补回原版地图 z9 的赛占蓬、河新、武光镇、拉绍等。 |
| `dict/placenames/map_tiles/regions/BY-Asipovichy-StaryyaDarohi-Hlusk-z9-296-166-1x2.json` | 补回原版地图 z9 的奥西波维奇、旧多罗吉、格卢斯克。 |
| `dict/placenames/map_tiles/regions/Iran-Zagros-z9-321-203-1x2.json` | 补回原版地图 z9 的伊兹格列、卡莱耶米拉巴德、萨尔波勒扎哈卜、克伦德加尔卜等。 |
| `dict/placenames/map_tiles/regions/BR-MatoGrosso-AltaFloresta-Colider-z6-22-33.json` | 补回原版地图 z6 的阿尔塔弗洛雷斯塔、科利德尔、巴西。 |
| `dict/placenames/map_tiles/regions/CN-InnerMongolia-TogtohQingshuihe-z9-414-193.json` | 补回原版地图 z9 的托克托、新营子、喇嘛湾、大路等。 |
| `dict/placenames/map_tiles/regions/JP8-SouthKyushu-z8-220-104.json` | 补回原版地图 z8 的鹿儿岛、宫崎、鹿屋。 |
| `dict/placenames/map_tiles/regions/US-WestTexas-Pecos-Odessa-FortStockton-z8-54-104.json` | 补回原版地图 z8 的佩科斯、敖德萨、斯托克顿堡。 |
| `dict/placenames/map_tiles/regions/PH9-PalawanAgutayaCuyo-z9-427-240.json` | 补回原版地图 z9 的阿古塔亚、库约。 |
| `dict/placenames/map_tiles/regions/CN-Liaoning-HadabeiXiuyan-z10-862-386.json` | 补回原版地图 z10 的哈达碑、岫岩。 |
| `dict/placenames/map_tiles/regions/TH9-SiRacha-Pattaya-BanChang-Sattahip-z9-399-237.json` | 补回原版地图 z9 的是拉差、芭堤雅、班昌、梭桃邑。 |
| `dict/placenames/map_tiles/regions/VN-SouthCoast-z9-406-240.json` | 补回原版地图 z9 的巴域、建祥、鸟栖、新盛等。 |
| `dict/placenames/map_tiles/regions/VN-SouthCoast-VungTau-z9-408-241.json` | 补回原版地图 z9 的巴地、头顿。 |
| `dict/placenames/map_tiles/regions/Philippines-WestMindanao-z8-216-122.json` | 补回原版地图 z8 的奥三棉示、伊利甘市、哥打巴托市。 |
| `dict/placenames/map_tiles/regions/CD-KasaiOriental-Gandajika-MweneDitu-z7-72-66.json` | 补回原版地图 z7 的东开赛省、甘达吉卡、姆韦内迪图。 |
| `dict/placenames/map_tiles/regions/RU8-Dagestan-Makhachkala-Buynaksk-z8-161-94-2x1.json` | 补回原版地图 z8 的马哈奇卡拉、布伊纳克斯克。 |
| `dict/placenames/map_tiles/regions/KH-VN-KampotBorder-z9-404-240.json` | 补回原版地图 z9 的速富、磅地铭、贡布、边佐等。 |
| `dict/placenames/map_tiles/regions/KH-VN-KepKienLuong-z9-404-241.json` | 补回原版地图 z9 的白马、坚良。 |
| `dict/placenames/map_tiles/regions/Turkey-Denizli-Tavas-z9-297-198.json` | 补回原版地图 z9 的塔瓦斯、贝亚阿奇、阿哲帕亚姆。 |
| `dict/placenames/map_tiles/regions/Turkey-Denizli-z9-297-197.json` | 补回原版地图 z9 的居内伊、恰尔、萨拉伊柯伊、代尼兹利等。 |
| `dict/placenames/map_tiles/regions/CN-Shanxi-JiaokouShuangchiFenxi-z9-414-199.json` | 补回原版地图 z9 的交口、双池、汾西。 |
| `dict/placenames/map_tiles/regions/PH9-NorthLuzon-z9-427-230.json` | 补回原版地图 z9 的卡布高、圣胡安、美岸、邦贵等。 |
| `dict/placenames/map_tiles/regions/Egypt-NorthSinai-AlArish-z10-608-418.json` | 补回原版地图 z10 的阿里什。 |
| `dict/placenames/map_tiles/regions/Turkey-Usak-Esme-Ulubey-z9-297-196.json` | 补回原版地图 z9 的乌沙克、埃什梅、乌卢贝伊。 |
| `dict/placenames/map_tiles/regions/US-BoulderCity-z10-185-402.json` | 补回原版地图 z10 的博尔德城。 |
| `dict/placenames/map_tiles/regions/KH-WestCoast-z9-403-240.json` | 补回原版地图 z9 的盐田、甘榜塞拉、普雷诺、西哈努克市。 |
| `dict/placenames/map_tiles/regions/PH10-NorthLuzon-z10-856-462-4x2.json` | 补回原版地图 z10 的马约尧、阿吉纳尔多、圣马里亚诺、阿方索利斯塔等。 |
| `dict/placenames/map_tiles/regions/SK-Medzilaborce-Snina-z9-287-175.json` | 补回原版地图 z9 的梅济拉博尔采、斯尼纳。 |
| `dict/placenames/map_tiles/regions/CV-Praia-z7-55-58.json` | 补回原版地图 z7 的普拉亚。 |
| `dict/placenames/map_tiles/regions/KerichoNakuru-z8-153-128.json` | 补回原版地图 z8 的凯里乔。 |
| `dict/placenames/map_tiles/regions/India-Telangana-KarimnagarKhammam-z7-92-57.json` | 补回原版地图 z7 的格里姆讷格尔、卡姆马姆。 |
| `dict/placenames/map_tiles/regions/PR7-Bicol-z10-864-471-1x2.json` | 补回原版地图 z10 的卡拉莫兰、卡拉莫安、圣安德烈斯。 |
| `dict/placenames/map_tiles/regions/PH-Catanduanes-z10-865-471-1x3.json` | 补回原版地图 z10 的潘丹、巴加马诺克、维加、巴托等。 |
| `dict/placenames/map_tiles/regions/US8-WichitaJoplin-z8-58-99.json` | 补回原版地图 z8 的威奇托、庞卡城、巴特尔斯维尔、科菲维尔等。 |
| `dict/placenames/map_tiles/regions/KH-KiriSakor-z9-402-240.json` | 补回原版地图 z9 的基里色空。 |
| `dict/placenames/map_tiles/regions/RU8-Dimitrovgrad-Tolyatti-z8-163-81-1-2.json` | 补回原版地图 z8 的季米特洛夫格勒、陶里亚蒂。 |
| `dict/placenames/map_tiles/regions/US-Utah-Lehi-EagleMountain-z9-96-193.json` | 补回原版地图 z9 的李海、老鹰山。 |
| `dict/placenames/map_tiles/regions/Negev-Yerucham-z10-611-419.json` | 补回原版地图 z10 的耶鲁哈姆。 |
| `dict/placenames/map_tiles/regions/Lubutu-z7-73-64.json` | 补回原版地图 z7 的卢布图。 |
| `dict/placenames/map_tiles/regions/Agazragane-z10-521-465.json` | 补回原版地图 z10 的阿加兹拉甘。 |
| `dict/placenames/map_tiles/regions/VU-PortVila-z6-61-35-2x1.json` | 补回原版地图 z6 的维拉港。 |
| `dict/placenames/map_tiles/regions/US-FortWorth-z8-58-103.json` | 补回原版地图 z8 的沃思堡。 |
| `dict/placenames/map_tiles/regions/Tibet-Nagqu-z7-96-52.json` | 补回原版地图 z7 的那曲镇、西藏自治区。 |
| `dict/placenames/map_tiles/regions/US-NewYork-NewJersey-Delaware-z7-37-48.json` | 补回原版地图 z7 的纽约市、新泽西州、威尔明顿、大西洋城。 |
| `dict/placenames/map_tiles/regions/BENGAL-C.json` | 原版地图z8的中文名称补回：杰索尔、库尔纳、加尔各答、梅迪尼布尔等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/BENGAL-W.json` | 原版地图z8的中文名称补回：丹巴德、阿散索尔、巴加尔普尔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/BENGAL-E.json` | 原版地图z8的中文名称补回：迈门辛、达卡、阿加尔塔拉、库米拉；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/BENGAL-F.json` | 原版地图z8的中文名称补回：艾藻尔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/Oman-Sohar-z8-168-110.json` | 原版地图z8的中文名称补回：苏哈尔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/India-Nagpur-Chhattisgarh-z7-92-56-2x1.json` | 原版地图z7的中文名称补回：恰蒂斯加尔邦、比莱、那格浦尔、钱德拉布尔等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/TL4-MODEL-R1.json` | 原版地图z9的中文名称补回：差德拉甘、丹赛、那空泰、隆告；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S52-C2-Complete6-z9-396-230-3-2.json` | 原版地图z9的中文名称补回：宋加洛、披猜、素可泰、彭世洛等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/US-KilleenTempleWaco-z8-58-104-2x1.json` | 原版地图z8的中文名称补回：基林、坦普尔、韦科；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/Lodja-z7-72-65.json` | 原版地图z7的中文名称补回：洛贾；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/Zambezi-z7-72-68.json` | 原版地图z7的中文名称补回：赞比希；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/US-CentralIllinois-z7-32-48.json` | 原版地图z7的中文名称补回：皮奥里亚、布卢明顿、伊利诺伊州、斯普林菲尔德等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/RU8-Samara-Novokuybyshevsk-z8-163-83.json` | 原版地图z8的中文名称补回：萨马拉、新古比雪夫斯克；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/PH9-PalawanTaytayDumaranAraceli-z9-425-240.json` | 原版地图z9的中文名称补回：泰泰、杜马兰、阿拉塞利；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/Rossosh-z9-312-173.json` | 原版地图z9的中文名称补回：罗索希；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/IR8-Arak-z8-163-102.json` | 原版地图z8的中文名称补回：阿拉克；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/MX-Tampico-z7-29-55.json` | 原版地图z7的中文名称补回：坦皮科；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/US-Richfield-z9-96-196.json` | 原版地图z9的中文名称补回：里奇菲尔德；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/US-Flagstaff-z9-97-202.json` | 原版地图z9的中文名称补回：弗拉格斯塔夫；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/A7-Somalia-BeledweyneJowharMogadishu-z7-80-62.json` | 原版地图z7的中文名称补回：贝莱德文、乔哈尔、摩加迪沙；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/A7-Boutilimit-z8-117-115.json` | 原版地图z8的中文名称补回：布提利米特；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SkopjeVeles-z9-286-190.json` | 原版地图z9的中文名称补回：斯科普里、韦莱斯；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JP9-Wakayama-z9-448-204.json` | 原版地图z9的中文名称补回：和歌山、高野、御坊、田边；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/Amantea-z9-278-195.json` | 原版地图z9的中文名称补回：阿曼泰亚；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JP9-Ine-Wakasa-z9-448-201.json` | 原版地图z9的中文名称补回：伊根、高滨、舞鹤、小滨；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/Salalah-z7-83-57.json` | 原版地图z7的中文名称补回：塞拉莱；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/PrinceRupert-z6-8-20.json` | 原版地图z6的中文名称补回：鲁珀特王子港；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AM-Recovered-SanAngelo-z8-56-104.json` | 原版地图z8的中文名称补回：圣安吉洛；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AM-Recovered-Paramaribo-z7-44-61.json` | 原版地图z7的中文名称补回：帕拉马里博；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SICILY-East-z9-277-198.json` | 原版地图z9的中文名称补回：阿德拉诺、阿奇雷亚莱、卡塔尼亚、伦蒂尼等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/GRD-Grenada-z9-168-238.json` | 原版地图z9的中文名称补回：希尔斯伯勒、圣乔治；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/Sanya-Tianya-Jiyang-z10-823-459.json` | 原版地图z10的中文名称补回：天涯区、吉阳区、三亚；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/WEM8-Spain-z8-127-95-3-1.json` | 原版地图z8的中文名称补回：萨拉戈萨、塔拉戈纳、巴塞罗那、马塔罗；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/Baocheng-North-Sanya-z10-823-458.json` | 原版地图z10的中文名称补回：保城；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S52-C4-Original5-z9-435-189-2-2.json` | 原版地图z9的中文名称补回：桦树、闹枝、花山、临江等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S52-C4-Original5-z9-437-190-1-1.json` | 原版地图z9的中文名称补回：漫江；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S52-C4-Base7-z9-433-190-2-1.json` | 原版地图z9的中文名称补回：红升、红庙子、通化、榆树等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S52-C4-Base7-z9-434-189-1-1.json` | 原版地图z9的中文名称补回：向阳、曙光、草市、柳河等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S52-C4-Base7-z9-435-188-3-1.json` | 原版地图z9的中文名称补回：松山、赤松、公吉、桦甸等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S52-C4-Base7-z9-437-189-1-1.json` | 原版地图z9的中文名称补回：露水河、泉阳、抚松；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G232-Korea-z10-880-399-1x4.json` | 原版地图z10的中文名称补回：箕城面、厚浦面、虎尾串面、迎日湾等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S20-Haldia-continuation-z7-95-56.json` | 原版地图z7的中文名称补回：跨片文字与符号衔接；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G157-z9-308-176-complete2.json` | 原版地图z9的中文名称补回：比洛泽尔斯凯、多布罗皮利亚；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G160-z9-311-176-complete5.json` | 原版地图z9的中文名称补回：夏斯季亚、济莫希里亚、卢甘斯克、比莱等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/B7-C75-z8-143-88-1-1.json` | 原版地图z8的中文名称补回：科希策；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/B7-C126-z9-263-200-1-1.json` | 原版地图z9的中文名称补回：达尔吉纳、赫拉塔、布加、塞提夫等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/B7-C162-z9-312-167-1-3.json` | 原版地图z9的中文名称补回：多布罗耶、利佩茨克、格里亚济、乌斯曼等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/B7-C116-z9-88-191-1-1.json` | 原版地图z9的中文名称补回：跨片文字与符号衔接；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/E4-PROPOSED-G10-PERU-z6-17-33-2-1.json` | 原版地图z6的中文名称补回：秘鲁、帕卡斯马约、特鲁希略、普卡尔帕等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/E4-PROPOSED-G11-NUNATSIAVUT-z6-20-19-2-1.json` | 原版地图z6的中文名称补回：努纳齐亚武特、内恩；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/E4-PROPOSED-G58-GUINEA-z8-118-121-1-2.json` | 原版地图z8的中文名称补回：科纳克里、弗里敦；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/WST3-PacificSpill-z5-5-16.json` | 原版地图z5的中文名称补回：跨片文字与符号衔接；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/D4-G33-z7-54-49-2-1.json` | 原版地图z7的中文名称补回：英雄港、蓬塔德尔加达；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/C4-G139-North-z5-9-18-1-1.json` | 原版地图z5的中文名称补回：安托法加斯塔、智利；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/C4-G139-South-z5-9-19-3-1.json` | 原版地图z5的中文名称补回：门多萨、瓦尔帕莱索、阿根廷、康塞普西翁等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/C4-G144-z6-52-35-1-2.json` | 原版地图z6的中文名称补回：卡拉萨、昂斯洛、埃克斯茅斯、卡那封；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/C4-G005-z9-285-206-3-1.json` | 原版地图z9的中文名称补回：卡尔萨、库巴、贝达、巴塔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/C4-G017-z9-294-208-1-2.json` | 原版地图z9的中文名称补回：马特鲁港；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/WEM8-WestMaghreb-z8-127-99-1-3.json` | 原版地图z8的中文名称补回：卡塔赫纳、瓦赫兰、西迪贝勒阿巴斯、特莱姆森；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/WEM8-AinSefraBeniOunif-z8-127-103-1-1.json` | 原版地图z8的中文名称补回：艾因塞夫拉、贝尼乌尼夫；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/WEM8-HassiMessaoud-z8-132-104-1-1.json` | 原版地图z8的中文名称补回：哈西迈萨乌德；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/N8-PREFIX-z9-415-195.json` | 原版地图z9的中文名称补回：八角、贺职、义井、宁武等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/N8-PREFIX-z9-415-198.json` | 原版地图z9的中文名称补回：清徐、会立、交城、文水等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/N8-PREFIX-z9-423-191.json` | 原版地图z9的中文名称补回：偏坡营、七家、安州、中关等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EI08-PROPOSAL-R1-z7-100-65-3-1.json` | 原版地图z7的中文名称补回：南苏门答腊省、楠榜省、班达楠榜、明古鲁等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EI08-PROPOSAL-R2-z7-101-66-5-1.json` | 原版地图z7的中文名称补回：万丹省、雅加达、茂物、井里汶等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/IDS2-PROPOSAL-R1-z7-104-67-2-1.json` | 原版地图z7的中文名称补回：登巴萨、西努沙登加拉；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/MAIN-NK-z9-436-191-new5.json` | 原版地图z9的中文名称补回：罗坦劳动者区、慈城、和坪、萨姆波劳动者区等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G155-CN-z9-305-199-1x1.json` | 原版地图z9的中文名称补回：恰姆勒亚伊拉、塔尔苏斯、梅尔辛；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G198-Yalgo-z10-511-472-1x1.json` | 原版地图z10的中文名称补回：雅尔戈；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G22-Vanuatu-z6-61-34.json` | 原版地图z6的中文名称补回：瓦努阿图；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G150-ORIGINAL-Manguredjipa-z9-296-255.json` | 原版地图z9的中文名称补回：曼古雷吉帕；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G150-SUPPORT-SixNormals-z9-297-255.json` | 原版地图z9的中文名称补回：曼吉纳、宾戈、阿洛亚、贝尼等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/D4-G68-z8-134-122-2-1.json` | 原版地图z8的中文名称补回：武卡里、马库尔迪；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/D4-G123-upper-z9-244-236-1-1.json` | 原版地图z9的中文名称补回：迪迪耶尼、科洛卡尼、吉霍约；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/D4-G123-lower-z9-243-237-2-1.json` | 原版地图z9的中文名称补回：达班、托罗多、卡蒂、巴马科等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/NC4-MODEL-R1.json` | 原版地图z9的中文名称补回：朝阳、沈家台、东大屯、羊山等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G172-CN-z9-409-223-1x1.json` | 原版地图z9的中文名称补回：上思县、华兰、那陈、贵台；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JF2-MODEL-R431.json` | 原版地图z10的中文名称补回：吉水、丁江、吉州区、吉安；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G179-CN-z9-416-215-1x1.json` | 原版地图z9的中文名称补回：白石、衡山、杨桥、石鼓；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G177-CN-z9-415-218-1x2.json` | 原版地图z9的中文名称补回：陶岭、禾亭、宁远、嘉禾等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G54-ElPaso-z8-52-104.json` | 原版地图z8的中文名称补回：艾尔帕索；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G31-LabradorCity-z7-40-41.json` | 原版地图z7的中文名称补回：拉布拉多市；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G181-CN-z9-420-200-2x1.json` | 原版地图z9的中文名称补回：冠县、聊城、沙镇、莘县等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/A5-G47-z7-99-64-2-1.json` | 原版地图z7的中文名称补回：武吉丁宜、沙哇伦多、巴东、占碑等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/A5-G70-z8-135-128-1-1.json` | 原版地图z8的中文名称补回：比丰、兰巴雷内；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/A5-G147-z9-295-239-2-1.json` | 原版地图z9的中文名称补回：富拉、巴巴努萨；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/B7-C115-z9-87-205-1-1.json` | 原版地图z9的中文名称补回：阿瓦隆；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/WST3-FortSevern-z6-16-19.json` | 原版地图z6的中文名称补回：塞文堡；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/WST3-FortBraggSpill-z9-79-194.json` | 原版地图z9的中文名称补回：跨片文字与符号衔接；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/OTHER-Zama-z10-520-470.json` | 原版地图z10的中文名称补回：跨片文字与符号衔接；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/OTHER-YoshkarOla-z8-162-78.json` | 原版地图z8的中文名称补回：跨片文字与符号衔接；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/OTHER-AlQurayyat-z10-618-418.json` | 原版地图z10的中文名称补回：跨片文字与符号衔接；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/OTHER-Jamame-Kismayo-z8-158-128.json` | 原版地图z8的中文名称补回：基斯马尤；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/HH5-Heilong-z8-216-89-1x2.json` | 原版地图z8的中文名称补回：齐齐哈尔、大庆；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/HHCL-East-z8-217-90-1x2.json` | 原版地图z8的中文名称补回：青冈镇、安达、兰西镇、肇东等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/HHCL-HarbinShangzhi-z8-218-91-2x1.json` | 原版地图z8的中文名称补回：哈尔滨、尚志；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G79-EnNahud-z8-148-118-1-2.json` | 原版地图z8的中文名称补回：恩纳胡德；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G67-Jos-Bauchi-z8-134-120-2-1.json` | 原版地图z8的中文名称补回：乔斯、包奇；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/MAIN-NK-z9-435-191-closed1.json` | 原版地图z9的中文名称补回：满浦、集安；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JFF-R11-N-z10-838-438-2x2.json` | 原版地图z10的中文名称补回：关西、定南、天九、长塘等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JFF-R11-S-z10-837-440-4x1.json` | 原版地图z10的中文名称补回：优胜、和平、车田、古寨等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JFF-R11-R-z10-843-430-1x2.json` | 原版地图z10的中文名称补回：上唐、南丰、桑田、茅排等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/DL11-Column-z10-863-383-1-6.json` | 原版地图z10的中文名称补回：安平、弓长岭、南芬、甜水等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/DL11-Top-z10-864-383-5-1.json` | 原版地图z10的中文名称补回：碱厂、八里甸子、东营坊、普乐堡等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/MAIN-LiaoningNorth-z10-863-382-5-1.json` | 原版地图z10的中文名称补回：铧子、溪湖、本溪、高官等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/NC4-MODEL-R2.json` | 原版地图z9的中文名称补回：清河城、本溪满族自治县、本溪、南芬等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EUE12-R1-Liepaja-z8-142-79-2x1.json` | 原版地图z8的中文名称补回：利耶帕亚；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EUE12-R2-KlaipedaSovetsk-z8-143-80.json` | 原版地图z8的中文名称补回：克莱佩达、苏维埃茨克；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EUE12-R3-Rzeszow-z8-143-86.json` | 原版地图z8的中文名称补回：热舒夫；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EUE12-R4-BaiaMare-z8-144-89.json` | 原版地图z8的中文名称补回：巴亚马雷；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EUE12-R5-Bulgaria-z8-146-93-1x2.json` | 原版地图z8的中文名称补回：大特尔诺沃、斯利文、旧扎戈拉、鲁塞；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EUE12-R6-AdriaticUpper-z8-140-94-3x1.json` | 原版地图z8的中文名称补回：佩奇、普里兹伦、斯库台、波德戈里察等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EUE12-R7-Albania-z8-141-95-2x1.json` | 原版地图z8的中文名称补回：都拉斯、地拉那、爱尔巴桑；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EI15-PROPOSAL-R1-z7-103-64-11-1.json` | 原版地图z7的中文名称补回：中加里曼丹省、帕朗卡拉亚、沙马林达、巴厘巴板等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EI15-PROPOSAL-R2-z7-104-65-3-1.json` | 原版地图z7的中文名称补回：南加里曼丹省、望加锡海峡、马辰、帕洛波等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/EI15-PROPOSAL-R3-z7-111-65-1-1.json` | 原版地图z7的中文名称补回：西巴布亚省；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/HH5-Hulun-z7-105-43-3x1.json` | 原版地图z7的中文名称补回：博尔贾、克拉斯诺卡缅斯克、满洲里、海拉尔区等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/NEW3-G9-Inukjuak-z6-18-19.json` | 原版地图z6的中文名称补回：伊努克爪克；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/NEW3-G15-Surgut-z6-45-18.json` | 原版地图z6的中文名称补回：苏尔古特；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/NEW3-G16-Lensk-z6-52-18.json` | 原版地图z6的中文名称补回：连斯克；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SRC62-Barishal1-z8-192-111.json` | 原版地图z8的中文名称补回：巴里萨尔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G26-Churchill-z7-30-38.json` | 原版地图z7的中文名称补回：丘吉尔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G4-ArcticBay-z5-8-6.json` | 原版地图z5的中文名称补回：北极湾；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G23-Ketchikan-PrinceRupert-z7-17-40-1x2.json` | 原版地图z7的中文名称补回：凯奇坎、鲁珀特王子港；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G52-Nogales-Cananea-AguaPrieta-z8-49-104-2x1.json` | 原版地图z8的中文名称补回：诺加莱斯、卡纳内阿、阿瓜普列塔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AU4-z6-55-35-4-1.json` | 原版地图z6的中文名称补回：滕南特克里克、伊萨山、克朗克里、卡伦巴等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/WestAfrica-G64-z8-127-121-2x1.json` | 原版地图z8的中文名称补回：塔马利、延迪、索科德；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SRC62-Oman-z8-168-111.json` | 原版地图z8的中文名称补回：伊卜里、尼兹瓦；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G24-Fort-Nelson-z7-20-37-1x2.json` | 原版地图z7的中文名称补回：纳尔逊堡；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/ALG-PROPOSED-129-z9-264-199-1-1.json` | 原版地图z9的中文名称补回：吉杰勒、塔海尔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/ALG-PROPOSED-130-z9-266-199-1-1.json` | 原版地图z9的中文名称补回：拜拉哈勒、阿扎巴；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/ALG-PROPOSED-131-z9-268-199-1-1.json` | 原版地图z9的中文名称补回：塔巴尔卡、艾因德拉罕；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JFF-CLOSED-A-z10-838-435-2x2.json` | 原版地图z10的中文名称补回：南塘、江口、储潭、赣县等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JFF5-MODEL-R3.json` | 原版地图z10的中文名称补回：雁洋、梅州、长沙；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JFF-CLOSED-C-z10-842-441-3x1.json` | 原版地图z10的中文名称补回：龙岗、黄金、砂田、潭江等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G142-CN-z9-293-239-1x1.json` | 原版地图z9的中文名称补回：瓦德比拉勒、埃代因、贾布罗纳；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G142-CN-z10-587-478-1x1.json` | 原版地图z10的中文名称补回：贾布罗纳、埃勒萨哈卜、巴迪、瓦德比拉勒；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G229-KR-z10-871-403.json` | 原版地图z10的中文名称补回：猬岛面、心元面；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G69-CAND-A-z8-134-124-1-3.json` | 原版z8完整词形的中文补回：卡拉巴尔、昆巴、喀麦隆山、林贝等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G69-CAND-B-z8-135-125-1-2.json` | 原版z8完整词形的中文补回：埃代阿、埃塞卡、克里比；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G69-CAND-C-z8-132-124-2-1.json` | 原版z8完整词形的中文补回：瓦里、阿巴、哈科特港、奥波博；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G122-WestNorth-z9-238-233-2x2.json` | 原版z9完整词形的中文补回：坎科萨、布安泽、达福尔、乌尔德延盖等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G122-WestSouth-z9-239-235-3x1.json` | 原版z9完整词形的中文补回：安比代迪右岸、马里图马尼亚、古梅拉、巴纳亚等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G122-WestMiddle-z9-240-234-2x1.json` | 原版z9完整词形的中文补回：科迪耶、比达吉、迪亚耶库拉、亚吉内等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G136-full39-top4-z9-287-166-CANDIDATE.json` | 原版z9完整词形的中文补回：马佐夫舍地区奥斯特鲁夫、奇热夫、布兰斯克、切哈诺维茨等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G136-full39-east1-z9-289-166-CANDIDATE.json` | 原版z9完整词形的中文补回：黑比亚沃斯托茨卡、克伦基、扎布武杜夫、米哈沃沃；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G136-full39-bottom5-z9-283-168-CANDIDATE.json` | 原版z9完整词形的中文补回：文格鲁夫、波德拉谢地区索科武夫、谢德尔采、姆罗济等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G167-z9-320-201-core20.json` | 原版z9完整词形的中文补回：马瓦特、苏尔达什、巴兹延、苏莱曼尼亚等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G167-z9-322-202-east8-closed.json` | 原版z9完整词形的中文补回：什韦舍、萨南达季、凯鲁恰伊、德赫戈兰等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G29-UpperSouth-z7-32-51.json` | 原版z7完整词形的中文补回：图珀洛、威尔明顿、弗洛伦斯、雅典等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G29-Florida-z7-34-52.json` | 原版z7完整词形的中文补回：盖恩斯维尔、奥卡拉、代托纳比奇、奥兰多等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AEG10-LibyanSea-z10-574-408-2-1.json` | 原版z10完整词形的中文补回：利比亚海；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AEG9-Chora-z10-583-399-1-1.json` | 原版z10完整词形的中文补回：霍拉；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AEG9-Fira-z10-584-400-1-1.json` | 原版z10完整词形的中文补回：菲拉；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AEG9-Malia-z10-584-404-1-1.json` | 原版z10完整词形的中文补回：马利亚；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AEG9-BeershebaDimona-z10-610-418-2-1.json` | 原版z10完整词形的中文补回：贝尔谢巴、迪莫纳；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AEG9-Palmyra-z10-620-407-1-1.json` | 原版z10完整词形的中文补回：巴尔米拉；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/AEG9-Turayf-z10-621-416-2-1.json` | 原版z10完整词形的中文补回：图赖夫；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/ARC3-ArcticNorthAmerica-z5-2-6-3x1.json` | 原版z5完整词形的中文补回：巴罗、波弗特海；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SA2-MODEL-z7-72-70-1-2.json` | 原版z7完整词形的中文补回：卡蒂马穆利洛、马翁；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SAH2-Tamanrasset-z8-131-111-2-1.json` | 原版z8完整词形的中文补回：塔曼拉塞特；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JA2-Hakodate-z8-228-95.json` | 原版z8完整词形的中文补回：函馆；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/JA2-LaptevSea-z5-27-5.json` | 原版z5完整词形的中文补回：拉普捷夫海；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SE2-Inabag-z10-508-455.json` | 原版z10完整词形的中文补回：伊纳巴格；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SE2-Safaja-z8-152-108.json` | 原版z8完整词形的中文补回：塞法杰；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/NG45-Golmud-z7-97-50.json` | 原版z7完整词形的中文补回：格尔木；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/NG82-WadiHalfa-z8-150-112.json` | 原版z8完整词形的中文补回：瓦迪哈勒法；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/NG20-Weipa-z6-57-34.json` | 原版z6完整词形的中文补回：韦帕；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/NG14-Seychelles-z6-41-32-2-1.json` | 原版z6完整词形的中文补回：塞舌尔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/F530-G44-Dunhuang-z7-97-48.json` | 原版z7完整词形的中文补回：敦煌；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/F530-G46-YunnanKachin-z7-98-54-2-1.json` | 原版z7完整词形的中文补回：丽江、大理、密支那、保山；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/F530-G59-Zouerat-z8-119-111.json` | 原版z8完整词形的中文补回：祖埃拉特；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/F530-G102-AndamanSea-z8-195-120.json` | 原版z8完整词形的中文补回：安达曼海；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/IT8-North-z9-279-191-2-1.json` | 原版z9完整词形的中文补回：巴列塔、安德里亚、比谢列、巴里等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/IT8-Central-z9-279-192-1-2.json` | 原版z9完整词形的中文补回：阿尔塔穆拉、马泰拉、皮斯蒂奇、波利科罗；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/IT8-Middle-z9-278-194-2-1.json` | 原版z9完整词形的中文补回：卡斯特罗维拉里、罗萨诺斯卡洛、阿克里、斯卡莱阿等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/IT8-South-z9-279-195-1-2.json` | 原版z9完整词形的中文补回：科森扎、圣乔万尼因菲奥雷、拉梅齐亚泰尔梅、卡坦扎罗等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S156-UmmRuwaba-z8-150-118.json` | 原版z8完整词形的中文补回：乌姆鲁瓦巴；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S156-Madiama-z10-499-472.json` | 原版z10完整词形的中文补回：马迪亚马；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S156-Inekar-z10-520-466-2-1.json` | 原版z10完整词形的中文补回：伊内卡尔；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G1-Marine-z4-5-11-2x1.json` | 原版z4完整词形的中文补回：德雷克海峡、斯科舍海；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G215-CN-z10-838-433-2x1-complete4.json` | 原版z10完整词形的中文补回：上圯、水槎、上模、万安县；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G188-z9-432-220.json` | 原版z9完整词形的中文补回：竹富、石垣；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G111-CN-z9-79-192-1x1.json` | 原版z9完整词形的中文补回：阿克塔、尤里卡、福图纳；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G117-CN-z9-92-191-1x1.json` | 原版z9完整词形的中文补回：韦尔斯；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G114-CN-z9-86-204-1x1.json` | 原版z9完整词形的中文补回：文图拉、奥克斯纳德；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/F4-CLOSED-G192-z10-494-466.json` | 原版z10完整词形的中文补回：巴西克努、姆贝拉；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/F4-CLOSED-G230-z10-870-405.json` | 原版z10完整词形的中文补回：智岛邑、望云面、严多面、荏子面；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/F4-CLOSED-G231-z10-878-382.json` | 原版z10完整词形的中文补回：白岩劳动者区；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SRC62-Iran-z9-320-199-3-1.json` | 原版z9完整词形的中文补回：奥什纳维耶、纳加代、马哈巴德、皮兰沙赫尔等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/S62-SouthGeorgiaScotia-z5-12-21.json` | 原版z5完整词形的中文补回：南乔治亚岛和南桑德韦奇岛、斯科舍海；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SRC62-Iraq1-z9-320-204.json` | 原版z9完整词形的中文补回：古拉拉、萨迪亚、纳夫特沙赫尔、苏马尔等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/SRC62-Laos2-z10-814-464.json` | 原版z10完整词形的中文补回：达瓦桑、阿迈、桑努艾、孟农；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/KeMacina-Central-z9-248-236-1x2.json` | 原版z9完整词形的中文补回：萨耶、恩戈阿、迪翁迪乌孔坎坎、迪亚拉马纳等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/KeMacina-West-z9-247-237.json` | 原版z9完整词形的中文补回：萨米内、蒂耶马纳、布拉、法洛等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/KeMacina-East-z9-249-237.json` | 原版z9完整词形的中文补回：苏伦图纳、曼迪亚库伊、马雷纳；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G221-z10-847-430-1x2.json` | 原版z10完整词形的中文补回：莒口、南平、星村、兴田等；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G120-CN-z9-96-203-2x1.json` | 原版z9完整词形的中文补回：科顿伍德、普雷斯科特谷、塞多纳、里姆罗克；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/PR7-Taketomi-z10-864-440-1x1.json` | 原版z10完整词形的中文补回：竹富；含必需跨片替换与符号。 |
| `dict/placenames/map_tiles/regions/G189-context-z9-442-210-recognized3.json` | 西之表、中种子、南种子的完整中文，闭合跨片西之表原文字形。 |
| `dict/placenames/map_tiles/regions/G27-FortSevern-z7-32-39.json` | 塞文堡z7完整跨片中文标签及实见普通城市圆点。 |
| `dict/placenames/map_tiles/regions/G119-PageHeight-z9-96-199-private.json` | 佩吉及原5028米标签，原生山峰三角像素保留；卡纳布引用旧owner。 |
| `dict/placenames/map_tiles/regions/G121-Arizona-z9-96-205-2x2.json` | 亚利桑那 z9 的11个中文标签；既有巴克艾保持原 owner。 |
| `dict/placenames/map_tiles/regions/G32-Amazon-z7-42-64-private7.json` | 巴西亚马孙 z7 的7个中文地名及7个实见城市圆点。 |
| `dict/placenames/map_tiles/regions/G189-z9-441-208-core12.json` | 九州 z9 核心及南部3×2区域的15个中文地名；保留必需空中文片。 |
| `dict/placenames/map_tiles/regions/G225-Quezon-core-z10-859-472.json` | 奎松省 z10 的4个中文标签；古马卡复用既有 owner。 |
| `dict/placenames/map_tiles/*.jsonl` | 去重原图索引、公共来源 URL 与精简审核表；保留必要来源、输入与 RGBA 指纹 |
| `docs/MAP_TILE_SOURCES.md` | 拆分地图格式、编辑规则、临时展开、完整重建与历史指纹迁移边界 |
| `assets/placenames/ChineseMapTiles.dat` | 已核验清单确定性编译的中文标签瓦片，安装器/绿色包/运行时共用；新增覆盖必须先通过来源与像素审核 |
| `scripts/build_map_tile_pack.py` | 有限缓存区域的原图、中文、字体和审核审计；核对显式选择的原始 WWP 中文补绘，构建离线 PNG 标签层及 CMOTIL01/02；未核验仅允许预览 |
| `scripts/map_tile_manifest.py` | 读取拆分地图源、解析公共引用和列举完整构建输入；支持 split/expand，无自动审批 |
| `scripts/test_map_tile_manifest.py` | 拆分源语义往返、失效审批、引用拒绝及迁移后的独立源码包字节复现测试 |
| `scripts/verify_map_tile_source_package.py` | 核验地图源码包的逐文件指纹与必需输入，确保发布包可按冻结来源复现 |
| `scripts/test_build_map_tile_pack.py` | 离线地图瓦片门禁、跨 tile 拼接、海拔保留、原始 WWP 补绘与选择失配拒绝、二进制契约和失败保留旧产物测试 |
| `scripts/requirements-map.txt` | 离线地图瓦片构建所需 Pillow/fontTools；运行时无需 Python，字体使用本机文件不复制入库 |
| `scripts/import_stamen_map_catalog.py` | 从固定来源导出与中文候选构建独立全球目录输入；保留原图证实缩放、非连续范围及未决记录，默认阻断全量编译 |
| `scripts/test_import_stamen_map_catalog.py` | 全球来源导入的身份审核、缩放分段、原图例外和未决分母保留回归 |
| `docs/superpowers/plans/2026-10-21-global-map-labels.md` | 全球地图中文补回的实施步骤、接口和完成前置证据 |
| `docs/GLOBAL_MAP_LABELS.md` | 全球标签来源、数据契约、编译/读取接口与未完成的完整性验收边界 |
| `scripts/test_release_placenames.py` | 发布侧地名门禁测试（元数据指纹、flags/顺序/坐标篡改必须失败；含 `placename_payload_problems` 入包对账用例，供 `R80-static-placename-payload` 调用） |
| `scripts/test_static_html.py` | 静态 HTML 汉化断言（编码/完整译文/`.orig` 备份保留） |
| `scripts/test_scenario_html.ps1` | 想定 HTML 导出的 PowerShell 回归 |
| `scripts/test_web_translation.cjs` | WebView2 译文脚本的 Node 侧契约测试 |
| `scripts/test_web_translation_state.cjs` | 独立无头 Edge 验证 Web 配额续做、同节点取消、来源/nonce/编辑保护；不操作游戏 |
| `scripts/test_webview2.ps1` | 真实 Edge/WebView2 DOM 回归 |
| `scripts/test_ui.ps1` | UI 模式回归（控件/下拉/设置抽查），需要游戏在跑 |
| `scripts/scan_english_residue.py` | **英文残留扫描（R52）**：拿游戏数据本身（DB 各表 Name、枚举 Description、想定源）当语料喂给引擎 `TranslateString`，判据是「翻完还剩多少真英文词」。这是唯一能发现「没进词典的字符串」的机制——`L-ui-residuals` 只能查已入词典的条目 |
| `scripts/build_translation_batches.py` | 缺口翻译流水线（R52）：`--source db/corrupt` 生成批次清单 → 交 LLM 逐条精修 → `--merge` 按**原归属**写回并跑守卫（拒收句子碎片、结构性内容、长度异常） |
| `scripts/term_gap_analysis.py` | 术语缺口分析（R52）：逐字复现引擎匹配规则，报出每条缺口卡在哪个词上，算出「加哪个词解锁最多条目」。输出收益排行 + 可生成术语批次 |
| `scripts/merge_terms.py` | 术语合并（R52）：把 LLM 精修的术语写进 `dict/src/terms.json`。守卫拒收未翻译/半译/句子/过长；术语表是组合翻译的词汇层，一个坏词会污染上千条名字 |
| `scripts/merge_record_names.py` | 数据库装备名合并（R52）：写回 `db_record_names.json`。守卫拒收残留英文词、二选一写法、结构性内容；型号/编号/`Type 63A` 这类正确放行 |
| `scripts/db_comments_next.py` | 列出 Comments 词表的下一批待译段（按出现行数排序，剔除 `Mod`/`Blk`/`Mk` 等型号批次词与纯代号）；配合人工翻译循环用 |
| `scripts/db_comments_work.py` | 数据库列表行尾 Comments（R72）待译清单 + 词表守卫：`--check` 供门禁（译文含中文、无纯数量键、无残留英文词），默认输出覆盖率与按行数排序的待译段 |
| `scripts/fix_db_residue.py` | 数据库记录名括号外英文残留的**确定性**改写（R71）：`Type NNN`→`NNN型/式` + 人工核过的厂名与整串译名；绝不按词替换（实测会把 `PC2`→「巡逻艇2」）。产出 `build/residue_out/batch_*.json` 交 `merge_record_names.py` 合并 |
| `scripts/merge_scenario_texts.py` | 想定文本合并（R52）：按批次清单的 `src` 写回**原归属** side/message 文件（全塞一个池会被字母序覆盖），DTG 时间码放行 |
| `scripts/quickbattle_html.py` | **快速战斗表单页工具**（单一入口）：`work` 抽待译片段+属性指纹、`hints` 给每批附已审定术语、`import` 合入译文并过守卫、`build` 渲染纯中文页并打 `QuickBattle_HTML.zip`、`verify` 门禁（页数/属性未变/无残留英文）。改译文或改渲染规则时只动这里 |
| `scripts/gen_v3_batches.py` | 按 `build/residue_gaps.json` 的当前真实剩余缺口，按表族生成 v3 精修批次（每类一套专用 instructions：传感器北约绰号、挂载弹种、设施编制、飞机绰号、卫星代号、武器弹头、基座形式、推进装置、舰艇舰级）。批次写入 `build/v3_batches/`，供 teammate 逐条精修 |
| `scripts/verify_placenames.py` | 地名发布门禁 |
| `scripts/test_placenames.py` | 地名离线回归（写操作只落在 `build/` 临时目录） |
| `scripts/test_placename_cache.py` | 用合成 WWP/PNG 做 Windows 集成测试 |
| `scripts/test_verify_placenames.py` | 地名门禁自身的回归用例 |
| `scripts/refresh_placename_tiles.ps1` | 重建地名瓦片（PowerShell） |
| `scripts/test_refresh_placename_tiles.ps1` | 上面脚本的测试 |
| `scripts/test_map_tile_pack.ps1` | 有限已核验瓦片包格式、缓存安全和运行时/安装器双入口模拟测试 |
| `scripts/test_map_label_renderer.ps1` | 地图标签渲染测试驱动 |
| `scripts/test_map_label_history.py` | 在 Windows 上编译 Git 上游和当前地图渲染器，逐缩放复核原有地名的实际显示。 |
| `src/tests/MapLabelHistoryTests.cs` | 对照 WWP 和 R88 的真实绘制记录，检查每条历史可见地名的包内替代和动态绘制。 |

### 8.7 杂项

| 路径 | 职责 |
|---|---|
| `scripts/scenario_meta.py` | 抽取想定元数据（键已排序 + 优先正式想定路径，保证产物稳定） |
| `scripts/credentials.py` | **凭据读取唯一入口**（密钥绝不入库） |
| `scripts/proc_util.py` | **子进程唯一入口**：给子进程注入 UTF-8 输出环境 + `utf-8 → cp936 → replace` 容错解码；脚本捕获子进程输出一律走这里，禁止裸 `text=True`（见 R63） |
| `scripts/cmocmd.sh` | 命令行快捷封装 |
| `scripts/migrate.sh` | 迁移脚本（历史） |
| `scripts/verify_launch.sh` | 启动验证（历史） |

---

## 9. 引擎诊断命令（实机验证靠它）

这些不是文件，而是**引擎内部命令通道**，通过 `scripts/live_harness.py` 的 `send()` 发送。
加新命令的落点：`src/engine/CMOLocalizer.cs` 的 `ProcessTestCommands()`。

| 命令 | 用途 |
|---|---|
| `XLATE <文本>` / `XLATE_BATCH <file>` | 只读翻译探针，验证某串会译成什么 |
| `WPF_DUMP` / `WPF_TEXTLOG` | 导出 WPF 控件树 / 文本变更日志 |
| `WEAPONS_DIAG SNAPSHOT / START / STOP` | 只读武器子树诊断：绑定/Inline/位置与 8 秒有界 Loaded/属性钩子/Rendering 记录；不是像素或首帧验收结果 |
| `CTRL_TREE [过滤]` | 控件树（可按窗体名过滤） |
| `CTRL_TEXT [窗体过滤] [控件名]` | 只读：取控件**完整**文本（`CTRL_TREE` 每个 text 截到 120 字符，多行富文本靠它拿不到原文）；省略控件名则导出该窗体所有非空文本。R91 用它采集快捷键窗口 6 段正文 |
| `TRACE_START` / `TRACE_STOP` / `TRACE_DUMP` | 事件追踪（含 `renderTicks`、`elementHostScans` 计数） |
| `FLICKER_PROBE` / `FLICKER_RESULT` | 频闪检测（按树内位置路径做 key） |
| `DUMP_COMBOS` / `DUMP_LISTS` / `DUMP_MENUS` / `DUMP_OPEN_FORMS` | 下拉框/列表/菜单/窗体导出 |
| `CLICK_MENU` / `CLICK_BUTTON` / `CLICK_WPF` / `CLICK_TOOLBAR` / `SELECT_TAB` | 模拟点击（**`CLICK_BUTTON` 按窗体顺序匹配，跨窗体同名会打错**） |
| `SCEN_*`（`SCEN_CTRL_TREE` / `SCEN_SELECT` / `SCEN_LOAD` / `SCEN_EXPAND_ALL`） | 想定加载流程 |
| `POOL_DUMP` | 导出字符串池注入情况 |
| `RELOAD_DICT` | 重载词典（改词典后免重启） |
| `BRIEFING_DUMP` | 只读：把当前打开的富文本简报（阵营简报/想定说明）正文导到 `CMOLocalizer_Briefing.txt`，并返回字符/汉字/英文行数与样本。简报汉化只能靠这个数字验收 |
| `WEBVIEW_TEXT <窗体>` / `WEBVIEW_JS <窗体> <脚本>` | 只读：在该窗体的 WebView2 里取 `body.innerText` / 执行一段脚本（阵营选择、系列战役描述面板只在这里能看到 DOM） |
| `WEBVIEW_DIAG <窗体>` | 只读：WebView2 桥状态——消息事件是否接上、回执计数 `messageEvents`、文档 nonce/来源、队列与首个丢弃原因（R84 靠它分清「译文没命中/回包被拒/通道死了」） |
| `CMOLocalizer_Status.txt`（游戏目录，运行时自写） | **本地化自检**：游戏内部版本（取自主窗体标题）、词典条数、池注入结果（OK/FAILED + 原因）、`build_check`（游戏构建号 vs 安装清单适配号）、标签层 URL 是否重定向、标签服务端口与地标数、已服务瓦片数。更新器读 `build_check` 在安装前示警（R87）；用户报「顶部状态栏/地形提示/地图标签仍英文」时先要这个文件（R86） |

---

## 10. 资源文件

| 路径 | 职责 |
|---|---|
| `assets/placenames/Synoptic.wwp` | 译好的概览地名集（打包时落到 `WW/PlacenameSets/Synoptic/0000_0000.wwp`） |
| `assets/placenames/Cities.wwp` | 译好的城市地名集（同上，`Cities/`） |
| `assets/launcher/stringsChinese.resx` | 启动器的中文字符串表 |
| `assets/db_templates/*.html` | 数据库查看器 HTML 模板（Aircraft/Facility/GroundUnit/Platform/Satellite/Ship/Submarine/Weapon 8 个） |

**运行时目录（不入库，`.gitignore`）**

| 目录 | 说明（不入库、不参与登记） |
|---|---|
| `build/` | 全部编译产物 + `regression_report.json` + 打包产物。**整目录 gitignored** |
| `evidence/` | 实机验证截图/证据。gitignored |
| `dict/worklist.json`、`dict/db_desc_work.json`、`dict/db_desc_chunks/`、`dict/db_desc_out/` | 中间态工作文件，gitignored |

---

## 11. 文档（`docs/`）

### 11.1 活文档（要维护，改代码可能连带要改）

| 路径 | 职责 |
|---|---|
| `docs/ROUTING.md` | **本文件**。新增文件必须登记 |
| `docs/BUG_REGISTRY.md` | 缺陷台账 R1–R89（CRLF，**只增不改**；R89 为未闭环条目）。修 bug 必须登记 |
| `docs/CHANGELOG.md` | 发布说明；`publish_release.py` 取顶部段落作为 Release 正文 |
| `docs/RELEASE_MANIFEST.md` | 发版清单与规则（含"已发布资产只增不改"） |
| `docs/RELEASE_TEST_SOP.md` | 发版测试 SOP |
| `docs/project_memory.md` | 项目口径与历史决策（16 条，改口径要同步） |
| `docs/AUTO_UPDATE_DESIGN.md` | 更新助手设计 |
| `docs/GITHUB_AUTO_UPDATE_DESIGN.md` | GitHub 自动更新设计 |
| `docs/FLIGHT_TUTORIAL_MANUAL.md` | **飞行教程通关手册**：8 个基础飞行教程的入口/按键/判定/注意事项 + 汉化排障口径。改教程相关 UI 文案、快捷键或汉化行为时同步更新 |
| `docs/PLACENAME_LOCALIZATION.md` | 地名汉化方案 |
| `docs/LOCALIZATION_VALIDATION.md` | 汉化验证说明（各门禁与实机检查的边界、计时口径、已知限制）。PR#5 引入，是「哪些通过了、哪些没验」的权威说明 |
| `docs/PLACENAME_SOURCES.md` | 地名译名的资料来源说明（哪些是中文通行名、哪些来自当地名称、哪些人工整理） |
| `docs/PLACENAME_SYNOPTIC_REVIEW.md` | 321 个区域标签的专项审核记录 |
| `docs/PLACENAME_REVIEW_REPORT.json` | 地名审核报告（机读，供复核与回溯） |
| `docs/PLACENAME_GLOBAL_REVIEW_PLAN.md` | 全量核对 4,287+321 个点的执行方案（历史计划，已完成使命，保留备查） |
| `docs/PLACENAME_LABEL_RENDERING.md` | 地名标签渲染方案 |
| `docs/MENU_TERMINOLOGY.md` | 菜单术语对照 |
| `docs/MAP_TILE_SERVER_VALIDATION.md` | 地图瓦片服务验证 |
| `docs/TUTORIAL_REGRESSION_V413.md` | 教程回归记录 |
| `docs/design/updater_mockup.html` | 更新助手 UI 原型 |
| `docs/credentials.example.json` | 凭据配置示例（**不含真实密钥**） |

### 11.2 历史记录（只读参考，不再更新）

| 路径 | 说明 |
|---|---|
| `docs/CHECKPOINT_V2.md`、`docs/checkpoint_notes.md`、`docs/checkpoint_turn31.md`、`docs/SUBWINDOW_CHECKPOINT.md`、`docs/project_checkpoint_db1478.md` | 历次开发检查点快照 |
| `docs/DIAGNOSIS_AND_FIX.md`、`docs/CRITICAL_FIX_NOTE.md` | 历史故障诊断记录 |
| `docs/FULL_LIVE_TEST_REPORT.md`、`docs/TEST_REPORT.md`、`docs/LIVE_TEST_PLAN.md` | 历史测试报告/计划 |
| `docs/NEXT_V414_PENDING.md` | v4.1.4 待办（已过期） |
| `docs/DEEP_LOCALIZATION_PLAN.md`、`docs/TRANSLATION_BRIEF.md`、`docs/BRIEFING_BRIEF.md`、`docs/brief_remaining.json` | 深度汉化/简报翻译方案（历史） |
| `docs/project_optimization_record.md` | 优化记录 |
| `docs/menu_strings_dump.txt` | 菜单串导出（历史快照） |
| `docs/README_汉化说明.txt` | 面向用户的说明文本 |

---

## 12. 测试工程（`src/tests/`）

| 路径 | 职责 | 对应门禁 |
|---|---|---|
| `src/tests/MixedPrefixTests.cs` | R42「英文前缀 + 中文尾」混排修复测试 | `R42-static-mixed-prefix-wiring` |
| `src/tests/IdempotencyTests.cs` | R45 翻译幂等（括号层数收敛） | `R45-static-translate-idempotent` |
| `src/tests/MapTileServerTests.cs` | 地图瓦片服务 7 模式并发回归 | `R34-static-tile-*` |
| `src/tests/MapLabelTilePackTests.cs` | 已核验 tilepack 加载、缓存增量与双入口回归（仅模拟目录） | Task 6 |
| `src/tests/MapTileDeliveryTests.cs` | 安装器瓦片包来源、原子部署、清单与还原的隔离测试宿主，不运行安装/卸载全流程 | Task 9 |
| `scripts/test_map_tile_delivery.ps1` | 编译真实 C# 5 安装器资源 fixture，运行隔离交付测试；不写游戏目录 | Task 9 |
| `scripts/test_map_tile_delivery_contract.py` | 无 Bash 环境下隔离执行部署/绿色包内嵌 Python 门禁，并检查资源和安装调用契约 | Task 9 |
| `src/tests/MapLabelRendererTests.cs` | 地图标签渲染 | `R33`/`R38`/`R39` |
| `src/tests/MapLabelSamples.cs` | 渲染测试用的样例数据 | — |
| `src/tests/CMOLocalizerTest.cs`、`src/tests/CMOLocalizerTestV2.cs` | 引擎早期冒烟测试 | — |
| `src/tests/TestV3.cs`、`src/tests/TestHijack.cs`、`src/tests/TestDarkReflection.cs` | 挂钩/反射验证（历史） | — |
| `src/tests/DummyHost.cs` | 测试用宿主桩 | — |
| `src/tests/BulkTranslationTests.cs` | 批量译文契约（34 条） | — |
| `src/tests/ComboBoxTranslationTests.cs` | 下拉项增量翻译：业务键/选中值/token 保持 | — |
| `src/tests/DarkTreeTranslationTests.cs` | 深色树分片遍历与集合归属核对 | — |
| `src/tests/UiIdleContinuationTests.cs` | 空闲续接器的模态/DoEvents 宿主行为 | — |
| `src/tests/UiLifecycleTests.cs` | UI 生命周期挂钩 | — |
| `src/tests/UiSchedulingTests.cs` | UI 调度顺序（不吞掉新投递的回调） | — |
| `src/tests/UiPerfMetricsTests.cs` | UI 执行计量负向验证：排队不算执行、慢回调超帧、取消不完成、忙时拒绝重置 | `R55-static-ui-perf` |
| `src/tests/FormsSnapshotTests.cs` | R59 回归：WinForms 活集合在窗体增删期间必须取快照遍历；含“直接 foreach 会抛错”的负向对照 | `R61-static-forms-snapshot` |
| `src/tests/WpfSchedulingTests.cs` | WPF 分片与归属回归：稳定同级游标续扫、跨 Dispatcher 合并、换根取消、输入让出 | `R54-static-wpf-scheduling` |
| `src/tests/WpfSafetyTests.cs` | WPF 写回安全（不重入属性系统、编辑区不写）与只读武器诊断的空目标/错控件/配额/命令可达性检查 | — |
| `src/tests/FirstPaintTests.cs` | 首帧中文（不等下一轮慢扫） | — |
| `src/tests/UpstreamCompatibilityTests.cs` | 与上游 master 修复的兼容性 | — |
| `src/tests/WebViewTranslationHostTests.cs` | 注入宿主：来源/nonce/容量/回退预算 | — |
| `src/tests/WebView2LatencyTests.cs` | WebView2 中文可见延迟 | — |
| `src/tests/ScenarioHtmlInstallerTests.cs` | 想定 HTML 的安装/绿色包覆盖语义 | — |
| `src/tests/DBTranslationTests.cs`、`src/tests/DBLiveProbeTests.py` | 数据库翻译与实机探针 | — |
| `src/tests/PanelFlickerProbeTests.py` | 面板中英翻转探针的自测 | `L-panel-flicker` |
| `src/tests/PerformanceProbeTests.py` | 性能探针负向自测 + 分层报告：超时、零工作、未推进、错想定、漏健康、旧绿残留必须失败 | `R56-static-perf-probe` |
| `src/tests/ReleaseContractTests.py` | 隔离 unittest/mock 验证报告、四资产、冻结构建及绿色包安装器契约；不读凭据/写网络/操作游戏 | `R57-static-release-contract` |
| `src/tests/PoolDisplayInjectionTests.py` | 池级注入白名单回归 | `R38-static-pool-inject` |
| `src/tests/DictionarySourceTests.py` | 词库完整字节重建、产物独立性、源删除不回生及污染注入负向测试；只写临时目录 | `R58-static-dictionary-source` |
| `src/tests/DbListCommentsTests.cs` | 数据库列表行尾 Comments 接线与行为（R72）：接线到位、整段优先命中、未命中不拆词、纯代号不产生中文污染 | `R72-static-db-comments` |
| `src/tests/DatabaseResidueTests.py` | 数据库记录名三道判据：场景源不得用句子碎片覆盖 db_* 干净译名、已修脏值不得回生、残留改写脚本不得按词污染型号（均带负向用例）| `R71-static-db-residue` |
| `src/tests/SanitizeGuardTests.py` | R76 构建期守卫双向回归：分号句尾散文 / 番号后括号 / 全角括号互写不得误杀；真代码与括号结构差仍拦住 | `R76-static-sanitize-guards` |
| `src/tests/WebBrowserInjectionTests.cs` | R77 中文页跳过【】双语注入：中文页/英文页/少量中文标签/空/null/长英文短中文六种判据 | `R77-static-webbrowser-injection` |
| `src/tests/StaticHtmlCharsetTests.py` | R78 简报 HTML 编码声明：缺失时补上、已有不重复、按真实编码声明 | `R78-static-html-charset-tests`
| `src/tests/README_WebViewTranslation.md` | 上面 WebView 系列测试的说明 | — |
| `src/tests/search_methods.cs`、`src/tests/test_db_load.cs` | 方法搜索 / DB 加载试验（历史） | — |
| `src/tests/README_MapTileServer.md` | 瓦片服务测试说明 | — |
| `src/tests/ResidueScan.cs` | R52 扫描器的 C# 宿主：加载引擎 DLL、反射调 `TranslateString`，把 TSV 语料逐条翻译并标注 `ok/none/partial` |

> **注意**：`MixedPrefixTests` / `IdempotencyTests` 接受第 2 个 CLI 参数为引擎 DLL 路径，
> 目的是防止读到过期的 `build/CMOLocalizer.dll` 造成假绿。

---

## 13. 遗留与一次性文件

### 13.1 `src/tools/`（82 个，全部是逆向探测工具）

**只有一个还在用**：`src/tools/GuiDriver.cs`（编成 `build/GuiDriver.exe`，
提供 list/children/postchar/capture/focus，实机测试驱动依赖它）。

其余 81 个是**一次性逆向探测脚本**，按前缀分族，需要时可参考其手法，但**不要以为它们还在链路里**：

| 路径 | 族 | 用途（历史） |
|---|---|---|
| `src/tools/GuiDriver.cs` | — | **在役**：GUI 自动化驱动 |
| `src/tools/*.cs` | 全部 | 兜底覆盖（上面已逐族说明；新增工具请加具体行） |
| `src/tools/Inspect*.cs` | Inspect 族 | 反射探查游戏 DLL/窗体/方法 |
| `src/tools/inspect_*.cs` | inspect 族 | 同上，后期批量版本（更细粒度） |
| `src/tools/Probe*.cs`、`src/tools/probe_*.cs` | Probe 族 | 针对具体控件/IL 的定点探测 |
| `src/tools/dump_*.cs` | dump 族 | 导出 XML/IL/加密简报/沙箱/刷新逻辑 |
| `src/tools/scan_*.cs` | scan 族 | 扫描 core/DAL/UI 对话框 |
| `src/tools/find_*.cs`、`src/tools/Find*.cs` | find 族 | 查找调用者/方法/类型/字符串 |
| `src/tools/check_core.cs`、`src/tools/ILScanner.cs`、`src/tools/Disasm.cs`、`src/tools/DumpIL.cs`、`src/tools/DumpTokens.cs`、`src/tools/WinProbe.cs`、`src/tools/build_chinese_tiles.cs` | 其他 | 反汇编/令牌导出/窗口探测/中文瓦片构建 |

### 13.2 一次性脚本

| 路径 | 说明 |
|---|---|
| `scripts/archive/*` | 归档的历史脚本（`translate_shard2.py` + 说明） |
| `scripts/bisect_offsets.py` | 二分定位导致启动失败的偏移条目（排障时可能还用得上） |
| `scripts/patch_engine_escapes.py` | 一次性源码补丁器（给引擎 JSON 扫描器补转义），**已完成使命** |
| `scripts/translate_shard1_a.py`、`scripts/run_translate.py`、`scripts/deep_translator_engine.py`、`scripts/deep_translator_engine_v2.py`、`scripts/generate_deep_localization.py` | 早期批量机翻流水线（已废弃，机翻质量不合格） |
| `scripts/find_nav_calls.py` | 早期探索脚本 |
| `scripts/verify_tutorials_v413.py` | v4.1.3 专用静态回归（版本已过） |

---

## 14. 常见任务 → 该改哪里

| 我要做的事 | 按顺序看这些 |
|---|---|
| 加一条界面翻译 | `dict/src/` 里对应的 json → `python scripts/build_dict.py` → `bash scripts/build.sh` → `bash scripts/deploy.sh` → `verify_regressions --static` |
| 修引擎翻译逻辑 | `src/engine/CMOLocalizer.cs`（先看 §4 内部地图）→ 加/改用例到 `src/tests/` → `verify_regressions.py` 里加 static 项 → build/deploy → 跑门禁 |
| 修"中英混排/半译" | `compose_terms.py`（规则镜像）+ `dict/src/terms.json`（词汇）+ 引擎 `TryComposeTranslate` 族，**三处要同步** |
| 修"面板频闪/越翻越长" | 引擎 `AllowWrite` 令牌桶 + `SetTextKeepBinding` 幂等短路；用 `FLICKER_PROBE`/`WPF_TEXTLOG` 取证 |
| 修"卡顿" | 先跑 `scripts/perf_probe.py`（看补扫频率与翻译事件）；大想定/多单位必须跑 `scripts/perf_probe_large.py`（渲染帧与补扫是否 1:1、有无僵尸 WPF 窗口）；改 `StartWpfRepaintTick`/`_repaintPeriod`/`TranslateWpfWindows`（R53 起全树补扫有 4ms 预算与游标续扫） |
| 加一个门禁用例 | `scripts/verify_regressions.py`（static/live 各自列表）；live 用例写在 `--live` 段 |
| 改安装器 | `src/installer/CMO_Chinese_Setup.cs` → `build.sh` → `verify_regressions --static --installer` |
| 改更新助手 | `src/updater/CMO_Updater.cs` + `docs/AUTO_UPDATE_DESIGN.md` → `test_updater_live.py` |
| 改地名译名 | `dict/placenames/cities.json` / `synoptic.json`（审核源）+ `scripts/placename_translation.py` → `apply_placenames.py` / `build_cities_placenames.py` → `verify_placenames.py` |
| 改数据库描述 | `dict/src/db_descriptions.json`（或走 `db_desc_work.py` 抽取 → 译 → `db_desc_merge.py` 合回） |
| 改快捷键窗口正文（按键/说明） | `dict/hotkeys/hotkeys_source.json`（实机原文，先 `CTRL_TEXT 快捷键` 重新采集）→ `dict/src/hotkeys_ui.json`（整段译文，键列/制表符逐字节保留）→ `build_dict.py` → `verify_hotkeys_ui.py`；**别走**简报批次（会把两列排版挤歪） |
| 游戏更新适配 | `GAME_BUILD` → 结构探测 → 偏移复核 → `verify_all.gate_version` + `L-build-adaptation` |
| 发版 | `docs/RELEASE_MANIFEST.md` → `docs/RELEASE_TEST_SOP.md`（**先拿用户授权**） |
| 加新脚本/新源文件 | **必须在本表登记**，否则 `verify_routing` 报红 |

---

## 15. 新增文件的规矩

1. **写在能一眼看出归属的目录**：引擎改动进 `src/engine/`，源翻译进 `dict/src/`，
   门禁进 `scripts/`，测试进 `src/tests/`。不要往根目录丢。
2. **在本表对应小节加一行**：`| \`路径\` | 一句话职责（干什么、谁调用、什么时候该改） |`。
   - 路径用反引号包裹，否则门禁解析不到。
   - 只需登记**路径模式**，一批同类文件可用 `dir/*` 或 `前缀*`。
3. **一次性脚本必须归到 §13**，并写明"已废弃/已完成使命"，否则后人不敢删。
4. **跑 `python scripts/verify_routing.py` 确认绿灯**。

> 门禁的匹配规则：表格首列反引号内的内容，`*` 匹配任意字符（**含 `/`**），
> `?` 匹配单字符，其余字面量。所以 `src/tools/*.cs` 能覆盖整个目录。
