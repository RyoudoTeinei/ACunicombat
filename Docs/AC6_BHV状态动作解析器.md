# AC6 BHV 状态动作解析器

日期：2026-07-18

## 目标

从 `basenormal.bhv` 中恢复“行为状态索引 -> 动画 ID”，再与社区提供的官方调试状态名和状态转换图逐项对齐。该结果用于替代仅凭动画编号、姿态和 TAE 事件猜测 QB、飞行、落地等动作语义的旧方法。

## 运行

在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/BuildAC6BhvStateMap.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/BuildAC6ActionCatalog.ps1
```

第一条命令生成：

- `Tools/AssetManifests/c0000_bhv_state_map.json`
- `Tools/AssetManifests/c0000_bhv_state_map.csv`

第二条命令把经过验证的官方映射合并到现有动作资产表。

## 已解析结构

- BHV 文件魔数：`0x00030005`
- 状态数：222
- 状态记录大小：`0x80`
- 动作记录数：293
- 动作记录大小：`0x40`
- 状态转换数：8673
- 状态主动作索引：状态记录 `+0x10` 的低 16 位
- 动画 ID：动作记录 `+0x20` 的 32 位值

解析器同时保留其他动作槽位和完整转换边，但只有主动作槽位会直接赋予官方状态语义。辅助槽位只作为追踪数据，避免把进入、退出或方向分支误当成主状态。

## 安全校验

社区调试数据来自 AC6 v1.03。解析器不会仅凭状态数量相同就套用名称，而会验证：

1. BHV 魔数、文件长度和所有表范围；
2. 222 个状态与调试数据逐索引对齐；
3. 每个状态的转换数量逐项一致；
4. 所有动作槽索引均落在动作表范围内；
5. 已知状态映射回归断言。

任何一项失败时，输出中的 `isSafeToApplyOfficialNames` 为 `false`，官方状态名和条件不会写入资产表。

## 已确认的关键映射

| 官方状态 | 状态索引 | 动画 ID |
|---|---:|---|
| Boost flight | 47 | `a000_120200` |
| Landing | 61 / 62 | `a000_004200` |
| Heavy landing | 63 | `a000_004210` |
| Quick Boost | 77 | `a000_022400` |
| Lock-on Quick Boost | 78 | `a000_022410` |
| Boost-dash Quick Boost | 79 | `a000_022420` |
| Air Quick Boost | 82 | `a000_122400` |
| Assault Armor | 176 | `a000_050100` |
| Air Assault Armor | 177 | `a000_150100` |

这也证明旧展示使用的 `a000_010100` 不是 QB；它不能再作为 QB、AB 或飞行动作使用。

## 资产表规则

- `OfficialMapped`：BHV 二进制和官方调试状态图已安全对齐，可采用官方语义。
- `Confirmed`：用户人工预览后明确确认的覆盖结论。
- `NeedsReview`：没有官方主状态映射，仍需人工判断。
- `Rejected`：人工明确排除。

正式动作库可接受 `OfficialMapped` 或 `Confirmed`，但还必须同时拥有 Pose 和四个部件 Clip。人工输入只维护 `manualNoteZh`；自动名称、分类、说明和状态由管线重建。

## 当前缺口

解析器已经解决“这个状态引用哪个动画”的问题，但不会凭空生成缺失文件。当前提取包中：

- `a000_120200` 等部分官方动作有实体 HKX，可以解码和预览；
- `a000_022400`、`a000_122400` 等真实 QB 动作只有官方引用，本地没有同名实体 HKX；
- 缺失实体 HKX 的动作会标为 `MissingPhysicalHkx`，不能进入 Unity Clip 构建。

因此下一项资源工作是从匹配版本的游戏档案中补齐这些动作的实体 HKX/compendium。语义映射不再需要逐条人工猜测，人工工作收敛为抽查和处理没有官方主状态映射的动作。
