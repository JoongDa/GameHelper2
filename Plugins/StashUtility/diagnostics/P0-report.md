# StashUtility 装备高亮 P0 报告

## 范围和基线

开始时确认分支为 `equipment2`，工作区干净。没有切换分支、Commit 或 Push。所有源码、诊断脚本和报告修改均位于 `Plugins/StashUtility/`。没有修改 Core、GameOffsets、Launcher、其他插件、项目引用或解决方案配置，没有增加依赖。

当前环境没有运行中的游戏，因此这里区分源码路径、可重复模拟计数与实机性能。没有测得实机 FPS，也不据模拟分配推算 FPS。

## 开发前的外部研究

EE2 为主要参考，读取 `master`，对应提交 `cca30662bf31eaf38bd711e2ec1a6b899a06c40e`：

- [ParsedItem.ts](https://github.com/Kvan7/Exiled-Exchange-2/blob/cca30662bf31eaf38bd711e2ec1a6b899a06c40e/renderer/src/parser/ParsedItem.ts)、[Parser.ts](https://github.com/Kvan7/Exiled-Exchange-2/blob/cca30662bf31eaf38bd711e2ec1a6b899a06c40e/renderer/src/parser/Parser.ts)：基础类型查询、类别、原始词缀与派生属性分层；解析失败明确返回错误。类别和基础类型来自剪贴板及数据库，这部分不能替换本项目的 Metadata/组件身份验证。
- [modifiers.ts](https://github.com/Kvan7/Exiled-Exchange-2/blob/cca30662bf31eaf38bd711e2ec1a6b899a06c40e/renderer/src/parser/modifiers.ts)：逻辑属性保留多个实际来源，数值与来源分开。可借鉴属性身份独立于实际 Mod 的模型；不能照搬其求和/最大值/计数统计来改变现有装备匹配语义。
- [interfaces.ts](https://github.com/Kvan7/Exiled-Exchange-2/blob/cca30662bf31eaf38bd711e2ec1a6b899a06c40e/renderer/src/web/price-check/filters/interfaces.ts)、[create-stat-filters.ts](https://github.com/Kvan7/Exiled-Exchange-2/blob/cca30662bf31eaf38bd711e2ec1a6b899a06c40e/renderer/src/web/price-check/filters/create-stat-filters.ts)：规则明确表达属性身份、多个实际 ID、启用状态、当前值、默认范围、边界及特殊数值模式。适合在构造规则时确定比较方式；本次保留已编译条件索引。
- [create-item-filters.ts](https://github.com/Kvan7/Exiled-Exchange-2/blob/cca30662bf31eaf38bd711e2ec1a6b899a06c40e/renderer/src/web/price-check/filters/create-item-filters.ts)、[pathofexile-trade.ts](https://github.com/Kvan7/Exiled-Exchange-2/blob/cca30662bf31eaf38bd711e2ec1a6b899a06c40e/renderer/src/web/price-check/trade/pathofexile-trade.ts)：物品条件和属性条件分开；一个逻辑属性的多个交易 ID 使用至少一个成立的组合。查询结果缓存属于网络查询生命周期，不适用于游戏内存快照。
- [FilterModifier.vue](https://github.com/Kvan7/Exiled-Exchange-2/blob/cca30662bf31eaf38bd711e2ec1a6b899a06c40e/renderer/src/web/price-check/filters/FilterModifier.vue)：UI 编辑既有筛选对象，隐藏与禁用分别表达。可借鉴规则修改与展示状态分离；不移植 Vue/Electron、自动启用筛选或交易服务。

APT 为架构与交互补充，读取提交 `3188208957422b21b69b12083a03edf3d1bac187` 下的 [ParsedItem.ts](https://github.com/SnosMe/awakened-poe-trade/blob/3188208957422b21b69b12083a03edf3d1bac187/renderer/src/parser/ParsedItem.ts)、[modifiers.ts](https://github.com/SnosMe/awakened-poe-trade/blob/3188208957422b21b69b12083a03edf3d1bac187/renderer/src/parser/modifiers.ts)、[filters](https://github.com/SnosMe/awakened-poe-trade/tree/3188208957422b21b69b12083a03edf3d1bac187/renderer/src/web/price-check/filters) 中的 interfaces、create-stat-filters、FilterModifier、create-presets 和 FilterGroup。它进一步支持“原始 Mod → 带来源的属性 → 筛选模型 → UI”的分层。其 PoE1 机制、伪属性衍生、自动选中逻辑和响应式更新不适用于本次 P0。

上述可复用/不适用结论在修改生产代码前已向用户说明。仅借鉴设计思想，没有复制第三方源码；[EE2 MIT 许可证](https://github.com/Kvan7/Exiled-Exchange-2/blob/cca30662bf31eaf38bd711e2ec1a6b899a06c40e/LICENSE)的版权要求已阅读。

## 现有链路与确认的成本

原链路为 `DrawUI → DrawOverlayFrame → 仓库/背包槽位遍历 → UiElement 构造与屏幕矩形 → GetItemAddressFromElement → 完整 Metadata 验证 → ProcessItemSlot → 100ms 快照 → Item/Mods 读取 → EquipmentMatcher → 高亮`。

缓存命中之前仍执行完整 Metadata 验证；所有可见槽位先创建 UI 包装对象；未知空槽到期后逐偏移扫描；同批到期装备集中重建 Item 并分配词缀列表和原始值临时集合。反射读取包装还会创建参数数组和装箱结构。规则、视觉和 Profile 名称修改原来共用规则失效/同步 JSON 保存路径。

`EquipmentMatcher` 已有 GameModId 字典、条件数组及可复用属性命中数组。`EquipmentModOption` 已表达 PropertyId 对多个实际 Mod；数据库分类/属性构造主要在静态初始化或类别首次访问时发生。P0 没有为这些低频路径重写数据模型。

用实际原版源码、模拟内存、确定性时钟测得：

| 场景 | 原版 | P0 |
| --- | --- | --- |
| 144 装备稳定命中，Metadata 字符串/帧 | 144 | 0 |
| 同场景，槽位/实体指针读取 | 288 次指针，另有 144 次字符串结构读取 | 144 次槽位指针 + 144 次 ItemStruct 读取 |
| 同场景，Item/Mods/模拟分配 | 0 / 0 / 0 B | 0 / 0 / 0 B |
| 144 装备同批到期，Item 重建 | 单帧 144 | 组件不变时 0，词缀读取按预算分批 |
| 576 装备同批到期，Item 重建 | 单帧 576 | 组件不变时 0，词缀读取按预算分批 |
| 144 空槽冷扫，16 个候选偏移/槽 | 2,304 次指针读取 | 144 次批量向量读取 |

原版 144/576 装备到期帧的模拟分配为 249,984/999,936 B。Item 内部和内存传输层是桩，这些分配值不能当作游戏实测。测试脚本及模型限制见 [README](README.md)。

## P0 实现

1. 槽位缓存保存已验证偏移、槽位节点结构、父缓存身份和物品 ItemStruct 身份。命中时每帧只检查槽位指针与身份头，同帧重复遍历复用检查结果。空槽不构造 UI 或 Item；已验证偏移变空后仍每帧单指针检查，未知空槽以 80~100ms 错开重扫。每 64 个候选批量读取，跨不可读边界时逐指针回退。
2. UI 包装对象仅在槽位几何或节点身份变化时重建；每帧通过刷新后的父缓存计算当前屏幕坐标。P1/P2 父缓存分别绑定。节点重建、扫描范围变化、切页、切区、进程句柄变化及不可见槽位及时清理相关缓存。
3. EquipmentSnapshot 分开保存槽位/物品身份、词缀、规则版本、匹配结果、绘制坐标与时间。周期刷新复验 Metadata，组件向量未变时复用 Item。读取前后核对物品身份、槽位指针、组件向量、Mods 容器及完整 Raw Roll；部分读取不提交。
4. 保留 100ms 周期，将到期条目统一排队，按真实等待时间和验证状态排序。每帧数量预算为 `clamp(ceil(可见装备数 × 帧间隔 / 100ms), 8, 64)`，另有 2ms 软时间预算；至少完成一个工作项。已明显超时的任务提升优先级，避免持续新物品流饿死旧任务。单次不可拆分读取可能超过软预算。
5. 未刷新条目可短暂保留已验证结果；快照年龄达到 200ms 后停止绘制。读取失败立即撤下该条目高亮，以待验证状态短间隔重试；确定不匹配则是有效的零匹配快照。规则变化直接使用已有有效词缀重匹配，无词缀的快照进入优先验证。
6. 保留 GameModId 精确匹配、PropertyId 去重、Raw Roll、平均附伤、至少 N 和“各 Profile 独立达标后取最大命中数”。没有跨 Profile 累加，也没有扩展旧 Profile 选中的 tier 范围。Models、Matcher、数据库及配置 schema 未改。
7. 规则编辑只失效对应类别的编译缓存；名称、视觉与 GREAT 展示阈值仅标记持久化。UI 保存合并为闲置 500ms 或持续修改最多 2s 后同步写盘。公开 SaveSettings 和禁用路径强制同步；先完整写临时 JSON 再替换，失败保留 dirty 重试，重新启用时不以旧文件覆盖未保存内存配置。
8. 泛型内存读取改为每进程句柄绑定的委托，避免逐调用反射参数数组/装箱。复用词缀双缓冲列表、原始值临时列表和排序比较器；身份/几何比较直接比较字段，避免 ValueType.Equals 装箱。

地图石、碑牌和珠宝仍每帧创建新 Item 并进入原有 EvaluateAndHighlightItem 逻辑。共用槽位扫描、矩形缓存和读取包装得到复用，匹配和绘制条件没有改动。没有新增后台内存读取、并发、洗词、自动点击或查价 API。

## 验证与实机边界

完整解决方案 Release 构建通过：0 警告、0 错误。

```powershell
dotnet build GameOverlay.sln -c Release --no-restore -m:1 --disable-build-servers -v minimal
./Plugins/StashUtility/diagnostics/Test-EquipmentP0.ps1
./Plugins/StashUtility/diagnostics/Test-EquipmentP0.ps1 -Slots 576
```

无依赖测试直接使用生产源码：33 项 Matcher 断言、37 项生命周期断言、8 项读取委托断言、9 项持久化断言，以及原版基线不变量。覆盖同帧重复遍历后读取失败不得提前画旧结果、无规则类别仍需周期身份复验、组件原地变更、空槽即时恢复与公平排序等边界。

实机仍需验证普通/四倍仓库、商人和双人面板的 UI 坐标，连续切页/移动/原址词缀变化、读失败恢复，以及地图石/碑牌/珠宝原行为。默认关闭的“测量叠加层性能”可在实机记录正常缓存路径的帧耗时、主线程分配和计数；调试探针应关闭。

首次打开大量装备时，高亮会分帧建立。若实机读取吞吐持续低于刷新需求，200ms 保护会暂停未经及时复验的旧高亮，直至验证恢复；不会无限延长缓存来维持画面。预算是否需要进一步调节，应由实际仓库的诊断峰值和响应时间决定。

Core 的父节点并行更新仍会分配快照/临时容器并读取父节点，Item 首次构造也有内部组件读取。本次只记录这些成本，没有越界修改；插件入口计数不等于整个进程的 ReadProcessMemory 总数。批量扫描数组与一致性校验向量数组仍有周期分配，P0 未使用复杂内存池或更长的过期缓存掩盖它们。
