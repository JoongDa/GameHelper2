# Equipment P0 验证

在仓库根目录运行：

```powershell
./Plugins/StashUtility/diagnostics/Test-EquipmentP0.ps1
./Plugins/StashUtility/diagnostics/Test-EquipmentP0.ps1 -Slots 576
```

需要 .NET 10 SDK，并先完整构建一次 Release 解决方案（配置测试复用其现有 Newtonsoft.Json.dll）。脚本不增加 NuGet 包，不连接游戏，不修改项目配置。它在系统临时目录建立独立项目，完成后删除该临时目录。`.cs.txt` 文件只作为测试输入，避免被插件项目默认编译。

验证分为五部分：

1. 使用工作区中的实际 `EquipmentMatcher.cs`、`EquipmentGroup.cs` 和 `EquipmentMod.cs`，执行 33 项断言，覆盖 GameModId 精确匹配、PropertyId 去重、旧 Profile 的变体范围、Raw Roll 边界、平均伤害、非法值、至少 N 项条件、多 Profile 和复用状态。
2. 从 `git show HEAD` 提取原版 `StashUtilityCore.EquipmentPerformance.cs` 及原版 `GetItemAddressFromElement`、`IsValidItemEntity`、`ReadFreshItem`、`TryReadEquipmentMods`，用模拟 Core、物品内存和绘制接口运行。唯一逻辑替换为可控时钟，使 100ms 同时过期场景可重复。`-BaselineRef` 可指定其他原版提交；默认 HEAD 适用于本次未提交的 P0 修改。
3. 直接编译工作区中的新版 Performance partial 和扫描、Item 构造、词缀读取方法；使用相同的可控时钟及模拟内存，验证稳定命中、分批刷新、物品替换、地址复用、读取失败、重复遍历、组件原地变化、200ms 保护、规则修改、UI 包装对象复用、切页、切区、进程重连、空槽恢复、无规则类别 Metadata 复验和队列优先级。
4. 直接提取实际内存读取方法及 MemoryReaders partial，运行 8 项委托绑定、失败返回和句柄重绑定断言。
5. 直接编译实际 SettingsPersistence partial，运行 9 项配置合并、强制保存、保存失败与恢复断言。仅将时钟及测试类命名空间替换为独立测试版本。

模拟内存每个物品只有一个已知单值词缀，扫描范围为 `0x100..0x180`，每槽位 16 个候选偏移。Item 使用原版反射创建方法，但 Item 内部实现和读内存传输层为桩。计数验证的是原版调用次数；分配结果仅适用于该模拟模型，不能作为真实游戏的 GC 数据。脚本不测量帧耗时，也不能验证真实仓库的 UI 偏移。

本机 .NET 10 SDK、144 槽位的基线示例：

| 场景 | Metadata 字符串 / 帧 | 指针读取 / 帧 | Item 创建 / 帧 | Mods Header / 帧 | Vector 数组 / 帧 | 模拟分配 / 帧 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 已占用槽位稳定命中 | 144 | 288 | 0 | 0 | 0 | 0 B |
| 已占用槽位 100ms 同时过期 | 144 | 432 | 144 | 288 | 576 | 249,984 B |
| 空槽位稳定命中 | 0 | 0 | 0 | 0 | 0 | 0 B |
| 空槽位 100ms 同时过期 | 0 | 2,304 | 0 | 0 | 0 | 0 B |

新版在相同模拟模型中的稳定命中结果：144 个槽位每帧 144 次槽位指针读取 + 144 次 ItemStruct 身份读取，Metadata、Item 构造、Mods 读取及模拟主线程分配均为 0。组件未变化的周期刷新也不重建 Item。全部到期时最多处理数量预算内的条目，并受 2ms 软时间预算限制；JIT、机器负载会影响实际单帧处理数量，脚本不将这个数量作为固定性能指标。

空槽冷扫描在本模型中由每槽位 16 次指针读取变为一次批量向量读取。默认生产扫描范围 `0x20..0x600` 共 188 个候选，每 64 个批量读取一次；批量读失败时逐指针回退，维持可读边界处的发现能力。未验证空槽的 80~100ms 失败缓存分散重扫；已验证偏移变空后仍每帧检查一个指针。

分配量可能随 SDK、运行时和 JIT 改变。指针读取是槽位、实体 Details 和词缀名称指针的合计，不包含 Core Item 构造内部读内存。Metadata 每次完整验证还会读取一次 `StdWString` 结构。输出末尾的 `Original baseline invariants: PASS` 表示上述调用结构得到验证。

合并验证仍需游戏内检查：普通仓库与四倍仓库首次打开、连续切页、移动/替换/移除物品、原地址上改变词缀、UI 节点地址复用、切区、进程重连、修改 Profile、暂时读失败后恢复，以及地图石/碑牌/珠宝原有行为。模拟测试无法替代这些实际内存和 UI 场景。

游戏内可在装备设置中勾选“测量叠加层性能”，并关闭调试探针。该开关默认关闭、不写入 Profile；每秒显示 DrawUI 主线程耗时/分配和插件入口计数的平均值、峰值、合计。Core 内部 Item/UI 读取以及并行父节点更新的实际读取次数不在这些入口计数中，因此不能将其当成全进程 ReadProcessMemory 总数。
