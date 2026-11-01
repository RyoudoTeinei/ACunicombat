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
| 0 | `AI/Core` 状态机 + `AI/Pilot` 驾驶原语 | 本轮 |
| 1 | `MechAiBrain` 接管 `Update()`，拆除 `ShouldYieldCombatToNavigation` 覆盖逻辑 | 本轮 |
| 2 | 新规避时机 + cold/beam + 干扰弹 | 本轮 |
| 3 | 编队层：角色、保持风格、目标分发与让开、长机接替 | 下一轮 |
| 4 | 任务阶段机：Strike、Escort 优先 | 下一轮 |
| 5 | 指挥层与难度参数 | 后续 |

## 已确认

- 飞机式大间距编队和机甲突击式近距编队都要做，由任务组决定默认风格。
- Strike 的角色默认自动判定，允许手动强制设置。
- 当前没有半主动（需持续照射）导弹，Crank 暂不实现，只保留接口位置。
