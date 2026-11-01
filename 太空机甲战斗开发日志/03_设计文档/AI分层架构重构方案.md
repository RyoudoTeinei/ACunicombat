# AI 分层架构重构方案

## 目的

现有的单机 AI 和编队 AI 表现"笨"，根源在结构，而不是哪个算法：

- `MechAiTargetController.Update()` 把规避、搜索、交战、编队导航串成一条 if/else 链。编队导航靠 `ShouldYieldCombatToNavigation` 事后覆盖交战结果，所以两者一直在抢输入。
- 战术意图和推力执行混写，每加一种行为都要重写加速度计算。
- 编队只有"队形 + 命令"，没有"槽位角色"。任务行为集中写在 `TryMissionNavigation` 的分支里，没有阶段。
- 缺少让开、放弃、轮转这类协同规则。

本方案借鉴《Sea Power》（海权）AI 的结构，按本项目的约束重写：只读航迹，跨机通信只走数据链。不拷贝反编译代码。

## 参考结论（海权）

| 机制 | 海权做法 | 我们采纳的形式 |
| --- | --- | --- |
| 状态切换 | 数字优先级 + `AnyTransition` 抢占，一张转移表 | `MechAiStateMachine`，额外加最短驻留时间和打断恢复 |
| 意图/执行分离 | 状态只给出 `CommandPosition`、速度档和高度 | 状态只调用 `MechAiPilot` 原语 |
| 编队角色 | `StationRole` 决定单机状态 | `MechStationRole` 决定单机状态 |
| 编队保持 | 远处真物理追位，到位后吸附长机 | 分档追位；到位后降增益并前馈长机速度，**不硬吸附** |
| 导弹规避 | 按 TTI 和自身转弯能力决定何时开始，cold 和 beam 二选一 | 同一思路，转弯能力换成横移加速度，并放干扰弹 |
| 射手去重 | 更近的友机在打同一目标就让开 | 在 L2 编队层实现 |
| 空袭阶段机 | 分配、起飞、集结、航渡、攻击、撤退 | 每种 `FleetTaskGroup` 一套阶段 |

## 分层

```
L4 指挥层   (后续) 敌方按战力派兵
L3 任务层   MechMissionFsm       每个编队一套：集结→航渡→执行→脱离→归队
L2 编队层   MechFormationBrain   槽位角色 / 目标分发 / 长机接替 / 集结判定 / 保持风格
L1 单机层   MechAiBrain          优先级抢占状态机（本轮实现）
L0 驾驶层   MechAiPilot          把意图翻译成输入帧（本轮实现）
            └ MechFlightMotionController / MechPlayerInputAdapter（不改）
```

每层只往下发意图，不越级。

## L0 驾驶层 `MechAiPilot`

原语：`Navigate`（飞到点，带速度匹配）、`HoldStation`（编队保持，含分档）、`Combat`（交战机动，内部委托现有 `MechAiManeuverPlanner`）、`Evade`（规避）、`Face` / `Search`（姿态）。

输出统一为 `MechPlayerInputFrame` 加姿态方向。加速度和刹车距离沿用 `MechAiRulePlanner` 的现有算法。

## L1 单机状态表

优先级数值越小越优先。高优先级状态用 `AnyTransition` 抢占。

| 优先级 | 状态 | 进入条件 |
| --- | --- | --- |
| 0 | Disabled | 被击毁或 AI 关闭 |
| 1 | Evade | 导弹威胁满足"该躲了"的时机判定 |
| 3 | Engage | 有可攻击目标，且编队或任务允许交战 |
| 6 | Navigate | 编队、任务或命令给出导航目标（包括编队保持） |
| 9 | Search | 默认状态 |

- Engage 内部保留现有的机动子状态（Intercept、FiringRun、ObliqueEngage、PostLaunchBreak、Extend、Recover），由 `MechAiManeuverPlanner` 管理。
- 被抢占后回到 `_lastBaseState` 重新求值，不会恢复已被取消的目标。
- 每个状态有最短驻留时间（Evade 除外），防止抖动。

## 导弹规避（替代原"0.04 秒最紧迫 + 侧闪"）

1. 威胁选择保持不变：取 CPA < 180m 的来袭弹中 TTI 最短的一枚。
2. 时机判定：`横移转向时间 ≈ 机动速度 / 可用横向加速度`。
   - 导弹相对速度优势小（≤ 阈值）或 TTI 较长时，走 **Cold**（背向拖死）。到达"拉开所需时间 + 余量"时开始机动。
   - 否则走 **Beam**（垂直于导弹速度横切）。在"建立横向速度所需时间 + 余量"时开始机动。
   - 时机未到时继续执行原状态，不提前慌乱。
3. Beam 方向选当前速度已经偏向的那一侧，省能量。
4. 最后 1.5 秒（终端）：QuickBoost 侧闪，并释放一次干扰弹（`CountermeasureReleaseEdge`）。
5. 拦截弹自动发射逻辑保持不变。

## L2 编队层（下一轮）

- `MechStationRole`：Default、Attacker、Escort、Cover、Shooter、Bait、Jammer、Scout、CAP。
- **保持风格** `MechFormationStyle`，按任务组给默认值：
  - `Airliner`（防空、预警、后勤）：大间距、松散，到位后低增益保持，不使用 QuickBoost。
  - `Assault`（打击、制空、SEAD）：近间距，到位后带侧向晃动横移，允许 QuickBoost 补位。
- Strike 角色分配：默认按挂载**自动判定**（有对舰或对地重武器的是 Attacker，其余是 Escort）。指挥面板可以**手动强制指定**，手动设置优先于自动判定。
- 目标分发：长机持有待打列表并轮转分配。成员本地另有两条否决规则：更近的友机在打同一目标就让开；追不上就放弃。
- 长机接替：长机失效时按 FormationIndex 和健康度选出新长机，并广播新配置。

## L3 任务层（下一轮）

| 任务 | 阶段 | 角色 |
| --- | --- | --- |
| Strike | 集结→航渡→进入攻击线→攻击→脱离→归队 | Attacker、Escort（自动或手动） |
| Escort | 集结→伴随→拦截→归位 | Escort |
| AirSuperiority | 航渡→扫荡→交战→回扫荡 | 成对的 Lead 和 Wing |
| SEAD | 航渡→诱发→被动定位→发射→脱离 | Shooter、Bait |
| EW | 航渡→占位→干扰→撤离 | Jammer、Escort |
| Recon | 航渡→凝视/扫描→回报→返航 | Scout、Cover |

## 实施步骤

| 步 | 内容 | 状态 |
| --- | --- | --- |
| 0 | `AI/Core` 状态机 + `AI/Pilot` 驾驶原语 | 已完成（Pilot 暂复用现有 `MechAutonomousFlight`） |
| 1 | `MechAiBrain` 接管 `Update()`，拆除 `ShouldYieldCombatToNavigation` 覆盖逻辑 | 已完成 |
| 2 | 新规避时机 + cold/beam + 干扰弹 | 已完成 |
| 3 | 编队层：角色、保持风格、射手让开、放弃追击、长机接替 | 已完成 |
| 4 | 任务阶段机：Strike、Escort 优先 | 下一轮 |
| 5 | 指挥层与难度参数 | 后续 |

## 实现位置

| 内容 | 文件 |
| --- | --- |
| 优先级状态机 | `Scripts/Mech/Runtime/AI/Core/MechAiStateMachine.cs` |
| 单机大脑（感知快照 + 5 个状态） | `Scripts/Mech/Runtime/AI/Unit/MechAiTargetController.Brain.cs` |
| 规避时机判定 | `Scripts/Mech/Runtime/AI/Unit/MechAiEvasionDoctrine.cs` |
| 编队规则（角色、风格、分档、让开、放弃） | `Scripts/Mech/Runtime/AI/Formation/MechFormationDoctrine.cs` |
| 编队接入（槽位保持、角色刷新、交战上报） | `Scripts/Mech/Runtime/MechFleetController.Station.cs` |
| 测试 | `Editor/MechAiBrainTests.cs`、`Editor/MechFormationDoctrineTests.cs` |

实现要点：

- 单机层：每帧先感知一次（威胁、库存、目标、编队导航），状态只负责产出输入帧。编队导航不再事后覆盖交战结果。
- Evade 是唯一标记为"打断"的转移，所以状态机会记住被打断的基础状态。退出 Evade 时重新求值，不恢复可能已经失效的旧目标。
- 编队保持只替换 `MechFlightTask.Formation` 这一分支。Move、返航、任务导航仍走原有导航。
- 风格的间距倍率作用在 `OwnFormationOffset` 和 `TryFormationPoint` 两处，两边保持一致。自定义队形使用绝对偏移，不受风格影响。
- 射手让开：心跳的 `Target` 字段在无任务时上报 AI 当前的交战目标。其他成员用 `MechFleetContactMatcher` 跨观测者关联，命中时给目标打分加 2.5 分作为惩罚，而不是一票否决，所以只有一个目标时仍然会去打。
- 命令目标（有任务时）不参与让开或放弃，由任务层负责。

- **长机接替**（`MechFleetController.Succession.cs`）：`State.Leader` 保留协议权限，实际跟随的是 `ActiveLeader`。回退顺序为：配置长机 → 编队根 → 同编队中存活且序号最小的成员。所有成员用同一份报文得出同一结果，不需要选举。启动后 3 秒内，从未收到心跳的长机不视为丢失。
- **状态机驻留**：只有打断类转移（规避、死亡）可以截断最短驻留时间。基础行为之间的抢占也要等驻留时间到，用来消除实测中 0.02 秒内 E→N→E 的往返。

## 实景评测（2026-11-01）

使用 `MechEnduranceCombatReview.Run -enduranceHeadless 1 -fleetReview 1 -fleetDefenseReview 1`，时长 120 秒。由我直接启动 Unity，Codex（gpt-6.1-terra，中等思考）只读日志写分析，报告在 `E:\UnityDev\tmp\aibuild\review\REPORT.md`。

| 运行 | 内容 | 指定武器 | AI 攻击弹 | 防御拦截 | 未授权 | 结果 |
| --- | --- | --- | --- | --- | --- | --- |
| 基线 rendered-120（10-27，重构前） | — | 1 | 14 | 42 | 0 | PASS |
| runB | 编队层 + 打击 | 1 | 10 | 17 | 0 | PASS |
| runC | + 驻留修复 + 长机接替 | 1 | 7 | 15 | 0 | PASS |
| runD | + 接替启动宽限 | 1 | 6 | 15 | 0 | PASS |

- 防御拦截总数下降不是退化。前 56 秒 runB 拦截 17 次，基线 16 次，而且威胁和认领更多。总数下降是因为两架敌机分别在约 30 秒和 54 秒被击毁，后半程没有威胁了。（Codex 分析结论，因果仍需对照实验确认。）
- 全部运行 0 异常，构建一致性 PASS。进程退出码 2 来自 `coverage=False`：coverage 只有在 full 采集加 HUD 绘制时才可能为真，headless 模式下必然为假，不代表 AI 失败。
- 不加 `-fleetDefenseReview 1` 时，场景默认出生点相距 70–100 km，120 秒内双方无法接敌，验收必然失败（runA）。评审 AI 时必须带这个参数。
- 长机接替实测：在评审脚本的"断链 7 秒"阶段（83–87 秒），两架僚机一致选出同一位代理长机，恢复链路后回到玩家。

## 遥测缺口

- 没有对抗措施（干扰弹）释放事件，无法从评测数据确认 AI 是否放了、有没有效。
- 编队成员没有逐帧世界坐标，无法统计僚机到长机距离的中位数、p90 和振荡。
- 默认场景没有下达打击任务，所以 `strike_phase` 在实景中尚未覆盖。

## 已知遗留

- `MechFleetMissileAITests.SuppressionFamiliesExposePassiveCapabilityAndLongRange`（4 例）在重构前就失败。原因是导弹目录现在把 `effectiveRangeMeters` 写为 0（射程改为计算得出），测试仍断言它大于 0。与 AI 无关，未修改。

## 已确认

- 飞机式大间距编队和机甲突击式近距编队都要做，由任务组决定默认风格。
- Strike 的角色默认自动判定，允许手动强制设置。
- 当前没有半主动（需持续照射）导弹，Crank 暂不实现，只保留接口位置。
